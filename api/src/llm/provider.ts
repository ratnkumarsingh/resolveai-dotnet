import type { InvestigationOutput, RcaOutput } from "@resolveai/shared";
import type { EvidencePacket } from "../pipeline/packet.js";

export interface RcaInput {
  packet: EvidencePacket;
  summary: string | null;
  hypotheses: {
    rank: number;
    title: string;
    explanation: string;
    confidence: string;
    speculative: boolean;
    evidence_ids: string[];
    helpful: boolean | null;
  }[];
  verified_root_cause: string | null;
  engineer_notes: string[];
  similar: { id: string; key: string; root_cause: string; resolution: string }[];
}

export interface LlmResult<T> {
  output: T;
  model: string | null;
  usage?: Record<string, number>;
}

/** A read-only tool the model may call during follow-up checks. */
export interface FollowUpTool {
  name: string;
  description: string;
  inputSchema: { type: "object"; properties?: Record<string, unknown>; required?: string[] };
}

/**
 * Runs one tool for the model and returns the text it sees, which starts with the evidence ID to cite.
 * It enforces the call limit itself, so a provider can't exceed it.
 */
export type CallTool = (name: string, args: Record<string, unknown>) => Promise<string>;

export interface LlmProvider {
  readonly mode: "live" | "recorded";
  readonly model: string | null;
  investigate(packet: EvidencePacket): Promise<LlmResult<InvestigationOutput>>;
  draftRca(input: RcaInput): Promise<LlmResult<RcaOutput>>;
  /** Optional extra evidence gathering before analysis: the provider decides which tools to call. */
  followUp?(packet: EvidencePacket, tools: FollowUpTool[], call: CallTool, maxCalls: number): Promise<{ usage?: Record<string, number> }>;
}

/** An error whose message is safe to show the user. `retryable` marks malformed-output failures. */
export class LlmError extends Error {
  constructor(message: string, readonly retryable = false) {
    super(message);
    this.name = "LlmError";
  }
}
