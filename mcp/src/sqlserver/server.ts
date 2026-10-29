import { loadMcpConfig } from "../common/config.js";
import { serveStdio } from "../common/server.js";
import { buildSqlServerServer } from "./tools.js";

await serveStdio(buildSqlServerServer(loadMcpConfig()));
