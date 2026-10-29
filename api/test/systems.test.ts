import { join } from "node:path";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { loadMcpConfig, REPO_ROOT } from "@resolveai/mcp";
import { afterAll, describe, expect, it } from "vitest";
import { z } from "zod";
import { RecordedProvider } from "../src/llm/recorded.js";
import { McpHub } from "../src/mcp/hub.js";
import { getInvestigationRow } from "../src/services/investigations.js";
import { demoSystems, inProcessHub, makeApp, output, runInvestigation, ScriptedProvider } from "./helpers.js";

type Call = { ref: string; phase: string; system: string; tool: string; ok: boolean; summary: string; rows: Record<string, unknown>[] };
type Stage = { stage: string; details: Record<string, unknown> };

const BLOCKING = {
  head: [{ session_id: 61, status: "sleeping", open_transaction_count: 1, last_query_text: "UPDATE dbo.Orders SET ReconciledAt = SYSUTCDATETIME() WHERE Status = N'open'" }],
  blocked: [{ session_id: 72, blocking_session_id: 61, wait_type: "LCK_M_U", wait_ms: 42000, query_text: "UPDATE dbo.Orders SET Status = N'placed' WHERE OrderId = 1" }],
};

/** Fake SQL Server: a live blocking chain on dbo.Orders, nothing else. */
const blockingDb = async (sql: string) =>
  sql.includes("blocking_session_id <> 0)\n       AND") ? BLOCKING.head : sql.includes("WHERE r.blocking_session_id <> 0") ? BLOCKING.blocked : [];

describe("system checks during an investigation", () => {
  it("runs the baseline checks and recorded follow-ups, and cites their results", async () => {
    const { app } = makeApp(new RecordedProvider(0), {}, undefined, await demoSystems());
    const inv = await runInvestigation(app, "INC-1045");
    expect(inv.status).toBe("completed");

    const calls = inv.tool_calls as Call[];
    expect(calls.filter((c) => c.phase === "baseline").map((c) => c.tool)).toEqual([
      "files_search", "files_search", "deploys_recent", "metrics_series", "metrics_series",
    ]);
    expect(calls.filter((c) => c.phase === "follow_up").map((c) => c.tool)).toEqual(["files_tail", "files_tail", "deploys_change_summary"]);
    expect(calls.every((c) => c.ok)).toBe(true);
    expect(calls.map((c) => c.ref)).toEqual(calls.map((_, i) => `mcp:${i + 1}`));
    expect(calls[0].summary).toMatch(/line\(s\) containing "ERROR"/);

    const top = inv.hypotheses[0];
    expect(top.confidence).toBe("high");
    const cited = top.evidence.map((e: { ref: string }) => e.ref);
    expect(cited).toEqual(expect.arrayContaining(["mcp:1", "mcp:6", "mcp:7", "mcp:8"]));
    expect(top.evidence.find((e: { ref: string }) => e.ref === "mcp:6").label).toBe("files: files_tail");
    expect(inv.validation_issues.filter((i: string) => i.startsWith("Removed citation"))).toEqual([]);

    const stages = (inv.trace as Stage[]).map((t) => t.stage);
    expect(stages).toEqual(["Gather evidence", "Search incident history", "Baseline checks (MCP)", "Build evidence packet",
      "Follow-up checks (recorded)", "Analyse (recorded)", "Validate output"]);
    const baseline = (inv.trace as Stage[]).find((t) => t.stage === "Baseline checks (MCP)")!;
    expect(baseline.details).toMatchObject({ calls: 5, failed: 0, files: 2, deploys: 1, metrics: 2, systems_off: "database" });
  });

  it("redacts secrets in tool output before the model sees them, but keeps the raw rows for the engineer", async () => {
    const { app, db } = makeApp(new RecordedProvider(0), {}, undefined, await demoSystems());
    const inv = await runInvestigation(app, "INC-1042");
    const errors = (inv.tool_calls as Call[]).find((c) => c.tool === "files_search")!;
    expect(JSON.stringify(errors.rows)).toContain("sk_live_51FAKE");
    const packet = getInvestigationRow(db, inv.id)!.evidence_packet!;
    expect(packet).not.toContain("sk_live_51FAKE");
    expect(packet).toContain("mcp:1");
    // The recorded INC-1042 analysis picks up the follow-up checks that confirm pool.max=20.
    expect(inv.hypotheses[0].evidence.map((e: { label: string }) => e.label)).toEqual(expect.arrayContaining(["deploys: deploys_change_summary", "files: files_tail"]));
  });

  it("uses a live SQL Server check when the database system is on", async () => {
    const { app } = makeApp(new RecordedProvider(0), {}, undefined, await demoSystems(blockingDb));
    const inv = await runInvestigation(app, "INC-1046");
    const calls = inv.tool_calls as Call[];
    expect(calls.slice(0, 4).map((c) => c.tool)).toEqual(["db_health", "db_blocking", "db_errors", "db_deadlocks"]);
    const blocking = calls.find((c) => c.tool === "db_blocking")!;
    expect(blocking.summary).toBe("1 blocked request(s) behind 1 head blocker(s) (session 61); longest wait 42s.");
    expect(inv.hypotheses[0].confidence).toBe("high");
    expect(inv.hypotheses[0].evidence.map((e: { ref: string }) => e.ref)).toContain(blocking.ref);
    expect(inv.summary).toMatch(/live check of SQL Server/);
  });

  it("falls back to log evidence when the database system is off", async () => {
    const { app } = makeApp(new RecordedProvider(0), {}, undefined, await demoSystems());
    const inv = await runInvestigation(app, "INC-1046");
    expect((inv.tool_calls as Call[]).some((c) => c.system === "database")).toBe(false);
    expect(inv.hypotheses[0].confidence).toBe("medium");
    expect(inv.limitations.join(" ")).toMatch(/SQL Server was not checked live/);
  });

  it("enforces the follow-up limit however many calls the model tries", async () => {
    const replies: string[] = [];
    const greedy = new ScriptedProvider(
      (p) => output({ hypotheses: [{ title: "x", explanation: "x", confidence: "low", evidence_ids: [p.items[0].id], counter_evidence: [], unknowns: [], recommended_checks: [] }] }),
      async (_packet, tools, call) => {
        expect(tools.map((t) => t.name)).toContain("files_tail");
        for (let i = 0; i < 10; i++) replies.push(await call("files_tail", { path: "payment-api/app.log", lines: 5 }));
        return {};
      },
    );
    const { app } = makeApp(greedy, {}, undefined, await demoSystems(undefined, 3));
    const inv = await runInvestigation(app, "INC-1042");
    expect((inv.tool_calls as Call[]).filter((c) => c.phase === "follow_up")).toHaveLength(3);
    expect(replies.slice(0, 3).every((r) => /^\[mcp:\d+\] Last 5 line\(s\)/.test(r))).toBe(true);
    expect(replies.slice(3).every((r) => r.startsWith("Limit reached"))).toBe(true);
    const stage = (inv.trace as Stage[]).find((t) => t.stage === "Follow-up checks (Claude)")!;
    expect(stage.details).toMatchObject({ calls: 3, limit: 3, refused: 7 });
  });

  it("keeps going when follow-ups fail", async () => {
    const broken = new ScriptedProvider(() => output({}), async () => {
      throw new Error("model unavailable");
    });
    const { app } = makeApp(broken, {}, undefined, await demoSystems());
    const inv = await runInvestigation(app, "INC-1042");
    expect(inv.status).toBe("completed");
    expect(inv.validation_issues).toContain("Follow-up checks stopped early: model unavailable");
  });

  it("lists systems with their status and tools", async () => {
    const { app } = makeApp(new RecordedProvider(0), {}, undefined, await demoSystems());
    const res = (await app.inject("/api/systems")).json();
    expect(res.systems.map((s: { system: string; status: string }) => [s.system, s.status])).toEqual([
      ["database", "disabled"], ["files", "connected"], ["deploys", "connected"], ["metrics", "connected"],
    ]);
    expect(res.systems[1].tools.map((t: { name: string }) => t.name)).toContain("files_search");
    expect((await app.inject("/api/config")).json().max_follow_up_calls).toBe(8);
  });
});

