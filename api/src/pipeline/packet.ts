import type { IncidentEvidence, ToolCallRecord } from "@resolveai/shared";
import { sanitize } from "./sanitize.js";
import type { SimilarCandidate } from "./similar.js";

export interface PacketItem {
  id: string;
  type: "log" | "deployment" | "metric" | "timeline" | "runbook" | "historical_incident" | "tool_result";
  timestamp: string | null;
  source: string;
  excerpt: string;
  occurrences?: number;
}

export interface EvidencePacket {
  incident: {
    key: string;
    title: string;
    service: string;
    severity: string;
    environment: string;
    started_at: string;
    detected_at: string;
    description: string;
    impact_summary: string;
  };
  question: string | null;
  items: PacketItem[];
  similar_candidates: string[];
  redactions: number;
  truncated: boolean;
}

export const MAX_LOG_GROUPS = 40;
export const MAX_PACKET_CHARS = 40_000;
const MAX_EXCERPT = 600;
const MAX_TOOL_EXCERPT = 1200;

/** One line per row, `key=value` pairs, nulls dropped: compact enough to fit several rows in an excerpt. */
function compactRows(rows: Record<string, unknown>[]) {
  return rows.map((r) => Object.entries(r).filter(([, v]) => v !== null && v !== undefined && v !== "")
    .map(([k, v]) => `${k}=${typeof v === "object" ? JSON.stringify(v) : String(v)}`).join(" ")).join("\n");
}

/** A tool call as a packet item, sanitised like every other excerpt. */
export function toolItem(call: ToolCallRecord): { item: PacketItem; redactions: number } {
  const r = sanitize(`${call.ok ? "" : "CHECK FAILED: "}${call.summary}${call.rows.length ? `\n${compactRows(call.rows)}` : ""}`);
  const excerpt = r.text.length > MAX_TOOL_EXCERPT ? `${r.text.slice(0, MAX_TOOL_EXCERPT)}…` : r.text;
  const args = Object.entries(call.args).map(([k, v]) => `${k}=${String(v)}`).join(", ");
  return {
    item: { id: call.ref, type: "tool_result", timestamp: null, source: `${call.system}: ${call.tool}(${args})`, excerpt },
    redactions: r.redactions,
  };
}

/** Adds a follow-up tool result to an existing packet (the packet stays the single record of what the model saw). */
export function addToolItem(packet: EvidencePacket, call: ToolCallRecord) {
  const { item, redactions } = toolItem(call);
  packet.items.push(item);
  packet.redactions += redactions;
  return item;
}

/** Normalises a log line so repeats that differ only by IDs/numbers collapse into one group. */
function logShape(message: string): string {
  return message.replace(/\b(req|request_id|trace|span)[-=:]?[\w-]+/gi, "$1#").replace(/\d+/g, "#");
}

/**
 * Builds the bounded, sanitised, ordered evidence the model is allowed to see and cite.
 * Every item has a stable typed ID; the validator only accepts citations of these IDs.
 */
export function buildPacket(evidence: IncidentEvidence, similar: SimilarCandidate[], question: string | null, toolCalls: ToolCallRecord[] = []): EvidencePacket {
  let redactions = 0;
  const clean = (s: string) => {
    const r = sanitize(s);
    redactions += r.redactions;
    return r.text.length > MAX_EXCERPT ? `${r.text.slice(0, MAX_EXCERPT)}…` : r.text;
  };

  const items: PacketItem[] = [];

  for (const e of evidence.timeline) {
    items.push({ id: e.ref, type: "timeline", timestamp: e.timestamp, source: e.kind, excerpt: clean(e.description) });
  }

  // Deduplicate logs by shape, keeping the first occurrence's ID and counting repeats.
  const groups = new Map<string, PacketItem>();
  for (const log of evidence.logs) {
    const key = `${log.service_name}|${log.level}|${logShape(log.message)}`;
    const existing = groups.get(key);
    if (existing) {
      existing.occurrences = (existing.occurrences ?? 1) + 1;
      continue;
    }
    groups.set(key, {
      id: log.ref,
      type: "log",
      timestamp: log.timestamp,
      source: `${log.source}/${log.service_name}`,
      excerpt: clean(`${log.level} ${log.message}${log.error_signature ? ` [signature=${log.error_signature}]` : ""}`),
    });
  }
  const logGroups = [...groups.values()];
  // Prefer errors and warnings when over the cap, but keep chronological order in the packet.
  const prioritized = logGroups
    .map((g, i) => ({ g, i, p: /^(ERROR|FATAL)/.test(g.excerpt) ? 0 : /^WARN/.test(g.excerpt) ? 1 : 2 }))
    .sort((a, b) => a.p - b.p || a.i - b.i)
    .slice(0, MAX_LOG_GROUPS)
    .sort((a, b) => a.i - b.i)
    .map((x) => x.g);
  let truncated = logGroups.length > prioritized.length;
  items.push(...prioritized);

  for (const d of evidence.deployments) {
    items.push({
      id: d.ref,
      type: "deployment",
      timestamp: d.deployed_at,
      source: `deploys/${d.service_name}`,
      excerpt: clean(`${d.service_name} ${d.version} deployed to ${d.environment}: ${d.change_summary}${d.commit_ref ? ` (commit ${d.commit_ref})` : ""}`),
    });
  }
  for (const m of evidence.metrics) {
    items.push({
      id: m.ref,
      type: "metric",
      timestamp: m.observed_at,
      source: "metrics",
      excerpt: `${m.name}: baseline ${m.baseline} ${m.unit} → peak ${m.peak} ${m.unit}`,
    });
  }
  for (const r of evidence.runbooks) {
    items.push({ id: r.ref, type: "runbook", timestamp: null, source: r.doc_type, excerpt: clean(`${r.title}: ${r.body}`) });
  }
  for (const c of similar) {
    const h = c.historical;
    items.push({
      id: h.ref,
      type: "historical_incident",
      timestamp: h.resolved_at,
      source: "incident-history",
      excerpt: clean(`${h.key} ${h.title} (${h.service_name}). Symptoms: ${h.symptoms}. Root cause: ${h.root_cause}. Resolution: ${h.resolution}.`),
    });
  }

  items.sort((a, b) => (a.timestamp ?? "9999").localeCompare(b.timestamp ?? "9999"));

  // Tool results come last, in call order (they describe what the systems showed when checked).
  for (const call of toolCalls) {
    const t = toolItem(call);
    items.push(t.item);
    redactions += t.redactions;
  }

  // Enforce the overall size budget by dropping the lowest-priority items (info logs first).
  const size = () => JSON.stringify(items).length;
  while (size() > MAX_PACKET_CHARS) {
    const idx = items.findLastIndex((i) => i.type === "log" && !/^(ERROR|WARN|FATAL)/.test(i.excerpt));
    const victim = idx >= 0 ? idx : items.findLastIndex((i) => i.type === "log");
    if (victim < 0) break;
    items.splice(victim, 1);
    truncated = true;
  }

  const inc = evidence.incident;
  return {
    incident: {
      key: inc.key,
      title: clean(inc.title),
      service: inc.service_name,
      severity: inc.severity,
      environment: inc.environment,
      started_at: inc.started_at,
      detected_at: inc.detected_at,
      description: clean(inc.description),
      impact_summary: clean(inc.impact_summary),
    },
    question: question ? clean(question) : null,
    items,
    similar_candidates: similar.map((c) => c.historical.ref),
    redactions,
    truncated,
  };
}
