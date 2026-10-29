import { randomUUID } from "node:crypto";
import type {
  InvestigationOutput,
  Hypothesis,
  Incident,
  Investigation,
  InvestigationStatus,
  RecommendedCheck,
  ResolvedEvidence,
  SimilarIncidentResult,
  HistoricalIncident,
  InvestigationRun,
  SystemKind,
  ToolCallRecord,
  TraceStage,
} from "@resolveai/shared";
import type { McpConfig } from "@resolveai/mcp";
import { evidenceId } from "@resolveai/shared";
import { all, type Db, get, json, run, tx } from "../db/index.js";
import { PROMPT_VERSION } from "../llm/prompts.js";
import { type CallTool, LlmError, type LlmProvider, type LlmResult } from "../llm/provider.js";
import { planBaseline } from "../mcp/baseline.js";
import type { Hub } from "../mcp/hub.js";
import { loadEvidence } from "../pipeline/evidence.js";
import { addToolItem, buildPacket, type EvidencePacket } from "../pipeline/packet.js";
import { findSimilar } from "../pipeline/similar.js";
import { validateInvestigation } from "../pipeline/validate.js";

/** The MCP systems an investigation can check, and how many extra calls the model may make. */
export interface Systems {
  hub: Hub;
  config: McpConfig;
  maxFollowUps: number;
}

export interface Deps {
  db: Db;
  llm: LlmProvider;
  systems?: Systems;
}

const OVERALL_TIMEOUT_MS = 180_000;
const FOLLOW_UP_TIMEOUT_MS = 120_000;

const now = () => new Date().toISOString();

/** Creates a queued investigation and runs it in the background. Returns its ID immediately. */
export function startInvestigation(deps: Deps, incident: Incident, question: string | null): string {
  const id = randomUUID();
  run(deps.db,
    `INSERT INTO investigations (id, incident_id, status, stage, question, llm_mode, prompt_version, started_at)
     VALUES (?, ?, 'queued', 'Queued', ?, ?, ?, ?)`,
    id, incident.id, question, deps.llm.mode, PROMPT_VERSION, now());
  setImmediate(() => {
    runInvestigation(deps, id, incident, question).catch((err) => failInvestigation(deps.db, id, err));
  });
  return id;
}

function setStage(db: Db, id: string, status: InvestigationStatus, stage: string) {
  run(db, "UPDATE investigations SET status = ?, stage = ? WHERE id = ?", status, stage, id);
}

function failInvestigation(db: Db, id: string, err: unknown) {
  const message = err instanceof LlmError ? err.message : "The investigation failed unexpectedly. The incident data is unchanged; try again.";
  if (!(err instanceof LlmError)) console.error(`investigation ${id} failed:`, err);
  run(db, "UPDATE investigations SET status = 'failed', stage = NULL, error_message = ?, completed_at = ? WHERE id = ?", message, now(), id);
}

export async function withTimeout<T>(p: Promise<T>, ms: number, message: string): Promise<T> {
  let timer: NodeJS.Timeout | undefined;
  try {
    return await Promise.race([p, new Promise<never>((_, reject) => (timer = setTimeout(() => reject(new LlmError(message)), ms)))]);
  } finally {
    clearTimeout(timer);
  }
}

/** Calls the model, retrying once when the failure is malformed output. */
export async function callWithRetry<T>(fn: () => Promise<LlmResult<T>>, issues: string[]): Promise<LlmResult<T>> {
  try {
    return await fn();
  } catch (err) {
    if (!(err instanceof LlmError && err.retryable)) throw err;
    issues.push(`First model response was rejected (${err.message}); retried once.`);
    return await fn();
  }
}

/** Records how long each stage took and what it reported; persisted as the investigation's audit trail. */
class Trace {
  readonly stages: TraceStage[] = [];

  async step<T>(stage: string, fn: () => T | Promise<T>, details: (r: T) => TraceStage["details"]): Promise<T> {
    const started = Date.now();
    const result = await fn();
    this.stages.push({ stage, started_at: new Date(started).toISOString(), duration_ms: Date.now() - started, details: details(result) });
    return result;
  }
}

