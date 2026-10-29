import { join } from "node:path";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import {
  buildDeploysServer, buildFilesServer, buildMetricsServer, buildSqlServerServer, loadMcpConfig, type Query, REPO_ROOT, SERVER_DEFS,
} from "@resolveai/mcp";
import type { InvestigationOutput, RcaOutput } from "@resolveai/shared";
import { type AppDeps, buildApp } from "../src/app.js";
import { McpHub, type SystemDef } from "../src/mcp/hub.js";
import type { Systems } from "../src/services/investigations.js";
import { openDb } from "../src/db/index.js";
import { seedDemoData } from "../src/db/seed.js";
import type { EvidencePacket } from "../src/pipeline/packet.js";
import type { LlmProvider, LlmResult, RcaInput } from "../src/llm/provider.js";
import { RecordedProvider } from "../src/llm/recorded.js";

/**
 * Builds an app on a fresh seeded in-memory DB. A live `llm` (such as ScriptedProvider) becomes the
 * default mode, matching LLM_MODE=live. Pass `live` to keep recorded as default with live on request.
 */
export function makeApp(llm: LlmProvider = new RecordedProvider(0), config: Partial<AppDeps["config"]> = {}, live?: LlmProvider, systems?: Systems) {
  const db = openDb(":memory:");
  seedDemoData(db);
  const providers = llm.mode === "live" ? { recorded: new RecordedProvider(0), live: llm } : { recorded: llm, live };
  const app = buildApp({
    db,
    providers,
    systems,
    config: {
      demoMode: true, model: "test-model", llmMode: llm.mode, liveAccessCode: undefined, liveDailyLimit: 1000, ...config,
    },
  });
  return { app, db };
}

/** Connects a hub to MCP servers built in-process (no child processes). */
export function inProcessHub(servers: { def: SystemDef; build: () => McpServer; enabled?: boolean }[], callTimeoutMs?: number) {
  return McpHub.connect(servers.map((s) => ({
    def: s.def,
    enabled: s.enabled ?? true,
    transport: () => {
      const [serverSide, clientSide] = InMemoryTransport.createLinkedPair();
      void s.build().connect(serverSide);
      return clientSide;
    },
  })), { callTimeoutMs });
}

/**
 * The four demo systems from mcp.config.example.json, in-process. SQL Server answers from `sqlQuery`
 * (a fake) when given, and is otherwise off, as it is by default.
 */
export async function demoSystems(sqlQuery?: Query, maxFollowUps = 8): Promise<Systems> {
  const cfg = loadMcpConfig(join(REPO_ROOT, "mcp.config.example.json"));
  const def = (server: string) => SERVER_DEFS.find((d) => d.server === server)!;
  const hub = await inProcessHub([
    { def: def("sqlserver"), build: () => buildSqlServerServer(cfg, sqlQuery), enabled: !!sqlQuery },
    { def: def("files"), build: () => buildFilesServer(cfg) },
    { def: def("deploys"), build: () => buildDeploysServer(cfg) },
    { def: def("metrics"), build: () => buildMetricsServer(cfg) },
  ]);
  return { hub, config: cfg, maxFollowUps };
}

/** A provider whose investigate() is scripted by the test; RCA delegates to the recorded provider. */
export class ScriptedProvider implements LlmProvider {
  readonly mode = "live" as const;
  readonly model = "scripted";
  packets: EvidencePacket[] = [];
  private readonly recorded = new RecordedProvider(0);

  constructor(
    private readonly script: (packet: EvidencePacket, call: number) => InvestigationOutput | Promise<InvestigationOutput>,
    /** Optional scripted follow-up phase: receives the tools and the call function, like a live model would. */
    readonly followUp?: LlmProvider["followUp"],
  ) {}

  async investigate(packet: EvidencePacket): Promise<LlmResult<InvestigationOutput>> {
    this.packets.push(packet);
    return { output: await this.script(packet, this.packets.length), model: "scripted" };
  }

  draftRca(input: RcaInput): Promise<LlmResult<RcaOutput>> {
    return this.recorded.draftRca(input);
  }
}

type App = ReturnType<typeof makeApp>["app"];

export async function runInvestigation(app: App, incident: string) {
  const start = await app.inject({ method: "POST", url: `/api/incidents/${incident}/investigations`, payload: {} });
  if (start.statusCode !== 202) throw new Error(`start failed: ${start.statusCode} ${start.body}`);
  const id = start.json().investigation_id as string;
  for (let i = 0; i < 200; i++) {
    const res = await app.inject({ method: "GET", url: `/api/investigations/${id}` });
    const inv = res.json();
    if (inv.status === "completed" || inv.status === "failed") return inv;
    await new Promise((r) => setTimeout(r, 10));
  }
  throw new Error("investigation did not finish");
}

export const output = (partial: Partial<InvestigationOutput>): InvestigationOutput => ({
  summary: "test",
  insufficient_evidence: false,
  hypotheses: [],
  similar_incidents: [],
  limitations: [],
  ...partial,
});
