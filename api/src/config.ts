import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const API_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), "..");

export interface Config {
  apiKey: string | undefined;
  /** Default mode for runs that don't ask for live. */
  llmMode: "live" | "recorded";
  model: string;
  /** With an API key set, a run can opt into live Claude by sending this code. */
  liveAccessCode: string | undefined;
  liveDailyLimit: number;
  demoMode: boolean;
  dbPath: string;
  host: string;
  port: number;
  /** Built web app to serve from the API (the Docker image sets this). */
  webDist: string | undefined;
  /** Start the MCP servers (database, files, deploys, metrics) and check them during investigations. */
  mcpEnabled: boolean;
  /** Most extra tool calls the model may make per investigation. */
  maxFollowUps: number;
}

export function loadConfig(env: NodeJS.ProcessEnv = process.env): Config {
  const apiKey = env.ANTHROPIC_API_KEY?.trim() || undefined;
  // Live mode needs a key; without one we fall back to the deterministic recorded mode.
  const llmMode = env.LLM_MODE === "live" && apiKey ? "live" : "recorded";
  const limit = Number(env.LIVE_DAILY_LIMIT?.trim() || "20");
  const followUps = Number(env.MCP_MAX_FOLLOWUP_CALLS?.trim() || "8");
  return {
    apiKey,
    llmMode,
    model: env.CLAUDE_MODEL?.trim() || "claude-opus-5-5",
    liveAccessCode: env.LIVE_ACCESS_CODE?.trim() || undefined,
    liveDailyLimit: Number.isFinite(limit) && limit >= 0 ? Math.floor(limit) : 20,
    demoMode: (env.DEMO_MODE ?? "true") === "true",
    dbPath: env.DB_PATH === ":memory:" ? ":memory:" : resolve(API_ROOT, env.DB_PATH || "../data/resolveai.db"),
    host: env.HOST?.trim() || "127.0.0.1",
    port: Number(env.API_PORT) || 8787,
    webDist: env.WEB_DIST?.trim() ? resolve(API_ROOT, env.WEB_DIST.trim()) : undefined,
    mcpEnabled: (env.MCP_ENABLED ?? "true") === "true",
    maxFollowUps: Number.isFinite(followUps) ? Math.min(20, Math.max(0, Math.floor(followUps))) : 8,
  };
}