/** Calls tools in order, numbering each result as evidence mcp:N and recording it. */
function toolRecorder(db: Db, investigationId: string, hub: Hub) {
  const calls: ToolCallRecord[] = [];
  const call = async (phase: ToolCallRecord["phase"], tool: string, args: Record<string, unknown>) => {
    const spec = hub.tools().find((t) => t.name === tool);
    const seq = calls.length + 1;
    const started = Date.now();
    const res = await hub.call(tool, args);
    const rec: ToolCallRecord = {
      ref: evidenceId("mcp", String(seq)), seq, phase, system: (spec?.system ?? "files") as SystemKind, server: spec?.server ?? "unknown",
      tool, args, ok: res.ok, summary: res.summary, rows: res.rows, truncated: res.truncated,
      duration_ms: Date.now() - started, started_at: new Date(started).toISOString(),
    };
    calls.push(rec);
    run(db,
      `INSERT INTO tool_calls (id, investigation_id, seq, phase, system, server, tool, args, ok, summary, rows, truncated, duration_ms, started_at)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
      randomUUID(), investigationId, seq, phase, rec.system, rec.server, tool, JSON.stringify(args), res.ok ? 1 : 0, res.summary,
      JSON.stringify(res.rows), res.truncated ? 1 : 0, rec.duration_ms, rec.started_at);
    return rec;
  };
  return { calls, call };
}

const countBy = (calls: ToolCallRecord[]) => Object.fromEntries(
  [...new Set(calls.map((c) => c.system))].map((s) => [s, calls.filter((c) => c.system === s).length]));

async function runInvestigation(deps: Deps, id: string, incident: Incident, question: string | null) {
  const { db, llm, systems } = deps;
  const trace = new Trace();
  const saveTrace = () => run(db, "UPDATE investigations SET trace = ? WHERE id = ?", JSON.stringify(trace.stages), id);

  setStage(db, id, "running", "Gathering logs, deployments and metrics");
  const evidence = await trace.step("Gather evidence", () => loadEvidence(db, incident), (e) => ({
    logs: e.logs.length,
    error_signals: e.error_signals.length,
    deployments: e.deployments.length,
    metrics: e.metrics.length,
    timeline_events: e.timeline.length,
    runbooks: e.runbooks.length,
  }));

  setStage(db, id, "running", "Searching incident history");
  const similar = await trace.step("Search incident history", () => findSimilar(db, evidence), (c) => ({
    candidates: c.length,
    top_match: c[0]?.historical.key ?? "none",
  }));
  const tools = systems && toolRecorder(db, id, systems.hub);
  if (systems && tools) {
    setStage(db, id, "running", "Checking systems: database, files, deploys and metrics");
    await trace.step("Baseline checks (MCP)", async () => {
      for (const p of planBaseline(incident, systems.config, systems.hub.tools())) await tools.call("baseline", p.tool, p.args);
      return tools.calls;
    }, (calls) => ({
      calls: calls.length,
      failed: calls.filter((c) => !c.ok).length,
      ...countBy(calls),
      systems_off: systems.hub.status().filter((s) => s.status !== "connected").map((s) => s.system).join(", ") || "none",
    }));
    saveTrace();
  }

  const packet = await trace.step("Build evidence packet", () => buildPacket(evidence, similar, question, tools?.calls ?? []), (p) => ({
    items: p.items.length,
    characters: JSON.stringify(p).length,
    redactions: p.redactions,
    truncated: p.truncated,
  }));
  run(db, "UPDATE investigations SET evidence_packet = ? WHERE id = ?", JSON.stringify(packet), id);
  saveTrace();

  const issues: string[] = [];
  if (systems && tools && llm.followUp && systems.maxFollowUps > 0) {
    const max = systems.maxFollowUps;
    const before = tools.calls.length;
    let refused = 0;
    // The limit is enforced here, not by the provider: past it, the model only gets a refusal back.
    const call: CallTool = async (name, args) => {
      if (tools.calls.length - before >= max) {
        refused++;
        return `Limit reached: no more tool calls are allowed in this investigation (${max}). Stop calling tools.`;
      }
      const item = addToolItem(packet, await tools.call("follow_up", name, args));
      return `[${item.id}] ${item.excerpt}`;
    };
    setStage(db, id, "running", llm.mode === "live" ? "Claude is checking systems for missing evidence" : "Running follow-up checks (recorded)");
    try {
      await trace.step(llm.mode === "live" ? "Follow-up checks (Claude)" : "Follow-up checks (recorded)", () => withTimeout(
        llm.followUp!(packet, systems.hub.tools().map(({ name, description, inputSchema }) => ({ name, description, inputSchema })), call, max),
        FOLLOW_UP_TIMEOUT_MS, "Follow-up checks took too long and were stopped.",
      ), (r) => ({
        calls: tools.calls.length - before, limit: max, refused,
        ...Object.fromEntries(Object.entries(r.usage ?? {}).filter(([, v]) => typeof v === "number")),
      }));
    } catch (err) {
      // Follow-ups only add evidence; if they fail, the analysis still runs on what was gathered.
      issues.push(`Follow-up checks stopped early: ${err instanceof Error ? err.message : String(err)}`);
    }
    run(db, "UPDATE investigations SET evidence_packet = ? WHERE id = ?", JSON.stringify(packet), id);
    saveTrace();
  }

  setStage(db, id, "running", llm.mode === "live" ? "Analysing evidence with Claude" : "Analysing evidence (recorded mode)");
  let result: LlmResult<InvestigationOutput>;
  try {
    result = await trace.step(llm.mode === "live" ? "Analyse with Claude" : "Analyse (recorded)", () => withTimeout(
      callWithRetry(() => llm.investigate(packet), issues),
      OVERALL_TIMEOUT_MS,
      "The analysis took too long and was stopped. Try again; the incident data is unchanged.",
    ), (r) => ({
      mode: llm.mode,
      model: r.model ?? "recorded",
      retried: issues.length > 0,
      ...Object.fromEntries(Object.entries(r.usage ?? {}).filter(([, v]) => typeof v === "number")),
    }));
  } finally {
    saveTrace();
  }

  const v = await trace.step("Validate output", () => validateInvestigation(result.output, packet), (r) => ({
    hypotheses: r.hypotheses.length,
    speculative: r.hypotheses.filter((h) => h.speculative).length,
    citations_removed: r.issues.filter((i) => i.startsWith("Removed citation")).length,
    unsafe_checks_removed: r.issues.filter((i) => i.startsWith("Removed a recommended check")).length,
    unverified_numbers: r.issues.filter((i) => i.startsWith("Unverified number")).length,
    insufficient_evidence: r.insufficient_evidence,
  }));
  issues.push(...v.issues);
  if (packet.redactions > 0) issues.push(`Redacted ${packet.redactions} secret or personal value(s) before analysis.`);
  if (packet.truncated) issues.push("Evidence packet was trimmed to fit the size limit; lower-priority log lines were left out.");

  tx(db, () => {
    for (const h of v.hypotheses) {
      const hid = randomUUID();
      run(db,
        `INSERT INTO hypotheses (id, investigation_id, rank, title, explanation, confidence, score, speculative, counter_evidence, unknowns, recommended_checks)
         VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
        hid, id, h.rank, h.title, h.explanation, h.confidence, h.score, h.speculative ? 1 : 0,
        JSON.stringify(h.counter_evidence), JSON.stringify(h.unknowns), JSON.stringify(h.recommended_checks));
      h.evidence_ids.forEach((ref, i) => run(db, "INSERT INTO evidence_links (hypothesis_id, evidence_ref, position) VALUES (?, ?, ?)", hid, ref, i));
    }
    similar.forEach((c, i) => {
      const reason = v.similar_reasons.get(c.historical.ref)
        ?? `Matched on: ${c.matched_terms.slice(0, 6).join(", ")}${c.historical.service_name === incident.service_name ? " (same service)" : ""}.`;
      run(db,
        `INSERT INTO investigation_similar_incidents (investigation_id, historical_incident_id, rank, match_score, matched_terms, similarity_reason)
         VALUES (?, ?, ?, ?, ?, ?)`,
        id, c.historical.id, i + 1, c.match_score, JSON.stringify(c.matched_terms), reason);
    });
    run(db,
      `UPDATE investigations SET status = 'completed', stage = NULL, model_name = ?, summary = ?, insufficient_evidence = ?,
              limitations = ?, validation_issues = ?, usage = ?, trace = ?, completed_at = ? WHERE id = ?`,
      result.model, v.summary, v.insufficient_evidence ? 1 : 0, JSON.stringify(v.limitations), JSON.stringify(issues),
      result.usage ? JSON.stringify(result.usage) : null, JSON.stringify(trace.stages), now(), id);
  });
}

