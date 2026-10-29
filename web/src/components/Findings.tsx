import { useMutation, useQueryClient } from "@tanstack/react-query";
import type { Hypothesis, IncidentEvidence } from "@resolveai/shared";
import { useState } from "react";
import { api, type InvestigationDto } from "../api";
import { SYSTEM, SystemIcon, systemOfLabel } from "./systems";
import { AiBadge, Button, Card, ConfidenceChip, cx, Empty, ErrorBox, formatTime, Spinner } from "./ui";

const KIND_STYLE: Record<string, string> = {
  log: "bg-slate-100 text-slate-700",
  dep: "bg-indigo-100 text-indigo-800",
  met: "bg-red-50 text-red-700",
  tl: "bg-amber-50 text-amber-800",
  doc: "bg-emerald-50 text-emerald-800",
  hist: "bg-violet-50 text-violet-800",
};

/** The source label of a cited record; system checks get their system's colour and icon. */
function EvidenceChip({ kind, label }: { kind: string; label: string }) {
  const system = kind === "mcp" ? systemOfLabel(label) : null;
  if (system) {
    return (
      <span className={cx("inline-flex shrink-0 items-center gap-1 rounded px-1.5 py-0.5 text-xs font-medium", SYSTEM[system].chip)} data-testid="system-citation">
        <SystemIcon system={system} className="h-3 w-3" />
        {label.slice(label.indexOf(":") + 1).trim()}
      </span>
    );
  }
  return <span className={cx("shrink-0 rounded px-1.5 py-0.5 text-xs font-medium", KIND_STYLE[kind])}>{label}</span>;
}

/** Facts computed directly from records, shown apart from anything the model inferred. */
function ObservedFacts({ evidence }: { evidence: IncidentEvidence }) {
  const start = Date.parse(evidence.incident.started_at);
  const priorDeploys = evidence.deployments.filter((d) => {
    const m = (start - Date.parse(d.deployed_at)) / 60_000;
    return m >= 0 && m <= 120;
  });
  const firstError = evidence.logs.find((l) => l.level === "ERROR");
  const moved = evidence.metrics.filter((m) => m.peak >= Math.max(3 * m.baseline, m.baseline + 1));
  const facts = [
    firstError && `First error at ${formatTime(firstError.timestamp)}: ${firstError.message}`,
    ...evidence.error_signals.slice(0, 3).map((s) => `${s.count}× ${s.signature} between ${formatTime(s.first_seen)} and ${formatTime(s.last_seen)}`),
    ...priorDeploys.map((d) => `${d.service_name} ${d.version} deployed ${Math.round((start - Date.parse(d.deployed_at)) / 60_000)} min before start: ${d.change_summary}`),
    ...moved.map((m) => `${m.name} rose from ${m.baseline.toLocaleString()} to ${m.peak.toLocaleString()} ${m.unit}`),
  ].filter(Boolean) as string[];

  return (
    <Card title={<span className="flex items-center gap-2">Observed facts <span className="rounded bg-slate-100 px-1.5 py-0.5 text-xs font-medium text-slate-600">from records — not AI</span></span>}>
      {facts.length === 0 ? (
        <p className="text-sm text-slate-500">No error signals, recent deployments or significant metric changes in the records.</p>
      ) : (
        <ul className="list-disc space-y-1 pl-5 text-sm text-slate-700">{facts.map((f) => <li key={f}>{f}</li>)}</ul>
      )}
    </Card>
  );
}

