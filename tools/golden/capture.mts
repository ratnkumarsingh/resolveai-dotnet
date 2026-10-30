/**
 * Records what the Node implementation does, so the .NET port can be compared against it.
 * Everything runs in-process on in-memory databases; nothing touches data/resolveai.db.
 *
 *   npx tsx tools/golden/capture.mts
 *
 * Values that legitimately differ between runs are normalised by the same rules in Normalize.cs.
 */
import { mkdirSync, rmSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import {
  buildDeploysServer, buildFilesServer, buildMetricsServer, buildSqlServerServer, loadMcpConfig, type Query, REPO_ROOT,
} from "@resolveai/mcp";
import { demoSystems, makeApp, runInvestigation } from "../../api/test/helpers.js";
import { RecordedProvider } from "../../api/src/llm/recorded.js";
import { getInvestigationRow } from "../../api/src/services/investigations.js";
import { SQL } from "../../mcp/src/sqlserver/queries.js";

const OUT = join(dirname(fileURLToPath(import.meta.url)), "../../tests/ResolveAI.Tests/Golden");
const STARTED = Date.now();
const NEAR_NOW_MS = 6 * 3600_000;

const RANDOM_UUID = /[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}/g;
const ISO_TIME = /\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?Z/g;
const VOLATILE_KEYS = new Set(["duration_ms", "modified", "size_bytes", "characters", "cache_read_input_tokens"]);

function normaliseText(s: string) {
  return s
    .replace(RANDOM_UUID, "<uuid>")
    .replace(/modified \d{4}-\d{2}-\d{2}T[\d:.]+Z/g, "modified <mtime>")
    .replace(/\(\d[\d,]* bytes/g, "(<n> bytes")
    .replace(ISO_TIME, (t) => (Math.abs(Date.parse(t) - STARTED) < NEAR_NOW_MS ? "<now>" : t));
}

function normalise(v: unknown): unknown {
  if (typeof v === "string") return normaliseText(v);
  if (Array.isArray(v)) return v.map(normalise);
  if (v && typeof v === "object") {
    return Object.fromEntries(Object.entries(v).map(([k, x]) => [k, VOLATILE_KEYS.has(k) ? "<volatile>" : normalise(x)]));
  }
  return v;
}

function save(name: string, value: unknown) {
  writeFileSync(join(OUT, `${name}.json`), `${JSON.stringify(normalise(value), null, 2)}\n`);
}

const INCIDENTS = ["INC-1039", "INC-1042", "INC-1043", "INC-1044", "INC-1045", "INC-1046"];
const WINDOW = { from: "2026-10-27T09:30:00Z", to: "2026-10-27T18:30:00Z" };

const cfg = loadMcpConfig(join(REPO_ROOT, "mcp.config.example.json"));
const sqlCfg = { ...cfg, services: { ...cfg.services, "order-api": { ...cfg.services["order-api"], database: "ResolveAIDemo" } } };

const BLOCKED = [{ session_id: 72, blocking_session_id: 61, database_name: "ResolveAIDemo", wait_type: "LCK_M_U", wait_ms: 42000, wait_resource: "OBJECT: 5:1:0", status: "suspended", command: "UPDATE", query_text: "UPDATE dbo.Orders SET Status = N'placed' WHERE OrderId = 1", program_name: "SQLCMD" }];
const HEADS = [{ session_id: 61, status: "sleeping", open_transaction_count: 1, last_request_end_time: new Date("2026-10-27T17:01:00Z"), program_name: "SQLCMD", last_query_text: "UPDATE dbo.Orders SET ReconciledAt = SYSUTCDATETIME() WHERE Status = N'open'" }];

/** A fake SQL Server: canned rows per fixed statement, so tool logic is pinned without a real server. */
const fakeSql: Query = async (sql) => {
  if (sql === SQL.blocked) return BLOCKED;
  if (sql === SQL.headBlockers) return HEADS;
  if (sql === SQL.sessionsByDatabase) return [{ database_name: "ResolveAIDemo", sessions: 3, active_requests: 2, blocked_requests: 1 }];
  if (sql === SQL.topWaits) return [{ wait_type: "LCK_M_U", waiting_tasks_count: 4, wait_time_ms: 90000 }];
  if (sql === SQL.sysInfo) return [{ cpu_count: 8, physical_memory_mb: 16000, committed_mb: 900, target_mb: 12000, sqlserver_start_time: new Date("2026-10-20T00:00:00Z"), version: "16.0.1210.3", edition: "Developer Edition" }];
  if (sql === SQL.longRequests) return [{ session_id: 72, database_name: "ResolveAIDemo", status: "suspended", command: "UPDATE", elapsed_s: 42, cpu_ms: 1, logical_reads: 3, wait_type: "LCK_M_U", query_text: "UPDATE dbo.Orders", program_name: "SQLCMD" }];
  if (sql === SQL.deadlocks) return [{ time: new Date("2026-10-27T17:03:00Z"), process: "p1", spid: 72, database_name: "ResolveAIDemo", wait_resource: "KEY: 5:1", lock_mode: "U", query_text: "UPDATE dbo.Orders", victim: 1 }];
  if (sql === SQL.systemHealthErrors) return [{ time: new Date("2026-10-27T17:01:30Z"), error_number: 17883, severity: 16, message: "Scheduler issue", captured_by: "system_health" }];
  if (sql === SQL.resolveaiSessionErrors) return [{ time: new Date("2026-10-27T17:02:10Z"), error_number: 1222, severity: 16, message: "Lock request time out period exceeded.", database_name: "ResolveAIDemo", captured_by: "resolveai_errors" }];
  if (sql === SQL.databaseState) return [{ database_name: "ResolveAIDemo", state_desc: "ONLINE", recovery_model_desc: "SIMPLE", log_reuse_wait_desc: "NOTHING", is_read_only: false }];
  if (sql.startsWith("EXEC ")) return [{ file_name: "ResolveAIDemo", type_desc: "ROWS", size_mb: 8, used_mb: 7.5, max_mb: 8, growth: "10 MB" }];
  if (sql === SQL.agentStatus) return [{ status_desc: "Stopped" }];
  if (sql === SQL.failedJobs) return [{ job_name: "Nightly", step_id: 1, step_name: "Run", run_at: new Date("2026-10-27T17:00:00Z"), message: "Failed" }];
  return [];
};

async function clientFor(server: McpServer) {
  const [a, b] = InMemoryTransport.createLinkedPair();
  await server.connect(a);
  const client = new Client({ name: "capture", version: "0" });
  await client.connect(b);
  return client;
}

const SERVERS: Record<string, () => McpServer> = {
  sqlserver: () => buildSqlServerServer(sqlCfg, fakeSql),
  files: () => buildFilesServer(cfg),
  deploys: () => buildDeploysServer(cfg),
  metrics: () => buildMetricsServer(cfg),
};

/** Tool cases: `protocol` marks input that fails schema validation, where only the failure is compared, not the wording. */
const TOOL_CASES: { server: string; tool: string; args: Record<string, unknown>; protocol?: boolean }[] = [
  { server: "sqlserver", tool: "db_health", args: {} },
  { server: "sqlserver", tool: "db_blocking", args: {} },
  { server: "sqlserver", tool: "db_long_queries", args: { min_seconds: 5 } },
  { server: "sqlserver", tool: "db_long_queries", args: { min_seconds: 0 }, protocol: true },
  { server: "sqlserver", tool: "db_long_queries", args: { min_seconds: 1.5 }, protocol: true },
  { server: "sqlserver", tool: "db_deadlocks", args: { from: WINDOW.from, to: WINDOW.to } },
  { server: "sqlserver", tool: "db_errors", args: { from: WINDOW.from, to: WINDOW.to } },
  { server: "sqlserver", tool: "db_errors", args: { from: WINDOW.from, to: WINDOW.to, phrase: "lock" } },
  { server: "sqlserver", tool: "db_space", args: {} },
  { server: "sqlserver", tool: "db_space", args: { database: "master]; DROP TABLE x;--" } },
  { server: "sqlserver", tool: "db_failed_jobs", args: { from: WINDOW.from, to: WINDOW.to } },
  { server: "sqlserver", tool: "db_errors", args: { from: "2026-10-01T00:00:00Z", to: "2026-10-27T00:00:00Z" } },
  { server: "sqlserver", tool: "db_errors", args: { from: "not a date", to: WINDOW.to } },
  { server: "sqlserver", tool: "db_errors", args: { from: WINDOW.to, to: WINDOW.from } },
  { server: "files", tool: "files_search", args: { root: "payment-api", phrase: "ERROR" } },
  { server: "files", tool: "files_search", args: { root: "payment-api", phrase: "error", from: "2026-10-27T10:04:00Z", to: "2026-10-27T10:05:00Z", max_matches: 2 } },
  { server: "files", tool: "files_search", args: { root: "payment-api", phrase: "x" }, protocol: true },
  { server: "files", tool: "files_search", args: { root: "..", phrase: "ERROR" } },
  { server: "files", tool: "files_tail", args: { path: "payment-api/app.log", lines: 3 } },
  { server: "files", tool: "files_tail", args: { path: "document-service/mounts.txt", lines: 10 } },
  { server: "files", tool: "files_tail", args: { path: "payment-api" } },
  { server: "files", tool: "files_tail", args: { path: "payment-api/missing.log" } },
  { server: "files", tool: "files_tail", args: { path: "../../README.md" } },
  { server: "files", tool: "files_check", args: { path: "payment-api/missing.log" } },
  { server: "files", tool: "files_recent_changes", args: { root: "payment-api", since: "not a date" } },
  { server: "deploys", tool: "deploys_recent", args: { service: "payment-api", from: "2026-10-26T12:00:00Z", to: "2026-10-27T11:00:00Z" } },
  { server: "deploys", tool: "deploys_recent", args: { from: "2026-10-27T00:00:00Z", to: "2026-10-27T20:00:00Z" } },
  { server: "deploys", tool: "deploys_recent", args: { service: "nope", from: WINDOW.from, to: WINDOW.to } },
  { server: "deploys", tool: "deploys_change_summary", args: { service: "payment-api", commit: "7d03b1f" } },
  { server: "deploys", tool: "deploys_change_summary", args: { service: "payment-api", commit: "--output=/tmp/x" }, protocol: true },
  { server: "deploys", tool: "deploys_change_summary", args: { service: "payment-api", commit: "abcdef1" } },
  { server: "metrics", tool: "metrics_list", args: { service: "payment-api" } },
  { server: "metrics", tool: "metrics_series", args: { service: "payment-api", name: "http 500 rate", from: "2026-10-27T09:30:00Z", to: "2026-10-27T11:30:00Z" } },
  { server: "metrics", tool: "metrics_series", args: { service: "payment-api", name: "nope", from: WINDOW.from, to: WINDOW.to } },
  { server: "metrics", tool: "metrics_series", args: { service: "payment-api", name: "HTTP 500 rate", from: "2020-01-01T00:00:00Z", to: "2020-01-01T01:00:00Z" } },
  { server: "metrics", tool: "metrics_series", args: { service: "../etc", name: "x", from: WINDOW.from, to: WINDOW.to } },
  { server: "metrics", tool: "metrics_list", args: { service: "unknown-service" } },
];

async function captureTools() {
  const lists: Record<string, unknown> = {};
  for (const [name, build] of Object.entries(SERVERS)) {
    const client = await clientFor(build());
    lists[name] = (await client.listTools()).tools;
    await client.close();
  }
  save("tools-list", lists);

  const results = [];
  for (const c of TOOL_CASES) {
    const client = await clientFor(SERVERS[c.server]());
    let outcome: unknown;
    try {
      const res = await client.callTool({ name: c.tool, arguments: c.args });
      const text = (res.content as { text: string }[]).map((x) => x.text).join("");
      let payload: unknown;
      try { payload = JSON.parse(text); } catch { payload = text; }
      outcome = c.protocol ? { rejected: !!res.isError } : { is_error: !!res.isError, payload };
    } catch {
      outcome = { rejected: true };
    }
    results.push({ ...c, outcome });
    await client.close();
  }
  save("tool-results", results);
}

async function captureRoutes() {
  const { app, db } = makeApp(new RecordedProvider(0), {}, undefined, await demoSystems());
  const get = async (url: string) => {
    const r = await app.inject(url);
    return { url, status: r.statusCode, body: r.json() };
  };

  const dump: Record<string, unknown> = {};
  for (const table of ["incidents", "log_events", "metric_snapshots", "timeline_events", "deployments", "knowledge_documents", "knowledge_fts", "historical_incidents", "historical_fts"]) {
    dump[table] = db.prepare(`SELECT * FROM ${table} ORDER BY rowid`).all();
  }
  save("seed", dump);

  const routes = [
    await get("/api/config"),
    await get("/api/systems"),
    await get("/api/incidents"),
    await get("/api/incidents?severity=SEV-1"),
    await get("/api/incidents?status=resolved"),
    await get("/api/incidents?service=payment-api"),
    await get("/api/incidents?q=pdf"),
    await get("/api/incidents?review=pending"),
    await get("/api/incidents?page=2&page_size=2"),
    await get("/api/incidents/INC-9999"),
    await get("/api/investigations/none"),
    await get("/api/rca-drafts/none"),
    await get("/api/knowledge/none"),
  ];
  for (const key of INCIDENTS) {
    routes.push(await get(`/api/incidents/${key}`), await get(`/api/incidents/${key}/evidence`));
  }
  save("routes", routes);
}

async function captureInvestigations() {
  const runs = [];
  const variants = [
    ...INCIDENTS.map((key) => ({ name: key, key, sql: undefined as Query | undefined })),
    { name: "INC-1046-sql-on", key: "INC-1046", sql: fakeSql },
  ];
  for (const v of variants) {
    const { app, db } = makeApp(new RecordedProvider(0), {}, undefined, await demoSystems(v.sql));
    const inv = await runInvestigation(app, v.key);
    const packet = JSON.parse(getInvestigationRow(db, inv.id)!.evidence_packet ?? "null");

    let rca = null;
    if (inv.status === "completed") {
      const created = await app.inject({ method: "POST", url: `/api/incidents/${v.key}/rca-drafts`, payload: { investigation_id: inv.id } });
      const body = created.json();
      const md = await app.inject(`/api/rca-drafts/${body.rca_draft_id}/export.md`);
      rca = { status: created.statusCode, rca: body.rca, markdown: md.body, content_disposition: md.headers["content-disposition"] };
    }
    const after = (await app.inject("/api/incidents")).json().items.find((i: { key: string }) => i.key === v.key);
    runs.push({ name: v.name, investigation: inv, packet, rca, review_state_after: after.review_state });
    await app.close();
  }
  save("investigations", runs);
}

rmSync(OUT, { recursive: true, force: true });
mkdirSync(OUT, { recursive: true });
await captureTools();
await captureRoutes();
await captureInvestigations();
console.log(`Golden files written to ${OUT}`);
process.exit(0);
