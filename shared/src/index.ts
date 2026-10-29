import { z } from "zod";

// ---------------------------------------------------------------------------
// Evidence IDs
// Every record handed to the model carries a typed ID so the server can verify
// a citation and link it back to its source table.
// ---------------------------------------------------------------------------

export const EVIDENCE_KINDS = ["log", "dep", "met", "doc", "hist", "tl", "mcp"] as const;
export type EvidenceKind = (typeof EVIDENCE_KINDS)[number];

export function evidenceId(kind: EvidenceKind, id: string): string {
  return `${kind}:${id}`;
}

export function parseEvidenceId(ref: string): { kind: EvidenceKind; id: string } | null {
  const i = ref.indexOf(":");
  if (i <= 0) return null;
  const kind = ref.slice(0, i) as EvidenceKind;
  if (!EVIDENCE_KINDS.includes(kind)) return null;
  return { kind, id: ref.slice(i + 1) };
}

// ---------------------------------------------------------------------------
// LLM output contracts (validated with structured outputs + server checks)
// ---------------------------------------------------------------------------

export const Confidence = z.enum(["low", "medium", "high"]);
export type Confidence = z.infer<typeof Confidence>;

export const RecommendedCheck = z.object({
  step: z.string().describe("A plain-language, read-only check an engineer can perform"),
  why: z.string().describe("Why this check discriminates between hypotheses"),
  expected_signal: z.string().describe("What the engineer should see if the hypothesis is right"),
});
export type RecommendedCheck = z.infer<typeof RecommendedCheck>;

export const LlmHypothesis = z.object({
  title: z.string(),
  explanation: z.string(),
  confidence: Confidence,
  evidence_ids: z.array(z.string()).describe("IDs copied exactly from the evidence packet"),
  counter_evidence: z.array(z.string()),
  unknowns: z.array(z.string()),
  recommended_checks: z.array(RecommendedCheck),
});
export type LlmHypothesis = z.infer<typeof LlmHypothesis>;

export const InvestigationOutput = z.object({
  summary: z.string(),
  insufficient_evidence: z.boolean(),
  hypotheses: z.array(LlmHypothesis),
  similar_incidents: z.array(
    z.object({
      incident_id: z.string().describe("A hist: ID copied from the candidate list"),
      similarity_reason: z.string(),
    }),
  ),
  limitations: z.array(z.string()),
});
export type InvestigationOutput = z.infer<typeof InvestigationOutput>;

export const RcaOutput = z.object({
  impact: z.string(),
  timeline: z.array(
    z.object({
      timestamp: z.string().describe("ISO-8601"),
      event: z.string(),
      evidence_ids: z.array(z.string()),
    }),
  ),
  root_cause: z.string().describe("Suspected root cause, phrased as unconfirmed unless feedback verified it"),
  contributing_factors: z.array(z.string()),
  resolution_steps: z.array(z.string()),
  prevention_actions: z.array(z.string()),
});
export type RcaOutput = z.infer<typeof RcaOutput>;

// ---------------------------------------------------------------------------
// Request bodies
// ---------------------------------------------------------------------------

export const StartInvestigationBody = z.object({
  question: z.string().max(500).optional(),
  /** "live" calls Claude and needs the access code header; anything else replays recorded output. */
  mode: z.enum(["recorded", "live"]).optional(),
});

export const CreateRcaBody = z.object({
  investigation_id: z.string(),
});

export const PatchRcaBody = z.object({
  impact: z.string().max(5000).optional(),
  root_cause: z.string().max(5000).optional(),
  contributing_factors: z.array(z.string().max(1000)).max(30).optional(),
  resolution_steps: z.array(z.string().max(1000)).max(30).optional(),
  prevention_actions: z.array(z.string().max(1000)).max(30).optional(),
  timeline: RcaOutput.shape.timeline.optional(),
  status: z.enum(["draft", "reviewed"]).optional(),
});
export type PatchRcaBody = z.infer<typeof PatchRcaBody>;

export const FeedbackBody = z.object({
  hypothesis_id: z.string().optional(),
  helpful: z.boolean().optional(),
  comment: z.string().max(2000).optional(),
  verified_root_cause: z.string().max(2000).optional(),
});
export type FeedbackBody = z.infer<typeof FeedbackBody>;

// ---------------------------------------------------------------------------
// API DTOs
// ---------------------------------------------------------------------------

export type Severity = "SEV-1" | "SEV-2" | "SEV-3" | "SEV-4";
export type IncidentStatus = "open" | "investigating" | "mitigated" | "resolved";

export interface Incident {
  id: string;
  key: string;
  title: string;
  service_name: string;
  severity: Severity;
  status: IncidentStatus;
  environment: string;
  started_at: string;
  detected_at: string;
  resolved_at: string | null;
  description: string;
  impact_summary: string;
}

export interface LogEvent {
  id: string;
  ref: string;
  timestamp: string;
  service_name: string;
  level: string;
  message: string;
  error_signature: string | null;
  trace_id: string | null;
  source: string;
}

export interface Deployment {
  id: string;
  ref: string;
  service_name: string;
  environment: string;
  version: string;
  deployed_at: string;
  change_summary: string;
  commit_ref: string | null;
}

