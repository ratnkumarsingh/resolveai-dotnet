import type {
  AppConfig,
  FeedbackBody,
  Incident,
  IncidentEvidence,
  IncidentListItem,
  Investigation,
  InvestigationRun,
  PatchRcaBody,
  RcaDraft,
  SystemStatus,
} from "@resolveai/shared";

const CODE_KEY = "resolveai.accessCode";

/** The live-mode access code, kept only in this browser. Storage can be unavailable (private mode), so failures are ignored. */
export const accessCode = {
  get(): string {
    try {
      return localStorage.getItem(CODE_KEY) ?? "";
    } catch {
      return "";
    }
  },
  set(code: string) {
    try {
      if (code) localStorage.setItem(CODE_KEY, code);
      else localStorage.removeItem(CODE_KEY);
    } catch {
      // Not persisted; the code just has to be entered again next time.
    }
  },
};

async function request<T>(method: string, url: string, body?: unknown): Promise<T> {
  const headers: Record<string, string> = {};
  if (body !== undefined) headers["content-type"] = "application/json";
  const code = accessCode.get();
  if (code && method !== "GET") headers["x-access-code"] = code;
  const res = await fetch(url, {
    method,
    headers,
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!res.ok) {
    let message = `Request failed (${res.status})`;
    try {
      message = (await res.json()).error ?? message;
    } catch {
      // Non-JSON error body; keep the generic message.
    }
    throw new Error(message);
  }
  return res.json() as Promise<T>;
}

export interface IncidentList {
  items: IncidentListItem[];
  total: number;
  page: number;
  page_size: number;
  services: string[];
}

export interface IncidentDetail {
  incident: Incident;
  latest_investigation_id: string | null;
  investigations: InvestigationRun[];
  rca_drafts: RcaDraft[];
}

export type InvestigationDto = Investigation & { stage: string | null };

export interface KnowledgeDoc {
  id: string;
  title: string;
  doc_type: string;
  service_name: string | null;
  body: string;
}

export const api = {
  config: () => request<AppConfig>("GET", "/api/config"),
  systems: () => request<{ systems: SystemStatus[]; enabled: boolean }>("GET", "/api/systems"),
  incidents: (params: Record<string, string>) =>
    request<IncidentList>("GET", `/api/incidents?${new URLSearchParams(Object.entries(params).filter(([, v]) => v))}`),
  incident: (id: string) => request<IncidentDetail>("GET", `/api/incidents/${encodeURIComponent(id)}`),
  evidence: (id: string) => request<IncidentEvidence>("GET", `/api/incidents/${encodeURIComponent(id)}/evidence`),
  startInvestigation: (id: string, question?: string, mode?: "live" | "recorded") =>
    request<{ investigation_id: string; status: string }>("POST", `/api/incidents/${encodeURIComponent(id)}/investigations`, { question, mode }),
  investigation: (id: string) => request<InvestigationDto>("GET", `/api/investigations/${id}`),
  feedback: (id: string, body: FeedbackBody) => request<{ id: string }>("POST", `/api/investigations/${id}/feedback`, body),
  createRca: (incidentId: string, investigationId: string) =>
    request<{ rca_draft_id: string; rca: RcaDraft }>("POST", `/api/incidents/${encodeURIComponent(incidentId)}/rca-drafts`, { investigation_id: investigationId }),
  patchRca: (id: string, body: PatchRcaBody) => request<RcaDraft>("PATCH", `/api/rca-drafts/${id}`, body),
  rcaMarkdown: async (id: string) => {
    const res = await fetch(`/api/rca-drafts/${id}/export.md`);
    if (!res.ok) throw new Error(`Export failed (${res.status})`);
    return res.text();
  },
  knowledge: (id: string) => request<KnowledgeDoc>("GET", `/api/knowledge/${id}`),
  resetDemo: () => request<{ ok: true }>("POST", "/api/demo/reset"),
};