describe("MCP hub", () => {
  const def = { system: "files" as const, server: "test", label: "Test" };

  it("ignores tools that are not marked read-only and times out a hung tool", async () => {
    const hub = await inProcessHub([{
      def,
      build: () => {
        const s = new McpServer({ name: "t", version: "0" });
        s.registerTool("safe_check", { inputSchema: {}, annotations: { readOnlyHint: true } }, async () => ({ content: [{ type: "text", text: '{"summary":"fine","rows":[]}' }] }));
        s.registerTool("delete_everything", { inputSchema: { path: z.string() } }, async () => ({ content: [{ type: "text", text: "deleted" }] }));
        s.registerTool("hang", { inputSchema: {}, annotations: { readOnlyHint: true } }, () => new Promise(() => undefined));
        return s;
      },
    }], 300);
    expect(hub.tools().map((t) => t.name)).toEqual(["safe_check", "hang"]);
    expect(hub.status()[0].detail).toBe("1 tool(s) ignored: not marked read-only.");
    expect(await hub.call("delete_everything", { path: "/" })).toMatchObject({ ok: false, summary: 'No connected system offers the tool "delete_everything".' });
    expect(await hub.call("safe_check", {})).toMatchObject({ ok: true, summary: "fine" });
    expect(await hub.call("hang", {})).toMatchObject({ ok: false, summary: "hang failed: hang timed out after 0.3s" });
  });

  describe("stdio", () => {
    let hub: McpHub;
    afterAll(() => hub?.close());

    it("starts the real servers as child processes with the config passed through the environment", async () => {
      const path = join(REPO_ROOT, "mcp.config.example.json");
      hub = await McpHub.start(loadMcpConfig(path), { ...process.env, RESOLVEAI_MCP_CONFIG: path });
      expect(hub.status().map((s) => s.status)).toEqual(["disabled", "connected", "connected", "connected"]);
      const r = await hub.call("files_search", { root: "payment-api", phrase: "pool exhausted" });
      expect(r).toMatchObject({ ok: true });
      expect(r.rows[0]).toMatchObject({ file: "logs/payment-api/app.log", line: 5 });
    }, 60_000);
  });
});
