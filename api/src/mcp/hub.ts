import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { getDefaultEnvironment, StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import type { Transport } from "@modelcontextprotocol/sdk/shared/transport.js";
import { configPath, type McpConfig, REPO_ROOT, SERVER_DEFS } from "@resolveai/mcp";
import type { SystemKind, SystemStatus } from "@resolveai/shared";

/** Which system a server covers; ServerDef entries from @resolveai/mcp satisfy it. */
export interface SystemDef {
  system: SystemKind;
  server: string;
  label: string;
}

export interface ToolSpec {
  system: SystemKind;
  server: string;
  name: string;
  description: string;
  inputSchema: { type: "object"; properties?: Record<string, unknown>; required?: string[] };
}

export interface ToolResult {
  ok: boolean;
  summary: string;
  rows: Record<string, unknown>[];
  truncated: boolean;
  source: string;
}

/** What the investigation pipeline needs from the MCP layer; tests pass a hub of in-process servers. */
export interface Hub {
  tools(): ToolSpec[];
  status(): SystemStatus[];
  call(tool: string, args: Record<string, unknown>): Promise<ToolResult>;
  close(): Promise<void>;
}

const CALL_TIMEOUT_MS = 15_000;

/** Environment passed to each server. Children get only this, so settings must be forwarded explicitly. */
function serverEnv(env: NodeJS.ProcessEnv): Record<string, string> {
  const pass = ["MSSQL_USER", "MSSQL_PASSWORD", "MSSQL_ODBC_DRIVER", "SystemRoot", "TEMP", "TMP", "LOCALAPPDATA", "ProgramData", "ProgramFiles", "GIT_EXEC_PATH"];
  return {
    ...getDefaultEnvironment(),
    ...Object.fromEntries(pass.filter((k) => env[k]).map((k) => [k, env[k]!])),
    RESOLVEAI_MCP_CONFIG: env.RESOLVEAI_MCP_CONFIG ?? configPath(env),
  };
}

interface Connection {
  def: SystemDef;
  client: Client | null;
  tools: ToolSpec[];
  status: SystemStatus["status"];
  detail: string | null;
  checkedAt: string;
  reconnect: (() => Promise<Client>) | null;
  restarted: boolean;
}

function parsePayload(res: Record<string, unknown>): ToolResult {
  const text = Array.isArray(res.content) ? res.content.map((c: { type?: string; text?: string }) => (c.type === "text" ? c.text ?? "" : "")).join("") : "";
  try {
    const p = JSON.parse(text) as Partial<ToolResult>;
    return { ok: !res.isError, summary: String(p.summary ?? ""), rows: Array.isArray(p.rows) ? p.rows : [], truncated: !!p.truncated, source: String(p.source ?? "") };
  } catch {
    return { ok: !res.isError, summary: text.slice(0, 500) || "(empty result)", rows: [], truncated: false, source: "" };
  }
}

export class McpHub implements Hub {
  private constructor(private readonly conns: Connection[], private readonly callTimeoutMs: number) {}

  /** Starts every enabled server as a child process over stdio. A server that fails to start is reported, not fatal. */
  static async start(cfg: McpConfig, env: NodeJS.ProcessEnv = process.env): Promise<McpHub> {
    const childEnv = serverEnv(env);
    return McpHub.connect(SERVER_DEFS.map((def) => ({
      def,
      enabled: def.enabled(cfg),
      disabledReason: def.disabledReason,
      transport: () => new StdioClientTransport({
        command: process.execPath, args: ["--import", "tsx", def.entry], cwd: REPO_ROOT, env: childEnv, stderr: "pipe",
      }),
    })));
  }

  /** Connects to servers through any transport (stdio in production, in-memory in tests). */
  static async connect(entries: {
    def: SystemDef;
    enabled: boolean;
    disabledReason?: string;
    transport: () => Transport;
  }[], options: { callTimeoutMs?: number } = {}): Promise<McpHub> {
    const conns = await Promise.all(entries.map(async (e): Promise<Connection> => {
      const base = { def: e.def, client: null, tools: [], checkedAt: new Date().toISOString(), reconnect: null, restarted: false };
      if (!e.enabled) return { ...base, status: "disabled", detail: e.disabledReason ?? "Turned off in mcp.config.json." };
      const open = async () => {
        const client = new Client({ name: "resolveai", version: "0.1.0" });
        await client.connect(e.transport());
        return client;
      };
      try {
        const client = await withTimeout(open(), 20_000, `${e.def.label} did not start within 20s`);
        const { tools } = await client.listTools();
        // Only read-only tools are ever offered; anything else a server exposes is ignored.
        const readOnly = tools.filter((t) => t.annotations?.readOnlyHint === true);
        return {
          ...base, client, reconnect: open, status: "connected",
          detail: readOnly.length < tools.length ? `${tools.length - readOnly.length} tool(s) ignored: not marked read-only.` : null,
          tools: readOnly.map((t) => ({
            system: e.def.system, server: e.def.server, name: t.name, description: t.description ?? "",
            inputSchema: t.inputSchema as ToolSpec["inputSchema"],
          })),
        };
      } catch (err) {
        return { ...base, status: "error", detail: (err as Error).message };
      }
    }));
    return new McpHub(conns, options.callTimeoutMs ?? CALL_TIMEOUT_MS);
  }

  tools(): ToolSpec[] {
    return this.conns.flatMap((c) => c.tools);
  }

  status(): SystemStatus[] {
    return this.conns.map((c) => ({
      system: c.def.system, server: c.def.server, label: c.def.label, status: c.status, detail: c.detail,
      tools: c.tools.map((t) => ({ name: t.name, description: t.description })), checked_at: c.checkedAt,
    }));
  }

  async call(tool: string, args: Record<string, unknown>): Promise<ToolResult> {
    const conn = this.conns.find((c) => c.tools.some((t) => t.name === tool));
    if (!conn?.client) return { ok: false, summary: `No connected system offers the tool "${tool}".`, rows: [], truncated: false, source: "" };
    try {
      return parsePayload(await withTimeout(conn.client.callTool({ name: tool, arguments: args }), this.callTimeoutMs, `${tool} timed out after ${this.callTimeoutMs / 1000}s`));
    } catch (err) {
      const message = (err as Error).message;
      // A crashed server is restarted once; a second failure leaves it in the error state.
      if (/closed|not connected|EPIPE/i.test(message) && conn.reconnect && !conn.restarted) {
        conn.restarted = true;
        try {
          conn.client = await conn.reconnect();
          return parsePayload(await withTimeout(conn.client.callTool({ name: tool, arguments: args }), this.callTimeoutMs, `${tool} timed out`));
        } catch (retryErr) {
          conn.status = "error";
          conn.detail = (retryErr as Error).message;
          conn.checkedAt = new Date().toISOString();
        }
      }
      return { ok: false, summary: `${tool} failed: ${message}`, rows: [], truncated: false, source: "" };
    }
  }

  async close() {
    await Promise.all(this.conns.map((c) => c.client?.close().catch(() => undefined)));
  }
}

function withTimeout<T>(p: Promise<T>, ms: number, message: string): Promise<T> {
  let timer: NodeJS.Timeout | undefined;
  return Promise.race([p, new Promise<never>((_, reject) => (timer = setTimeout(() => reject(new Error(message)), ms)))]).finally(() => clearTimeout(timer));
}
