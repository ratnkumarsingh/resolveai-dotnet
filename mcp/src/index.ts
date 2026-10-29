import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import type { McpConfig } from "./common/config.js";
import { buildDeploysServer } from "./deploys/tools.js";
import { buildFilesServer } from "./files/tools.js";
import { buildMetricsServer } from "./metrics/tools.js";
import { buildSqlServerServer } from "./sqlserver/tools.js";

export { configPath, loadMcpConfig, McpConfig, REPO_ROOT, type ServiceConfig } from "./common/config.js";
export type { ToolPayload } from "./common/result.js";
export type { Query } from "./sqlserver/connect.js";
export { buildDeploysServer, buildFilesServer, buildMetricsServer, buildSqlServerServer };

const SRC = dirname(fileURLToPath(import.meta.url));

/** One MCP server per system. `entry` is the stdio script; `build` creates the same server in-process (tests). */
export const SERVER_DEFS = [
  { system: "database", server: "sqlserver", label: "SQL Server", entry: join(SRC, "sqlserver/server.ts"),
    enabled: (c: McpConfig) => c.sqlserver.enabled,
    disabledReason: "Turned off. Run demo-systems/sqlserver/setup.sql, then set sqlserver.enabled in mcp.config.json." },
  { system: "files", server: "files", label: "Files & logs", entry: join(SRC, "files/server.ts"),
    enabled: (c: McpConfig) => c.files.allowed_roots.length > 0, disabledReason: "No folders allowed (files.allowed_roots is empty)." },
  { system: "deploys", server: "deploys", label: "Deploys & Git", entry: join(SRC, "deploys/server.ts"),
    enabled: (c: McpConfig) => !!c.deploys.feed || Object.values(c.services).some((s) => s.repo), disabledReason: "No deploy feed or git repo configured." },
  { system: "metrics", server: "metrics", label: "Metrics & host", entry: join(SRC, "metrics/server.ts"),
    enabled: () => true, disabledReason: "" },
] as const;

export type ServerDef = (typeof SERVER_DEFS)[number];
