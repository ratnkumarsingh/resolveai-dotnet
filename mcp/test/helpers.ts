import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { McpConfig } from "../src/common/config.js";

/** Connects a test client to a server in-process. */
export async function connect(server: McpServer) {
  const [a, b] = InMemoryTransport.createLinkedPair();
  await server.connect(a);
  const client = new Client({ name: "test", version: "0" });
  await client.connect(b);
  const call = async (name: string, args: Record<string, unknown> = {}) => {
    const res = await client.callTool({ name, arguments: args });
    const text = (res.content as { text: string }[])[0].text;
    type Payload = { summary: string; rows: Record<string, unknown>[]; truncated: boolean; source: string };
    // Input-schema rejections come back from the SDK as plain text rather than our JSON payload.
    const payload: Payload = text.startsWith("{") ? JSON.parse(text) : { summary: text, rows: [], truncated: false, source: "sdk" };
    return { isError: !!res.isError, ...payload };
  };
  return { client, call };
}

export const config = (partial: Record<string, unknown>) => McpConfig.parse(partial);