function HypothesisCard({ h, investigationId, onEvidence }: { h: Hypothesis; investigationId: string; onEvidence: (ref: string) => void }) {
  const qc = useQueryClient();
  const vote = useMutation({
    mutationFn: (helpful: boolean) => api.feedback(investigationId, { hypothesis_id: h.id, helpful }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["investigation", investigationId] }),
  });
  const helpful = h.feedback?.helpful;

  return (
    <article className="rounded-lg border border-slate-200 bg-white" data-testid="hypothesis">
      <header className="flex flex-wrap items-start gap-2 border-b border-slate-100 px-4 py-3">
        <span className="grid h-6 w-6 shrink-0 place-items-center rounded-full bg-slate-900 text-xs font-semibold text-white">{h.rank}</span>
        <h4 className="min-w-0 flex-1 font-semibold">{h.title}</h4>
        <ConfidenceChip confidence={h.confidence} />
        {h.speculative && <span className="rounded bg-amber-100 px-1.5 py-0.5 text-xs font-medium text-amber-900">speculative</span>}
      </header>
      <div className="space-y-4 p-4">
        <p className="text-sm text-slate-700">{h.explanation}</p>

        <div>
          <h5 className="mb-1.5 text-xs font-semibold uppercase tracking-wide text-slate-500">Supporting evidence</h5>
          {h.evidence.length === 0 ? (
            <p className="text-sm text-amber-800">No supporting records in the evidence packet.</p>
          ) : (
            <ul className="space-y-1.5">
              {h.evidence.map((e) => (
                <li key={e.ref}>
                  <button
                    onClick={() => onEvidence(e.ref)}
                    className="group flex w-full items-start gap-2 rounded-md p-1.5 text-left text-sm hover:bg-slate-50"
                    title="Show source record"
                    data-testid="evidence-link"
                  >
                    <EvidenceChip kind={e.kind} label={e.label} />
                    {e.timestamp && <span className="shrink-0 font-mono text-xs leading-5 text-slate-500">{formatTime(e.timestamp, e.kind === "hist").replace(" UTC", "")}</span>}
                    <span className="min-w-0 break-words text-slate-700 group-hover:text-indigo-700 group-hover:underline">
                      {/* A system check cites its one-line summary here; the rows are in the System checks tab. */}
                      {e.kind === "mcp" ? e.excerpt.split("\n")[0] : e.excerpt}
                    </span>
                  </button>
                </li>
              ))}
            </ul>
          )}
        </div>

        {(h.counter_evidence.length > 0 || h.unknowns.length > 0) && (
          <div className="grid gap-4 sm:grid-cols-2">
            {h.counter_evidence.length > 0 && (
              <div>
                <h5 className="mb-1 text-xs font-semibold uppercase tracking-wide text-slate-500">Counter-evidence</h5>
                <ul className="list-disc space-y-0.5 pl-5 text-sm text-slate-700">{h.counter_evidence.map((c) => <li key={c}>{c}</li>)}</ul>
              </div>
            )}
            {h.unknowns.length > 0 && (
              <div>
                <h5 className="mb-1 text-xs font-semibold uppercase tracking-wide text-slate-500">Unknowns</h5>
                <ul className="list-disc space-y-0.5 pl-5 text-sm text-slate-700">{h.unknowns.map((c) => <li key={c}>{c}</li>)}</ul>
              </div>
            )}
          </div>
        )}

        {h.recommended_checks.length > 0 && (
          <div>
            <h5 className="mb-1.5 text-xs font-semibold uppercase tracking-wide text-slate-500">Recommended checks <span className="font-normal normal-case">(for an engineer to perform — nothing is executed)</span></h5>
            <div className="overflow-x-auto rounded-md border border-slate-200">
              <table className="w-full text-left text-sm">
                <thead className="bg-slate-50 text-xs text-slate-500"><tr><th className="px-3 py-1.5">Check</th><th className="px-3 py-1.5">Why</th><th className="px-3 py-1.5">Expected signal</th></tr></thead>
                <tbody className="divide-y divide-slate-100">
                  {h.recommended_checks.map((c) => (
                    <tr key={c.step}><td className="px-3 py-2 align-top">{c.step}</td><td className="px-3 py-2 align-top text-slate-600">{c.why}</td><td className="px-3 py-2 align-top text-slate-600">{c.expected_signal}</td></tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>
        )}

        <footer className="flex flex-wrap items-center gap-2 border-t border-slate-100 pt-3 text-sm">
          <span className="text-slate-500">Was this hypothesis useful?</span>
          <Button className={cx(helpful === true && "border-emerald-400 bg-emerald-50 text-emerald-800")} onClick={() => vote.mutate(true)} disabled={vote.isPending}>Useful</Button>
          <Button className={cx(helpful === false && "border-red-300 bg-red-50 text-red-800")} onClick={() => vote.mutate(false)} disabled={vote.isPending}>Not useful</Button>
          {vote.isError && <span className="text-red-700">{(vote.error as Error).message}</span>}
        </footer>
      </div>
    </article>
  );
}

function VerifiedCauseForm({ inv }: { inv: InvestigationDto }) {
  const qc = useQueryClient();
  const [cause, setCause] = useState("");
  const [comment, setComment] = useState("");
  const [hypothesisId, setHypothesisId] = useState("");
  const save = useMutation({
    mutationFn: () => api.feedback(inv.id, {
      hypothesis_id: hypothesisId || undefined,
      verified_root_cause: cause.trim() || undefined,
      comment: comment.trim() || undefined,
    }),
    onSuccess: () => {
      setCause("");
      setComment("");
      qc.invalidateQueries({ queryKey: ["investigation", inv.id] });
    },
  });

  return (
    <Card title="Record what was verified">
      <form
        className="space-y-3"
        onSubmit={(e) => {
          e.preventDefault();
          save.mutate();
        }}
      >
        <label className="block text-sm">
          <span className="text-slate-600">Verified root cause</span>
          <input value={cause} onChange={(e) => setCause(e.target.value)} placeholder="e.g. pool.max set to 20 by v2.8.1 config change" className="mt-1 w-full rounded-md border border-slate-300 px-3 py-1.5" />
        </label>
        <label className="block text-sm">
          <span className="text-slate-600">Investigation notes</span>
          <textarea value={comment} onChange={(e) => setComment(e.target.value)} rows={2} placeholder="What you checked and what you saw" className="mt-1 w-full rounded-md border border-slate-300 px-3 py-1.5" />
        </label>
        <div className="flex flex-wrap items-center gap-2">
          <select value={hypothesisId} onChange={(e) => setHypothesisId(e.target.value)} className="rounded-md border border-slate-300 px-2 py-1.5 text-sm" aria-label="Related hypothesis">
            <option value="">Not tied to a hypothesis</option>
            {inv.hypotheses.map((h) => <option key={h.id} value={h.id}>#{h.rank} {h.title.slice(0, 60)}</option>)}
          </select>
          <Button type="submit" variant="primary" disabled={save.isPending || (!cause.trim() && !comment.trim())}>Save</Button>
          {save.isSuccess && <span className="text-sm text-emerald-700">Saved. It will be used in the next RCA draft.</span>}
        </div>
        {save.isError && <ErrorBox error={save.error} />}
      </form>
    </Card>
  );
}

export function Findings({
  inv, loading, error, evidence, onEvidence, onRun,
}: {
  inv: InvestigationDto | undefined;
  loading: boolean;
  error: unknown;
  evidence: IncidentEvidence | undefined;
  onEvidence: (ref: string) => void;
  onRun: () => void;
}) {
  if (error) return <ErrorBox error={error} />;
  if (loading) return <div className="flex items-center gap-2 text-sm text-slate-500"><Spinner /> Loading investigation…</div>;
  if (!inv) {
    return (
      <div className="space-y-4">
        {evidence && <ObservedFacts evidence={evidence} />}
        <Empty title="No investigation yet">
          <p>Run an investigation to gather evidence, search past incidents and get ranked hypotheses.</p>
          <Button variant="primary" className="mt-3" onClick={onRun}>Run investigation</Button>
        </Empty>
      </div>
    );
  }

  if (inv.status === "queued" || inv.status === "running") {
    return (
      <div className="rounded-lg border border-indigo-200 bg-indigo-50 p-6" role="status" data-testid="investigation-running">
        <div className="flex items-center gap-3 text-indigo-900">
          <Spinner className="h-5 w-5" />
          <div>
            <div className="font-medium">Investigation in progress</div>
            <div className="text-sm text-indigo-800">{inv.stage ?? "Queued"}…</div>
          </div>
        </div>
      </div>
    );
  }

  if (inv.status === "failed") {
    return (
      <div className="space-y-4">
        <ErrorBox error={new Error(inv.error_message ?? "The investigation failed.")} onRetry={onRun} />
        {evidence && <ObservedFacts evidence={evidence} />}
      </div>
    );
  }

  return (
    <div className="space-y-4" data-testid="investigation-results">
      {evidence && <ObservedFacts evidence={evidence} />}

      <section className="rounded-lg border border-violet-200 bg-violet-50/50 p-4">
        <div className="mb-2 flex flex-wrap items-center gap-2">
          <h3 className="text-sm font-semibold text-slate-800">Inferred analysis</h3>
          <AiBadge>{inv.llm_mode === "live" ? `AI-generated (${inv.model_name ?? "Claude"}) — not confirmed` : "Recorded analysis — not confirmed"}</AiBadge>
          <span className="ml-auto text-xs text-slate-500">
            {inv.evidence_count} evidence records · completed {formatTime(inv.completed_at)} · prompt {inv.prompt_version}
          </span>
        </div>
        <p className="text-sm text-slate-800">{inv.summary}</p>
        {inv.question && <p className="mt-2 text-xs text-slate-500">Question asked: “{inv.question}”</p>}
      </section>

      {inv.insufficient_evidence && (
        <div className="rounded-lg border border-amber-300 bg-amber-50 p-4 text-sm text-amber-900" data-testid="insufficient-evidence">
          <strong>Insufficient evidence.</strong> The available records don't support a root-cause hypothesis. Gather more logs, metrics or change history and run the investigation again.
        </div>
      )}

      <div className="space-y-3">
        {inv.hypotheses.map((h) => <HypothesisCard key={h.id} h={h} investigationId={inv.id} onEvidence={onEvidence} />)}
      </div>

      {(inv.limitations.length > 0 || inv.validation_issues.length > 0) && (
        <div className="grid gap-4 md:grid-cols-2">
          {inv.limitations.length > 0 && (
            <Card title="Limitations">
              <ul className="list-disc space-y-1 pl-5 text-sm text-slate-700">{inv.limitations.map((l) => <li key={l}>{l}</li>)}</ul>
            </Card>
          )}
          {inv.validation_issues.length > 0 && (
            <Card title="Validation notes">
              <ul className="list-disc space-y-1 pl-5 text-sm text-slate-600">{inv.validation_issues.map((l) => <li key={l}>{l}</li>)}</ul>
            </Card>
          )}
        </div>
      )}

      <TracePanel trace={inv.trace} calls={inv.tool_calls} live={inv.llm_mode === "live"} />

      <VerifiedCauseForm inv={inv} />
    </div>
  );
}

const fmtDetail = (v: string | number | boolean) => (typeof v === "boolean" ? (v ? "yes" : "no") : String(v));

/** Dot colour per pipeline stage, matched on the stage name. */
const STAGE_TONE: [RegExp, string][] = [
  [/^Gather/, "bg-slate-500"],
  [/^Search/, "bg-violet-500"],
  [/^Baseline/, "bg-gradient-to-br from-sky-500 to-indigo-600"],
  [/^Build/, "bg-amber-500"],
  [/^Follow-up/, "bg-gradient-to-br from-violet-500 to-fuchsia-600"],
  [/^Analyse/, "bg-gradient-to-br from-indigo-500 to-violet-600"],
  [/^Validate/, "bg-emerald-500"],
];
const toneOf = (stage: string) => STAGE_TONE.find(([re]) => re.test(stage))?.[1] ?? "bg-slate-400";

/** The audit trail as a timeline: each stage, how long it took, what it reported, and every system call it made. */
function TracePanel({ trace, calls, live }: { trace: InvestigationDto["trace"]; calls: InvestigationDto["tool_calls"]; live: boolean }) {
  if (!trace.length) return null;
  const total = trace.reduce((n, t) => n + t.duration_ms, 0);
  const slowest = Math.max(1, ...trace.map((t) => t.duration_ms));
  const slowestCall = Math.max(1, ...calls.map((c) => c.duration_ms));
  return (
    <details className="rounded-lg border border-slate-200 bg-white p-4 text-sm shadow-sm" data-testid="trace-panel">
      <summary className="cursor-pointer font-medium text-slate-800">
        How this was produced{" "}
        <span className="font-normal text-slate-500">
          · {trace.length} stages · {(total / 1000).toFixed(1)} s{calls.length > 0 && ` · ${calls.length} system checks`}
        </span>
      </summary>
      <ol className="relative ml-2 mt-4 space-y-4 border-l-2 border-slate-100 pl-5">
        {trace.map((t, i) => {
          const phase = /^Baseline/.test(t.stage) ? "baseline" : /^Follow-up/.test(t.stage) ? "follow_up" : null;
          const stageCalls = phase ? calls.filter((c) => c.phase === phase) : [];
          return (
            <li key={t.stage} className="relative">
              <span className={cx("absolute -left-[31px] top-0.5 grid h-5 w-5 place-items-center rounded-full text-[10px] font-bold text-white ring-4 ring-white", toneOf(t.stage))}>{i + 1}</span>
              <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
                <span className="font-medium text-slate-800">{t.stage}</span>
                <span className="h-1.5 w-24 overflow-hidden rounded-full bg-slate-100" aria-hidden>
                  <span className={cx("block h-full rounded-full", toneOf(t.stage))} style={{ width: `${Math.max(4, (t.duration_ms / slowest) * 100)}%` }} />
                </span>
                <span className="font-mono text-xs text-slate-500">{t.duration_ms} ms</span>
              </div>
              <div className="mt-1 flex flex-wrap gap-1">
                {Object.entries(t.details).map(([k, v]) => (
                  <span key={k} className="rounded bg-slate-100 px-1.5 text-xs leading-5 text-slate-600">{k.replace(/_/g, " ")}: {fmtDetail(v)}</span>
                ))}
              </div>
              {stageCalls.length > 0 && (
                <ul className="mt-2 space-y-1">
                  {stageCalls.map((c) => (
                    <li key={c.ref} className="flex flex-wrap items-center gap-2 text-xs">
                      <span
                        className={cx("h-2.5 w-2.5 shrink-0 rounded-full", c.phase === "baseline" ? SYSTEM[c.system].dot : SYSTEM[c.system].hollow)}
                        aria-hidden
                      />
                      <span className={cx("rounded px-1.5 py-0.5 font-mono font-medium", SYSTEM[c.system].chip)}>{c.tool}</span>
                      {c.phase === "follow_up" && <span className="font-semibold text-violet-700">{live ? "Claude asked" : "follow-up"}</span>}
                      {!c.ok && <span className="rounded bg-red-100 px-1 font-semibold text-red-700">failed</span>}
                      <span className="h-1 w-16 overflow-hidden rounded-full bg-slate-100" aria-hidden>
                        <span className={cx("block h-full rounded-full", SYSTEM[c.system].bar)} style={{ width: `${Math.max(6, (c.duration_ms / slowestCall) * 100)}%` }} />
                      </span>
                      <span className="font-mono text-slate-500">{c.duration_ms} ms</span>
                      <span className="min-w-0 flex-1 truncate text-slate-600" title={c.summary}>{c.summary}</span>
                    </li>
                  ))}
                </ul>
              )}
            </li>
          );
        })}
      </ol>
    </details>
  );
}
