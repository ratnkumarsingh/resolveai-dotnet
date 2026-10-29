import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import type { z } from "zod";
import { LIMITS } from "./limits.js";
import { type CallToolResult, fail, ToolError } from "./result.js";

export const READ_ONLY = { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false } as const;

export function createServer(name: string) {
  return new McpServer({ name, version: "0.1.0" });
}

/**
 * Registers a read-only tool. Every handler gets the same guard rails: a timeout, and expected
 * failures (ToolError) returned as an error result instead of a protocol error.
 */
export function readOnlyTool<S extends z.ZodRawShape>(
  server: McpServer,
  name: string,
  description: string,
  inputSchema: S,
  handler: (args: z.infer<z.ZodObject<S>>) => Promise<CallToolResult>,
) {
  server.registerTool(name, { description, inputSchema, annotations: READ_ONLY }, (async (args: z.infer<z.ZodObject<S>>) => {
    let timer: NodeJS.Timeout | undefined;
    try {
      return await Promise.race([
        handler(args),
        new Promise<never>((_, reject) => (timer = setTimeout(() => reject(new ToolError(`${name} took longer than ${LIMITS.callTimeoutMs / 1000}s and was stopped.`)), LIMITS.callTimeoutMs))),
      ]);
    } catch (err) {
      if (err instanceof ToolError) return fail(err.message);
      return fail(`${name} failed: ${err instanceof Error ? err.message : String(err)}`);
    } finally {
      clearTimeout(timer);
    }
  }) as never);
}

/** Runs a server over stdio (how ResolveAI, Claude Code and Claude Desktop start it). */
export async function serveStdio(server: McpServer) {
  await server.connect(new StdioServerTransport());
}
