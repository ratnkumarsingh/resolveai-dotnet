import { loadMcpConfig } from "@resolveai/mcp";
import { buildApp } from "./app.js";
import { loadConfig } from "./config.js";
import { get, openDb } from "./db/index.js";
import { seedDemoData } from "./db/seed.js";
import { ClaudeProvider } from "./llm/claude.js";
import { RecordedProvider } from "./llm/recorded.js";
import { McpHub } from "./mcp/hub.js";

const config = loadConfig();
const db = openDb(config.dbPath);

// First run: start with the demo dataset so the app is never empty.
if (!get(db, "SELECT 1 FROM incidents LIMIT 1")) seedDemoData(db);

const providers = {
  recorded: new RecordedProvider(),
  live: config.apiKey ? new ClaudeProvider(config.apiKey, config.model) : undefined,
};
// One MCP server per system. A system that fails to start is shown as an error on the Systems page; it never stops the API.
let systems;
if (config.mcpEnabled) {
  try {
    const mcpConfig = loadMcpConfig();
    systems = { hub: await McpHub.start(mcpConfig), config: mcpConfig, maxFollowUps: config.maxFollowUps };
    for (const s of systems.hub.status()) console.log(`  system ${s.label}: ${s.status}${s.detail ? ` (${s.detail})` : ""}`);
  } catch (err) {
    console.error("MCP systems are unavailable:", (err as Error).message);
  }
}

const app = buildApp({
  db,
  providers,
  systems,
  config: { ...config, demoResetAnyIp: process.env.DEMO_RESET_ANY_IP === "true" },
});

// Investigations interrupted by a restart can't resume; mark them failed so the UI isn't stuck polling.
db.prepare("UPDATE investigations SET status = 'failed', stage = NULL, error_message = 'Interrupted by a server restart. Run the investigation again.' WHERE status IN ('queued', 'running')").run();

await app.listen({ host: config.host, port: config.port });
const live = !providers.live
  ? "off (no API key)"
  : config.llmMode === "live"
    ? `default (${config.model})`
    : config.liveAccessCode
      ? `with access code (${config.model}, ${config.liveDailyLimit}/day)`
      : "off (set LIVE_ACCESS_CODE to allow it)";
console.log(`ResolveAI on http://${config.host}:${config.port}${config.webDist ? " (serving web app)" : ""} — recorded mode; live Claude: ${live}`);
