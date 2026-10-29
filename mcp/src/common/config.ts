import { existsSync, readFileSync } from "node:fs";
import { dirname, isAbsolute, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { z } from "zod";

/** What ResolveAI knows about one service: where its logs, code and database live. */
const ServiceConfig = z.object({
  log_roots: z.array(z.string()).default([]),
  /** A git working copy for this service's code, if any. */
  repo: z.string().nullable().default(null),
  /** SQL Server database the service uses, if any. */
  database: z.string().nullable().default(null),
  /** Metric names worth pulling in every baseline. */
  key_metrics: z.array(z.string()).default([]),
});
export type ServiceConfig = z.infer<typeof ServiceConfig>;

export const McpConfig = z.object({
  services: z.record(z.string(), ServiceConfig).default({}),
  files: z.object({
    /** Every files_* path must resolve inside one of these folders. */
    allowed_roots: z.array(z.string()).default([]),
  }).default({ allowed_roots: [] }),
  deploys: z.object({
    /** Optional JSON feed of deployments (used by the demo). */
    feed: z.string().nullable().default(null),
  }).default({ feed: null }),
  metrics: z.object({
    series_dir: z.string().nullable().default(null),
  }).default({ series_dir: null }),
  sqlserver: z.object({
    enabled: z.boolean().default(false),
    server: z.string().default("localhost"),
    /** msnodesqlv8 = Windows sign-in (local Windows only); tedious = SQL login over TCP. */
    driver: z.enum(["msnodesqlv8", "tedious"]).default("tedious"),
    port: z.number().int().optional(),
    trust_server_certificate: z.boolean().default(true),
    /** Run every query as this read-only login (EXECUTE AS LOGIN), so the server enforces read-only even for an admin connection. */
    execute_as: z.string().regex(/^[\w.-]+$/).nullable().default(null),
  }).default({ enabled: false, server: "localhost", driver: "tedious", trust_server_certificate: true, execute_as: null }),
});
export type McpConfig = z.infer<typeof McpConfig>;

export const REPO_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), "../../..");

/** Finds the config file: RESOLVEAI_MCP_CONFIG, then mcp.config.json, then the committed example. */
export function configPath(env: NodeJS.ProcessEnv = process.env): string {
  if (env.RESOLVEAI_MCP_CONFIG) return resolve(env.RESOLVEAI_MCP_CONFIG);
  const local = join(REPO_ROOT, "mcp.config.json");
  return existsSync(local) ? local : join(REPO_ROOT, "mcp.config.example.json");
}

/** Loads and validates the config, resolving every relative path against the config file's folder. */
export function loadMcpConfig(path = configPath()): McpConfig {
  const cfg = McpConfig.parse(JSON.parse(readFileSync(path, "utf8")));
  const base = dirname(path);
  const abs = (p: string) => (isAbsolute(p) ? p : resolve(base, p));
  for (const s of Object.values(cfg.services)) {
    s.log_roots = s.log_roots.map(abs);
    if (s.repo) s.repo = abs(s.repo);
  }
  cfg.files.allowed_roots = cfg.files.allowed_roots.map(abs);
  if (cfg.deploys.feed) cfg.deploys.feed = abs(cfg.deploys.feed);
  if (cfg.metrics.series_dir) cfg.metrics.series_dir = abs(cfg.metrics.series_dir);
  return cfg;
}
