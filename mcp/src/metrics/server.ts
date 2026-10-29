import { loadMcpConfig } from "../common/config.js";
import { serveStdio } from "../common/server.js";
import { buildMetricsServer } from "./tools.js";

await serveStdio(buildMetricsServer(loadMcpConfig()));
