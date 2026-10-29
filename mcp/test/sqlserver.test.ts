import { describe, expect, it } from "vitest";
import { asLogin, connect as connectSql, type Query } from "../src/sqlserver/connect.js";
import { SQL } from "../src/sqlserver/queries.js";
import { buildSqlServerServer } from "../src/sqlserver/tools.js";
import { config, connect } from "./helpers.js";

const cfg = config({ services: { "order-api": { database: "ResolveAIDemo" } }, sqlserver: { enabled: true } });

/** Answers each fixed statement with canned rows; records what ran. */
function fake(answers: Partial<Record<keyof typeof SQL | "exec", Record<string, unknown>[] | Error>>) {
  const ran: { sql: string; params?: Record<string, unknown> }[] = [];
  const query: Query = async (sql, params) => {
    ran.push({ sql, params });
    const key = sql.startsWith("EXEC ") ? "exec" : (Object.keys(SQL) as (keyof typeof SQL)[]).find((k) => SQL[k] === sql);
    const a = key ? answers[key] : undefined;
    if (a instanceof Error) throw a;
    return a ?? [];
  };
  return { query, ran };
}

describe("sqlserver server", () => {
  it("reports a blocking chain with its head blocker", async () => {
    const { query } = fake({
      headBlockers: [{ session_id: 61, status: "sleeping", open_transaction_count: 1, last_query_text: "UPDATE dbo.Orders SET ReconciledAt = SYSUTCDATETIME()" }],
      blocked: [{ session_id: 72, blocking_session_id: 61, wait_type: "LCK_M_U", wait_ms: 42000, query_text: "UPDATE dbo.Orders SET Status = N'placed'" }],
    });
    const { call } = await connect(buildSqlServerServer(cfg, query));
    const r = await call("db_blocking");
    expect(r.summary).toBe("1 blocked request(s) behind 1 head blocker(s) (session 61); longest wait 42s.");
    expect(r.rows.map((x) => x.role)).toEqual(["head_blocker", "blocked"]);
  });

  it("only touches allow-listed databases, quoted, through sp_executesql", async () => {
    const { query, ran } = fake({
      databaseState: [{ database_name: "ResolveAIDemo", state_desc: "ONLINE", log_reuse_wait_desc: "NOTHING" }],
      exec: [{ file_name: "ResolveAIDemo", type_desc: "ROWS", size_mb: 8, used_mb: 7.5, max_mb: 8 }],
    });
    const { call } = await connect(buildSqlServerServer(cfg, query));
    const ok = await call("db_space", {});
    expect(ok.summary).toMatch(/ResolveAIDemo is ONLINE; log reuse wait: NOTHING; 1 file\(s\), 1 near max size/);
    expect(ran.some((r) => r.sql === "EXEC [ResolveAIDemo].sys.sp_executesql @stmt" && r.params?.stmt === SQL.fileSpace)).toBe(true);
    const bad = await call("db_space", { database: "master]; DROP TABLE x;--" });
    expect(bad.isError).toBe(true);
    expect(bad.summary).toMatch(/not in the allow-list/);
  });

  it("merges error sources and degrades when one is not readable", async () => {
    const { query } = fake({
      systemHealthErrors: new Error("VIEW SERVER STATE permission was denied"),
      resolveaiSessionErrors: [
        { time: new Date("2026-10-27T14:02:10Z"), error_number: 1222, severity: 16, message: "Lock request time out period exceeded." },
        { time: new Date("2026-10-27T14:03:05Z"), error_number: 1222, severity: 16, message: "Lock request time out period exceeded." },
      ],
    });
    const { call } = await connect(buildSqlServerServer(cfg, query));
    const r = await call("db_errors", { from: "2026-10-27T14:00:00Z", to: "2026-10-27T15:00:00Z" });
    expect(r.summary).toBe("2 SQL Server error(s) in the window (most common: 1222 ×2).");
    expect(r.rows[0].time).toBe("2026-10-27T14:03:05.000Z");
  });

  it("says when SQL Agent is missing and turns connection failures into a readable error", async () => {
    const { call } = await connect(buildSqlServerServer(cfg, fake({}).query));
    expect((await call("db_failed_jobs", { from: "2026-10-27T14:00:00Z", to: "2026-10-27T15:00:00Z" })).summary).toBe("SQL Agent is not available on this server.");
    const noCreds = connectSql({ ...cfg.sqlserver, driver: "tedious" }, {});
    await expect(noCreds("SELECT 1")).rejects.toThrow(/MSSQL_USER and MSSQL_PASSWORD/);
  });

  it("wraps statements so they run as the reader login and always revert", () => {
    const wrapped = asLogin("resolveai_reader", SQL.deadlocks);
    expect(wrapped.startsWith("EXECUTE AS LOGIN = N'resolveai_reader';")).toBe(true);
    expect(wrapped).toMatch(/BEGIN CATCH\n {2}REVERT;\n {2}THROW;/);
    expect(wrapped.trim().endsWith("REVERT;")).toBe(true);
  });
});

// Opt-in check against a real local SQL Server after running demo-systems/sqlserver/setup.sql.
describe.skipIf(process.env.MSSQL_TEST !== "1")("sqlserver integration", () => {
  it("runs every tool against the local server as resolveai_reader", async () => {
    const live = config({ services: { "order-api": { database: "ResolveAIDemo" } },
      sqlserver: { enabled: true, driver: "msnodesqlv8", execute_as: "resolveai_reader" } });
    const { call } = await connect(buildSqlServerServer(live));
    const now = new Date();
    const from = new Date(now.getTime() - 3600_000).toISOString();
    for (const [tool, args] of [["db_health", {}], ["db_blocking", {}], ["db_long_queries", { min_seconds: 5 }], ["db_space", {}],
      ["db_errors", { from }], ["db_deadlocks", { from }], ["db_failed_jobs", { from }]] as const) {
      const r = await call(tool, args);
      expect(r.isError, `${tool}: ${r.summary}`).toBe(false);
    }
  }, 60_000);
});
