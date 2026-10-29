import { LIMITS } from "./limits.js";

/**
 * The one result shape every ResolveAI tool returns, as JSON text. `summary` is a single plain sentence
 * the agent can quote; `rows` holds the bounded detail.
 */
export interface ToolPayload {
  summary: string;
  rows: Record<string, unknown>[];
  truncated: boolean;
  /** Where the data came from, e.g. "sys.dm_exec_requests" or "demo-systems/logs/payment-api". */
  source: string;
}

export interface CallToolResult {
  [key: string]: unknown;
  content: { type: "text"; text: string }[];
  isError?: boolean;
}

/** Serialises a payload, dropping trailing rows until it fits the output cap. */
export function ok(summary: string, rows: Record<string, unknown>[], source: string, truncated = false): CallToolResult {
  let kept = rows.slice(0, LIMITS.maxRows);
  let cut = truncated || kept.length < rows.length;
  let text = JSON.stringify({ summary, rows: kept, truncated: cut, source } satisfies ToolPayload);
  while (text.length > LIMITS.maxOutputChars && kept.length > 0) {
    kept = kept.slice(0, Math.floor(kept.length * 0.8));
    cut = true;
    text = JSON.stringify({ summary, rows: kept, truncated: cut, source } satisfies ToolPayload);
  }
  return { content: [{ type: "text", text }] };
}

/** A tool-level failure the agent should see (bad path, system unavailable), not a protocol error. */
export function fail(message: string): CallToolResult {
  return { content: [{ type: "text", text: JSON.stringify({ summary: message, rows: [], truncated: false, source: "error" }) }], isError: true };
}

/** Thrown inside a tool for an expected, user-facing failure. */
export class ToolError extends Error {}