// ---------------------------------------------------------------------------
// Read model
// ---------------------------------------------------------------------------

interface InvestigationRow {
  id: string;
  incident_id: string;
  status: InvestigationStatus;
  stage: string | null;
  question: string | null;
  llm_mode: "live" | "recorded";
  evidence_packet: string | null;
  model_name: string | null;
  prompt_version: string;
  summary: string | null;
  insufficient_evidence: number;
  limitations: string;
  validation_issues: string;
  trace: string;
  started_at: string;
  completed_at: string | null;
  error_message: string | null;
}

export function getInvestigationRow(db: Db, id: string) {
  return get<InvestigationRow>(db, "SELECT * FROM investigations WHERE id = ?", id);
}

export function getPacket(row: { evidence_packet: string | null }): EvidencePacket | null {
  return json<EvidencePacket | null>(row.evidence_packet, null);
}

const KIND_LABEL: Record<string, string> = {
  log: "Log", dep: "Deployment", met: "Metric", doc: "Runbook", hist: "Past incident", tl: "Timeline", mcp: "System check",
};

export function resolveEvidence(packet: EvidencePacket | null, refs: string[]): ResolvedEvidence[] {
  const byId = new Map(packet?.items.map((i) => [i.id, i]) ?? []);
  return refs.map((ref) => {
    const item = byId.get(ref);
    const kind = ref.slice(0, ref.indexOf(":"));
    return {
      ref,
      kind,
      // A system check is labelled with its system and tool, e.g. "database: db_blocking".
      label: kind === "mcp" && item ? item.source.replace(/\(.*$/, "") : `${KIND_LABEL[kind] ?? kind}${item?.occurrences ? ` ×${item.occurrences}` : ""}`,
      excerpt: item?.excerpt ?? "(record not in packet)",
      timestamp: item?.timestamp ?? null,
    };
  });
}

export function getInvestigation(db: Db, id: string): (Investigation & { stage: string | null }) | undefined {
  const row = getInvestigationRow(db, id);
  if (!row) return undefined;
  const packet = getPacket(row);

  const hypotheses: Hypothesis[] = all<{
    id: string; rank: number; title: string; explanation: string; confidence: Hypothesis["confidence"]; score: number;
    speculative: number; counter_evidence: string; unknowns: string; recommended_checks: string;
  }>(db, "SELECT * FROM hypotheses WHERE investigation_id = ? ORDER BY rank", id).map((h) => {
    const refs = all<{ evidence_ref: string }>(db,
      "SELECT evidence_ref FROM evidence_links WHERE hypothesis_id = ? ORDER BY position", h.id).map((r) => r.evidence_ref);
    const fb = get<{ helpful: number | null }>(db,
      "SELECT helpful FROM feedback WHERE hypothesis_id = ? AND helpful IS NOT NULL ORDER BY created_at DESC, rowid DESC LIMIT 1", h.id);
    return {
      id: h.id,
      rank: h.rank,
      title: h.title,
      explanation: h.explanation,
      confidence: h.confidence,
      score: h.score,
      speculative: !!h.speculative,
      evidence: resolveEvidence(packet, refs),
      counter_evidence: json<string[]>(h.counter_evidence, []),
      unknowns: json<string[]>(h.unknowns, []),
      recommended_checks: json<RecommendedCheck[]>(h.recommended_checks, []),
      feedback: fb ? { helpful: fb.helpful === null ? null : !!fb.helpful } : null,
    };
  });

  const similar_incidents: SimilarIncidentResult[] = all<HistoricalIncident & { match_score: number; matched_terms: string; similarity_reason: string }>(db,
    `SELECT h.id, h.key, h.title, h.service_name, h.symptoms, h.root_cause, h.resolution, h.resolved_at, h.rca_document_id,
            s.match_score, s.matched_terms, s.similarity_reason
       FROM investigation_similar_incidents s JOIN historical_incidents h ON h.id = s.historical_incident_id
      WHERE s.investigation_id = ? ORDER BY s.rank`, id,
  ).map(({ match_score, matched_terms, similarity_reason, ...h }) => ({
    historical: { ...h, ref: evidenceId("hist", h.id) },
    match_score,
    matched_terms: json<string[]>(matched_terms, []),
    similarity_reason,
  }));

  return {
    id: row.id,
    incident_id: row.incident_id,
    status: row.status,
    stage: row.stage,
    question: row.question,
    llm_mode: row.llm_mode,
    model_name: row.model_name,
    prompt_version: row.prompt_version,
    started_at: row.started_at,
    completed_at: row.completed_at,
    error_message: row.error_message,
    summary: row.summary,
    insufficient_evidence: !!row.insufficient_evidence,
    limitations: json<string[]>(row.limitations, []),
    validation_issues: json<string[]>(row.validation_issues, []),
    hypotheses,
    similar_incidents,
    evidence_count: packet?.items.length ?? 0,
    trace: json<TraceStage[]>(row.trace, []),
    tool_calls: listToolCalls(db, id),
  };
}

export function listToolCalls(db: Db, investigationId: string): ToolCallRecord[] {
  return all<{
    seq: number; phase: ToolCallRecord["phase"]; system: SystemKind; server: string; tool: string; args: string; ok: number;
    summary: string; rows: string; truncated: number; duration_ms: number; started_at: string;
  }>(db, "SELECT * FROM tool_calls WHERE investigation_id = ? ORDER BY seq", investigationId).map((r) => ({
    ref: evidenceId("mcp", String(r.seq)), seq: r.seq, phase: r.phase, system: r.system, server: r.server, tool: r.tool,
    args: json<Record<string, unknown>>(r.args, {}), ok: !!r.ok, summary: r.summary, rows: json<Record<string, unknown>[]>(r.rows, []),
    truncated: !!r.truncated, duration_ms: r.duration_ms, started_at: r.started_at,
  }));
}

export function latestInvestigationId(db: Db, incidentId: string): string | undefined {
  return get<{ id: string }>(db,
    "SELECT id FROM investigations WHERE incident_id = ? ORDER BY started_at DESC, rowid DESC LIMIT 1", incidentId)?.id;
}

/** Every run for an incident, newest first, so a re-run is visibly additive rather than a replacement. */
export function listInvestigationRuns(db: Db, incidentId: string): InvestigationRun[] {
  return all<InvestigationRun>(db,
    `SELECT i.id, i.status, i.llm_mode, i.started_at,
            (SELECT title FROM hypotheses h WHERE h.investigation_id = i.id ORDER BY rank LIMIT 1) AS top_hypothesis
       FROM investigations i WHERE i.incident_id = ? ORDER BY i.started_at DESC, i.rowid DESC`, incidentId);
}

/** Live (paid) model calls made since midnight UTC, across investigations and RCA drafts. */
export function liveRunsToday(db: Db): number {
  const since = `${new Date().toISOString().slice(0, 10)}T00:00:00.000Z`;
  const inv = get<{ n: number }>(db, "SELECT COUNT(*) AS n FROM investigations WHERE llm_mode = 'live' AND started_at >= ?", since)?.n ?? 0;
  const rca = get<{ n: number }>(db, "SELECT COUNT(*) AS n FROM rca_drafts WHERE llm_mode = 'live' AND generated_at >= ?", since)?.n ?? 0;
  return inv + rca;
}
