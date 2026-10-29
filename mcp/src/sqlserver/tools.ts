import { z } from "zod";
import type { McpConfig } from "../common/config.js";
import { ok, ToolError } from "../common/result.js";
import { createServer, readOnlyTool } from "../common/server.js";
import { isoTime, parseWindow } from "../common/window.js";
import { connect, type Query } from "./connect.js";
import { SQL } from "./queries.js";

const iso = (v: unknown) => (v instanceof Date ? v.toISOString() : v);
/** Dates become ISO strings so results serialise the same way from either driver. */
const clean = (rows: Record<string, unknown>[]) => rows.map((r) => Object.fromEntries(Object.entries(r).map(([k, v]) => [k, iso(v)])));

/** "Permission denied" from SQL Server, reworded so the agent knows the check is unavailable, not empty. */
async function attempt(q: Query, text: string, params?: Parameters<Query>[1]) {
  try {
    return { rows: clean(await q(text, params)), error: null as string | null };
  } catch (err) {
    if (err instanceof ToolError) throw err;
    return { rows: [], error: (err as Error).message };
  }
}

export function buildSqlServerServer(cfg: McpConfig, query: Query = connect(cfg.sqlserver)) {
  const server = createServer("resolveai-sqlserver");
  const databases = [...new Set(Object.values(cfg.services).map((s) => s.database).filter((d): d is string => !!d))];
  const pickDb = (db?: string) => {
    const name = db ?? databases[0];
    if (!name) throw new ToolError("No databases are configured for any service.");
    if (!databases.includes(name)) throw new ToolError(`Database "${name}" is not in the allow-list (${databases.join(", ")}).`);
    return name;
  };
  const source = `sqlserver/${cfg.sqlserver.server}`;
  const database = z.string().optional().describe(`One of: ${databases.join(", ") || "(none configured)"}`);

  readOnlyTool(server, "db_health",
    "Point-in-time SQL Server health: sessions and blocked requests per database, the top waits since the server started, and memory/CPU.",
    {},
    async () => {
      const [sessions, waits, info] = await Promise.all([query(SQL.sessionsByDatabase), query(SQL.topWaits), query(SQL.sysInfo)]);
      const i = clean(info)[0] ?? {};
      const blocked = sessions.reduce((n, s) => n + Number(s.blocked_requests ?? 0), 0);
      const rows = [
        { section: "server", ...i },
        ...clean(sessions).map((s) => ({ section: "sessions", ...s })),
        ...clean(waits).map((w) => ({ section: "top_wait_since_start", ...w })),
      ];
      return ok(`SQL Server ${i.version ?? ""} at ${new Date().toISOString()}: ${sessions.reduce((n, s) => n + Number(s.sessions ?? 0), 0)} user session(s), ` +
        `${blocked} blocked request(s); top wait since start: ${waits[0]?.wait_type ?? "none"}.`, rows, source);
    });

  readOnlyTool(server, "db_blocking",
    "Point-in-time blocking chains: which sessions are waiting on which, for how long, on what resource, and what the head blocker last ran.",
    {},
    async () => {
      const [blocked, heads] = await Promise.all([query(SQL.blocked), query(SQL.headBlockers)]);
      const rows = [
        ...clean(heads).map((h) => ({ role: "head_blocker", ...h })),
        ...clean(blocked).map((b) => ({ role: "blocked", ...b })),
      ];
      const longest = blocked.reduce((m, b) => Math.max(m, Number(b.wait_ms ?? 0)), 0);
      return ok(blocked.length
        ? `${blocked.length} blocked request(s) behind ${heads.length} head blocker(s) (session ${heads.map((h) => h.session_id).join(", ")}); longest wait ${Math.round(longest / 1000)}s.`
        : `No blocking right now (${new Date().toISOString()}).`, rows, source);
    });

  readOnlyTool(server, "db_long_queries",
    "Requests that have been running for at least min_seconds right now, longest first, with their query text.",
    { min_seconds: z.number().int().min(1).max(3600).default(30) },
    async ({ min_seconds }) => {
      const rows = clean(await query(SQL.longRequests, { min_ms: min_seconds * 1000 }));
      return ok(rows.length ? `${rows.length} request(s) running ${min_seconds}s or longer; longest ${rows[0].elapsed_s}s.`
        : `No requests running ${min_seconds}s or longer.`, rows, source);
    });

  readOnlyTool(server, "db_deadlocks",
    "Deadlocks recorded by the system_health session between from and to: each process involved, its lock and query, and which one was the victim.",
    { from: isoTime, to: isoTime.optional() },
    async ({ from, to }) => {
      const w = parseWindow(from, to);
      const r = await attempt(query, SQL.deadlocks, { from: w.from, to: w.to });
      if (r.error) throw new ToolError(`Cannot read deadlock history: ${r.error}`);
      const events = new Set(r.rows.map((x) => x.time)).size;
      return ok(events ? `${events} deadlock(s) between ${w.from.toISOString()} and ${w.to.toISOString()}.` : "No deadlocks recorded in that window.", r.rows, source);
    });

  readOnlyTool(server, "db_errors",
    "SQL Server errors between from and to: severe errors from system_health, plus severity 11+ errors (lock timeouts, conversion failures) " +
    "if the optional resolveai_errors session is running. Optionally filter by a phrase in the message.",
    { from: isoTime, to: isoTime.optional(), phrase: z.string().max(200).optional() },
    async ({ from, to, phrase }) => {
      const w = parseWindow(from, to);
      const [health, session] = await Promise.all([
        attempt(query, SQL.systemHealthErrors, { from: w.from, to: w.to }),
        attempt(query, SQL.resolveaiSessionErrors, { from: w.from, to: w.to }),
      ]);
      if (health.error && session.error) throw new ToolError(`Cannot read SQL Server error history: ${health.error}`);
      const needle = phrase?.toLowerCase();
      const rows = [...health.rows, ...session.rows]
        .filter((r) => !needle || String(r.message ?? "").toLowerCase().includes(needle))
        .sort((a, b) => String(b.time).localeCompare(String(a.time)));
      const byNumber = new Map<unknown, number>();
      for (const r of rows) byNumber.set(r.error_number, (byNumber.get(r.error_number) ?? 0) + 1);
      const top = [...byNumber].sort((a, b) => b[1] - a[1]).slice(0, 3).map(([n, c]) => `${n} ×${c}`).join(", ");
      return ok(rows.length ? `${rows.length} SQL Server error(s) in the window (most common: ${top}).` : "No SQL Server errors recorded in that window.",
        rows, source);
    });

  readOnlyTool(server, "db_space",
    "Data and log file sizes, used space and growth for one database, plus its state and why its log cannot be reused.",
    { database },
    async ({ database: db }) => {
      const name = pickDb(db);
      const quoted = `[${name.replace(/]/g, "]]")}]`;
      const [state, files] = await Promise.all([
        query(SQL.databaseState, { db: name }),
        query(`EXEC ${quoted}.sys.sp_executesql @stmt`, { stmt: SQL.fileSpace }),
      ]);
      const s = state[0];
      if (!s) throw new ToolError(`Database ${name} does not exist on ${cfg.sqlserver.server}.`);
      const rows = [{ section: "database", ...clean(state)[0] }, ...clean(files).map((f) => ({ section: "file", ...f }))];
      const full = files.filter((f) => f.max_mb != null && Number(f.used_mb) >= 0.9 * Number(f.max_mb));
      return ok(`${name} is ${s.state_desc}; log reuse wait: ${s.log_reuse_wait_desc}; ${files.length} file(s)${full.length ? `, ${full.length} near max size` : ""}.`, rows, source);
    });

  readOnlyTool(server, "db_failed_jobs",
    "SQL Agent job steps that failed between from and to.",
    { from: isoTime, to: isoTime.optional() },
    async ({ from, to }) => {
      const w = parseWindow(from, to);
      const status = await attempt(query, SQL.agentStatus);
      const agent = status.rows[0]?.status_desc ?? null;
      if (!agent) return ok("SQL Agent is not available on this server.", [], source);
      const r = await attempt(query, SQL.failedJobs, { from: w.from, to: w.to });
      if (r.error) throw new ToolError(`Cannot read SQL Agent job history: ${r.error}`);
      return ok(`${r.rows.length} failed job step(s) in the window; SQL Agent is ${String(agent).toLowerCase()}.`, r.rows, source);
    });

  return server;
}