export interface MetricSnapshot {
  id: string;
  ref: string;
  name: string;
  unit: string;
  baseline: number;
  peak: number;
  observed_at: string;
}

export interface TimelineEvent {
  id: string;
  ref: string;
  timestamp: string;
  kind: string;
  description: string;
}

export interface KnowledgeDocument {
  id: string;
  ref: string;
  title: string;
  doc_type: "runbook" | "rca" | "guide";
  service_name: string | null;
  body: string;
}

export interface HistoricalIncident {
  id: string;
  ref: string;
  key: string;
  title: string;
  service_name: string;
  symptoms: string;
  root_cause: string;
  resolution: string;
  resolved_at: string;
  rca_document_id: string | null;
}

export interface ErrorSignal {
  signature: string;
  count: number;
  first_seen: string;
  last_seen: string;
}

export interface IncidentEvidence {
  incident: Incident;
  timeline: TimelineEvent[];
  logs: LogEvent[];
  error_signals: ErrorSignal[];
  deployments: Deployment[];
  metrics: MetricSnapshot[];
  runbooks: KnowledgeDocument[];
}

export type InvestigationStatus = "queued" | "running" | "completed" | "failed";

export interface ResolvedEvidence {
  ref: string;
  kind: string;
  label: string;
  excerpt: string;
  timestamp: string | null;
}

export interface Hypothesis {
  id: string;
  rank: number;
  title: string;
  explanation: string;
  confidence: Confidence;
  score: number;
  speculative: boolean;
  evidence: ResolvedEvidence[];
  counter_evidence: string[];
  unknowns: string[];
  recommended_checks: RecommendedCheck[];
  feedback: { helpful: boolean | null } | null;
}

export interface SimilarIncidentResult {
  historical: HistoricalIncident;
  match_score: number;
  matched_terms: string[];
  similarity_reason: string;
}

export interface Investigation {
  id: string;
  incident_id: string;
  status: InvestigationStatus;
  question: string | null;
  llm_mode: "live" | "recorded";
  model_name: string | null;
  prompt_version: string;
  started_at: string;
  completed_at: string | null;
  error_message: string | null;
  summary: string | null;
  insufficient_evidence: boolean;
  limitations: string[];
  validation_issues: string[];
  hypotheses: Hypothesis[];
  similar_incidents: SimilarIncidentResult[];
  evidence_count: number;
  trace: TraceStage[];
  /** Every MCP tool call made for this run, in order: the baseline checks, then Claude's follow-ups. */
  tool_calls: ToolCallRecord[];
}

// ---------------------------------------------------------------------------
// Systems (one MCP server per system the agent can check)
// ---------------------------------------------------------------------------

export const SYSTEMS = ["database", "files", "deploys", "metrics"] as const;
export type SystemKind = (typeof SYSTEMS)[number];

/** One read-only tool call against a system. `ref` (mcp:N) is the evidence ID hypotheses cite. */
export interface ToolCallRecord {
  ref: string;
  seq: number;
  /** baseline = ResolveAI's fixed checks; follow_up = a call Claude chose to make. */
  phase: "baseline" | "follow_up";
  system: SystemKind;
  server: string;
  tool: string;
  args: Record<string, unknown>;
  ok: boolean;
  summary: string;
  rows: Record<string, unknown>[];
  truncated: boolean;
  duration_ms: number;
  started_at: string;
}

export interface SystemStatus {
  system: SystemKind;
  server: string;
  label: string;
  /** disabled = turned off in mcp.config.json; error = configured but failed to start. */
  status: "connected" | "error" | "disabled";
  detail: string | null;
  tools: { name: string; description: string }[];
  checked_at: string;
}

/** One step of how an investigation was produced: timing plus the counts that step reported. */
export interface TraceStage {
  stage: string;
  started_at: string;
  duration_ms: number;
  details: Record<string, string | number | boolean>;
}

/** pending = a completed investigation still needs a human to verify the cause or review the RCA. */
export type ReviewState = "none" | "pending" | "verified";

export interface IncidentListItem extends Incident {
  review_state: ReviewState;
}

export interface InvestigationRun {
  id: string;
  status: InvestigationStatus;
  llm_mode: "live" | "recorded";
  started_at: string;
  top_hypothesis: string | null;
}

export interface RcaDraft extends RcaOutput {
  id: string;
  incident_id: string;
  investigation_id: string;
  version: number;
  status: "draft" | "reviewed";
  generated_at: string;
  updated_at: string;
  evidence_ids: string[];
  validation_issues: string[];
  llm_mode: "live" | "recorded";
}

export interface AppConfig {
  /** Mode used when a run doesn't ask for live. */
  llm_mode: "live" | "recorded";
  model: string;
  /** Live Claude runs are possible on request (API key and access code are configured). */
  live_available: boolean;
  live_model: string | null;
  live_daily_limit: number;
  live_runs_today: number;
  demo_mode: boolean;
  data_source: "Demo dataset";
  /** Most extra tool calls Claude may make per live investigation. */
  max_follow_up_calls: number;
}
