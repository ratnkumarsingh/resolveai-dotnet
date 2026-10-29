import { isAbsolute, relative, sep } from "node:path";
import type { McpConfig } from "@resolveai/mcp";
import type { Incident } from "@resolveai/shared";
import type { ToolSpec } from "./hub.js";

export interface PlannedCall {
  tool: string;
  args: Record<string, unknown>;
}

const MIN = 60_000;
const iso = (ms: number) => new Date(ms).toISOString().replace(/\.\d{3}Z$/, "Z");

/** The same window loadEvidence uses: 30 minutes before the start to an hour after detection. */
export function incidentWindow(incident: Incident) {
  const start = Date.parse(incident.started_at);
  return { from: iso(start - 30 * MIN), to: iso(Math.max(Date.parse(incident.detected_at), start) + 60 * MIN), start };
}

/**
 * The fixed checks ResolveAI runs for every investigation, before any model is involved. Only tools a
 * connected server offers are planned, so a system that is turned off simply contributes nothing.
 */
export function planBaseline(incident: Incident, cfg: McpConfig, available: ToolSpec[]): PlannedCall[] {
  const has = new Set(available.map((t) => t.name));
  const svc = cfg.services[incident.service_name];
  const w = incidentWindow(incident);
  const calls: PlannedCall[] = [];
  const add = (tool: string, args: Record<string, unknown>) => {
    if (has.has(tool)) calls.push({ tool, args });
  };

  if (svc?.database) {
    add("db_health", {});
    add("db_blocking", {});
    add("db_errors", { from: w.from, to: w.to });
    add("db_deadlocks", { from: w.from, to: w.to });
  }
  // The files server resolves relative paths against its first allowed root; using them keeps local
  // absolute paths out of tool arguments (and out of what the model sees).
  const base = cfg.files.allowed_roots[0];
  const shortPath = (p: string) => {
    const rel = base ? relative(base, p) : p;
    return rel && !rel.startsWith("..") && !isAbsolute(rel) ? rel.split(sep).join("/") : p;
  };
  for (const root of (svc?.log_roots ?? []).map(shortPath)) {
    add("files_search", { root, phrase: "ERROR", from: w.from, to: w.to, max_matches: 30 });
    add("files_search", { root, phrase: "WARN", from: w.from, to: w.to, max_matches: 15 });
  }
  // Deploys in the 23 hours before the incident window closes (tools cap a window at 24 hours).
  add("deploys_recent", { service: incident.service_name, from: iso(Date.parse(w.to) - 23 * 60 * MIN), to: w.to });
  for (const name of svc?.key_metrics ?? []) add("metrics_series", { service: incident.service_name, name, from: w.from, to: w.to });
  return calls;
}
