import { loadMcpConfig } from "../common/config.js";
import { serveStdio } from "../common/server.js";
import { buildFilesServer } from "./tools.js";

await serveStdio(buildFilesServer(loadMcpConfig()));
