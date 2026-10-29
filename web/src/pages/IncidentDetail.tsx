import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { Link, useParams } from "react-router-dom";
import { accessCode, api } from "../api";
import { DeploymentsTab, LogsTab, OverviewTab, rowId } from "../components/EvidenceTabs";
import { Findings } from "../components/Findings";
import { RcaPanel } from "../components/RcaEditor";
import { SimilarIncidents } from "../components/SimilarIncidents";
import { SystemsStrip } from "../components/systems";
import { ToolResultsTab } from "../components/ToolResults";
import { Button, cx, ErrorBox, formatTime, SeverityChip, Spinner, StatusChip } from "../components/ui";

const TABS = [
  ["overview", "Signals & timeline"],
  ["logs", "Logs"],
  ["deployments", "Deployments"],
  ["findings", "AI findings"],
  ["systems", "System checks"],
  ["similar", "Similar incidents"],
  ["rca", "RCA"],
] as const;
type Tab = (typeof TABS)[number][0];

const TAB_FOR_KIND: Record<string, Tab> = {
  log: "logs", dep: "deployments", met: "overview", tl: "overview", doc: "overview", hist: "similar", mcp: "systems",
};

export default function IncidentDetail() {
  const { id = "" } = useParams();
  const qc = useQueryClient();
  const [tab, setTab] = useState<Tab>("overview");
  const [focus, setFocus] = useState<{ ref: string; n: number } | null>(null);
  const [question, setQuestion] = useState("");
  const [startedId, setStartedId] = useState<string | null>(null);
  const [live, setLive] = useState(false);

  const config = useQuery({ queryKey: ["config"], queryFn: api.config });
  const systems = useQuery({ queryKey: ["systems"], queryFn: api.systems });
  // Shown only when the server can run live on request; a server that is live by default needs no toggle.
  const liveAllowed = !!config.data?.live_available && config.data.llm_mode !== "live";
  const liveOn = liveAllowed && live;
  const detail = useQuery({ queryKey: ["incident", id], queryFn: () => api.incident(id) });
  const evidence = useQuery({ queryKey: ["evidence", id], queryFn: () => api.evidence(id) });

  const investigationId = startedId ?? detail.data?.latest_investigation_id ?? null;
  const inv = useQuery({
    queryKey: ["investigation", investigationId],
    queryFn: () => api.investigation(investigationId!),
    enabled: !!investigationId,
    refetchInterval: (q) => (q.state.data && ["queued", "running"].includes(q.state.data.status) ? 1500 : false),
  });

  const start = useMutation({
    mutationFn: () => api.startInvestigation(id, question.trim() || undefined, liveOn ? "live" : undefined),
    onSuccess: (res) => {
      setStartedId(res.investigation_id);
      setTab("findings");
      qc.invalidateQueries({ queryKey: ["investigation", res.investigation_id] });
      qc.invalidateQueries({ queryKey: ["incident", id] });
      qc.invalidateQueries({ queryKey: ["config"] });
    },
  });

  // Reset local state when navigating between incidents.
  useEffect(() => {
    setStartedId(null);
    setTab("overview");
    setFocus(null);
  }, [id]);

  // When a run finishes, refresh the incident so the run list shows its final status.
  const invStatus = inv.data?.status;
  useEffect(() => {
    if (invStatus === "completed" || invStatus === "failed") qc.invalidateQueries({ queryKey: ["incident", id] });
  }, [invStatus, id, qc]);

  // After a jump to a source record, scroll it into view once its tab has rendered.
  useEffect(() => {
    if (!focus) return;
    const t = setTimeout(() => document.getElementById(rowId(focus.ref))?.scrollIntoView({ behavior: "smooth", block: "center" }), 50);
    return () => clearTimeout(t);
  }, [focus, tab]);

  const showEvidence = (ref: string) => {
    setTab(TAB_FOR_KIND[ref.slice(0, ref.indexOf(":"))] ?? "overview");
    setFocus((f) => ({ ref, n: (f?.n ?? 0) + 1 }));
  };

  if (detail.isPending) return <div className="flex items-center gap-2 text-sm text-slate-500"><Spinner /> Loading incident…</div>;
  if (detail.isError) {
    return (
      <div className="space-y-3">
        <Link to="/" className="text-sm text-indigo-600">← Incidents</Link>
        <ErrorBox error={detail.error} onRetry={() => detail.refetch()} />
      </div>
    );
  }

  const incident = detail.data.incident;
  const runs = detail.data.investigations;
  const running = inv.data && ["queued", "running"].includes(inv.data.status);
  const focusRef = focus?.ref ?? null;

  return (
    <div className="space-y-5">
      <Link to="/" className="text-sm text-indigo-600 hover:underline">← Incident queue</Link>

      <header className="rounded-lg border border-slate-200 bg-white p-5">
        <div className="flex flex-wrap items-start gap-4">
          <div className="min-w-0 flex-1">
            <div className="flex flex-wrap items-center gap-2">
              <span className="font-mono text-sm text-slate-500">{incident.key}</span>
              <SeverityChip severity={incident.severity} />
              <StatusChip status={incident.status} />
              <span className="rounded bg-slate-100 px-1.5 py-0.5 font-mono text-xs text-slate-600">{incident.service_name}</span>
              <span className="text-xs text-slate-500">{incident.environment}</span>
            </div>
            <h1 className="mt-1.5 text-2xl font-semibold">{incident.title}</h1>
            <p className="mt-1 text-sm text-slate-600">{incident.description}</p>
            <div className="mt-3 rounded-md bg-red-50 px-3 py-2 text-sm text-red-900"><strong>Impact:</strong> {incident.impact_summary}</div>
            <div className="mt-2 text-xs text-slate-500">
              Started {formatTime(incident.started_at, true)} · detected {formatTime(incident.detected_at, true)}
              {incident.resolved_at && <> · resolved {formatTime(incident.resolved_at, true)}</>}
            </div>
            {systems.data && (
              <div className="mt-3">
                <SystemsStrip calls={inv.data?.tool_calls ?? []} statuses={systems.data.systems} running={!!running} />
              </div>
            )}
          </div>
          <form
            className="w-full space-y-2 sm:w-80"
            onSubmit={(e) => {
              e.preventDefault();
              start.mutate();
            }}
          >
            <input
              value={question}
              onChange={(e) => setQuestion(e.target.value)}
              maxLength={500}
              placeholder="Optional question, e.g. is the DB the bottleneck?"
              aria-label="Investigation question"
              className="w-full rounded-md border border-slate-300 px-3 py-1.5 text-sm"
            />
            {liveAllowed && (
              <label className="flex items-start gap-2 text-xs text-slate-600">
                <input type="checkbox" checked={live} onChange={(e) => setLive(e.target.checked)} className="mt-0.5" data-testid="use-live" />
                <span>
                  Use live Claude ({config.data!.live_model}) · {Math.max(0, config.data!.live_daily_limit - config.data!.live_runs_today)} of{" "}
                  {config.data!.live_daily_limit} left today
                  {live && !accessCode.get() && config.data!.llm_mode !== "live" && (
                    <span className="block text-amber-700">Needs the access code — <Link to="/settings" className="underline">enter it in Settings</Link>.</span>
                  )}
                </span>
              </label>
            )}
            <Button type="submit" variant="primary" className="w-full" disabled={start.isPending || !!running} data-testid="run-investigation">
              {(start.isPending || running) && <Spinner className="h-3.5 w-3.5" />}
              {running ? "Investigation running…" : inv.data ? "Run investigation again" : "Run investigation"}
            </Button>
            {start.isError && <ErrorBox error={start.error} />}
            {runs.length > 0 && (
              <div className="text-xs text-slate-500" data-testid="prior-runs">
                {runs.length} previous run{runs.length > 1 ? "s" : ""} — a new run is added; earlier results are kept.
                {runs.length > 1 && (
                  <select
                    value={investigationId ?? ""}
                    onChange={(e) => {
                      setStartedId(e.target.value);
                      setTab("findings");
                    }}
                    className="mt-1 w-full rounded-md border border-slate-300 px-2 py-1 text-xs text-slate-700"
                    aria-label="Investigation run"
                  >
                    {runs.map((r, i) => (
                      <option key={r.id} value={r.id}>
                        {i === 0 ? "Latest" : `Run ${runs.length - i}`} · {formatTime(r.started_at, true)} · {r.llm_mode} · {r.status}
                        {r.top_hypothesis ? ` · ${r.top_hypothesis.slice(0, 40)}` : ""}
                      </option>
                    ))}
                  </select>
                )}
              </div>
            )}
          </form>
        </div>
      </header>

      <nav className="flex gap-1 overflow-x-auto border-b border-slate-200" role="tablist">
        {TABS.map(([key, label]) => (
          <button
            key={key}
            role="tab"
            aria-selected={tab === key}
            onClick={() => setTab(key)}
            className={cx(
              "-mb-px whitespace-nowrap border-b-2 px-3 py-2 text-sm",
              tab === key ? "border-indigo-600 font-medium text-indigo-700" : "border-transparent text-slate-600 hover:text-slate-900",
            )}
          >
            {label}
            {key === "findings" && running && <Spinner className="ml-1.5 h-3 w-3 align-[-2px]" />}
            {key === "logs" && evidence.data && <span className="ml-1 text-xs text-slate-400">{evidence.data.logs.length}</span>}
            {key === "systems" && !!inv.data?.tool_calls.length && (
              <span className="ml-1 rounded-full bg-gradient-to-r from-sky-500 to-violet-500 px-1.5 text-[11px] font-semibold text-white">{inv.data.tool_calls.length}</span>
            )}
            {key === "rca" && detail.data.rca_drafts.length > 0 && <span className="ml-1 text-xs text-slate-400">v{detail.data.rca_drafts[0].version}</span>}
          </button>
        ))}
      </nav>

      <div key={tab}>
        {["overview", "logs", "deployments"].includes(tab) && (
          evidence.isPending ? <div className="flex items-center gap-2 text-sm text-slate-500"><Spinner /> Loading evidence…</div>
            : evidence.isError ? <ErrorBox error={evidence.error} onRetry={() => evidence.refetch()} />
              : tab === "overview" ? <OverviewTab evidence={evidence.data} focus={focusRef} />
                : tab === "logs" ? <LogsTab evidence={evidence.data} focus={focusRef} />
                  : <DeploymentsTab evidence={evidence.data} focus={focusRef} />
        )}
        {tab === "findings" && (
          <Findings
            inv={inv.data}
            loading={!!investigationId && inv.isPending}
            error={inv.error}
            evidence={evidence.data}
            onEvidence={showEvidence}
            onRun={() => start.mutate()}
          />
        )}
        {tab === "systems" && <ToolResultsTab inv={inv.data} focus={focusRef} />}
        {tab === "similar" && <SimilarIncidents inv={inv.data} focus={focusRef} />}
        {tab === "rca" && <RcaPanel incidentKey={id} drafts={detail.data.rca_drafts} inv={inv.data} onEvidence={showEvidence} />}
      </div>
    </div>
  );
}
