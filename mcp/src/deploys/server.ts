import { loadMcpConfig } from "../common/config.js";
import { serveStdio } from "../common/server.js";
import { buildDeploysServer } from "./tools.js";

await serveStdio(buildDeploysServer(loadMcpConfig()));
