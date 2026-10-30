import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { Link, useNavigate } from "react-router-dom";
import { api } from "../api";
import type { ReviewState } from "@resolveai/shared";
import { age, cx, Empty, ErrorBox, formatTime, SeverityChip, Spinner, StatusChip } from "../components/ui";

const select = "rounded-md border border-slate-300 bg-white px-2 py-1.5 text-sm";

const REVIEW: Record<ReviewState, { label: string; className: string; title: string }> = {
  none: { label: "Not investigated", className: "bg-slate-100 text-slate-600", title: "No completed investigation yet" },
  pending: { label: "Needs review", className: "bg-amber-100 text-amber-900", title: "AI findings exist but no one has verified the root cause and reviewed the RCA" },
  verified: { label: "Verified", className: "bg-emerald-100 text-emerald-800", title: "Root cause verified and RCA marked reviewed by a person" },
};

export default function IncidentQueue() {
  const navigate = useNavigate();
  const [q, setQ] = useState("");
  const [severity, setSeverity] = useState("");
  const [status, setStatus] = useState("");
  const [service, setService] = useState("");
  const [review, setReview] = useState(false);

  const list = useQuery({
    queryKey: ["incidents", q, severity, status, service, review],
    queryFn: () => api.incidents({ q, severity, status, service, review: review ? "pending" : "" }),
    placeholderData: keepPreviousData,
  });

  const items = list.data?.items ?? [];
  const active = items.filter((i) => i.status !== "resolved");

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-end justify-between gap-3">
        <div>
          <h1 className="text-xl font-semibold">Incident queue</h1>
          <p className="text-sm text-slate-500">
            {list.data ? `${active.length} active · ${list.data.total} shown` : "Loading incidents…"}
          </p>
        </div>
        <div className="flex flex-wrap gap-2">
          <button
            type="button"
            aria-pressed={review}
            onClick={() => setReview((r) => !r)}
            className={cx("rounded-full border px-3 py-1 text-sm", review ? "border-amber-400 bg-amber-100 font-medium text-amber-900" : "border-slate-300 bg-white text-slate-600 hover:bg-slate-50")}
            data-testid="needs-review"
          >
            Needs review
          </button>
          <input
            type="search"
            value={q}
            onChange={(e) => setQ(e.target.value)}
            placeholder="Search key, title or service"
            aria-label="Search incidents"
            className="w-64 rounded-md border border-slate-300 bg-white px-3 py-1.5 text-sm"
          />
          <select aria-label="Severity" value={severity} onChange={(e) => setSeverity(e.target.value)} className={select}>
            <option value="">All severities</option>
            {["SEV-1", "SEV-2", "SEV-3", "SEV-4"].map((s) => <option key={s}>{s}</option>)}
          </select>
          <select aria-label="Status" value={status} onChange={(e) => setStatus(e.target.value)} className={select}>
            <option value="">All statuses</option>
            {["open", "investigating", "mitigated", "resolved"].map((s) => <option key={s} value={s}>{s}</option>)}
          </select>
          <select aria-label="Service" value={service} onChange={(e) => setService(e.target.value)} className={select}>
            <option value="">All services</option>
            {list.data?.services.map((s) => <option key={s}>{s}</option>)}
          </select>
        </div>
      </div>

      {list.isError && <ErrorBox error={list.error} onRetry={() => list.refetch()} />}
      {list.isPending && <div className="flex items-center gap-2 text-sm text-slate-500"><Spinner /> Loading incidents…</div>}
      {list.isSuccess && items.length === 0 && (
        <Empty title={review ? "Nothing waiting for review" : "No incidents match these filters"}>
          {review ? "Every investigated incident has a verified root cause and a reviewed RCA." : "Clear the search or filters, or reseed the demo data from Settings."}
        </Empty>
      )}

      {items.length > 0 && (
        <div className="overflow-x-auto rounded-lg border border-slate-200 bg-white">
          <table className="w-full text-left text-sm">
            <thead className="border-b border-slate-200 bg-slate-50 text-xs uppercase tracking-wide text-slate-500">
              <tr>
                <th className="px-4 py-2">Incident</th>
                <th className="px-4 py-2">Severity</th>
                <th className="px-4 py-2">Service</th>
                <th className="px-4 py-2">Status</th>
                <th className="px-4 py-2">Review</th>
                <th className="px-4 py-2">Started</th>
                <th className="px-4 py-2">Age</th>
                <th className="px-4 py-2" />
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100">
              {items.map((i) => (
                <tr key={i.id} className="cursor-pointer hover:bg-slate-50" onClick={() => navigate(`/incidents/${i.key}`)}>
                  <td className="px-4 py-3">
                    <div className="font-medium text-slate-900">
                      <span className="font-mono text-xs text-slate-500">{i.key}</span> {i.title}
                    </div>
                    <div className="mt-0.5 line-clamp-1 text-xs text-slate-500">{i.impact_summary}</div>
                  </td>
                  <td className="px-4 py-3"><SeverityChip severity={i.severity} /></td>
                  <td className="px-4 py-3 font-mono text-xs">{i.service_name}</td>
                  <td className="px-4 py-3"><StatusChip status={i.status} /></td>
                  <td className="whitespace-nowrap px-4 py-3">
                    <span className={cx("rounded px-1.5 py-0.5 text-xs font-medium", REVIEW[i.review_state].className)} title={REVIEW[i.review_state].title}>
                      {REVIEW[i.review_state].label}
                    </span>
                  </td>
                  <td className="whitespace-nowrap px-4 py-3 text-xs text-slate-600">{formatTime(i.started_at, true)}</td>
                  <td className="px-4 py-3 text-xs text-slate-600">{age(i.started_at)}</td>
                  <td className="px-4 py-3 text-right">
                    <Link
                      to={`/incidents/${i.key}`}
                      onClick={(e) => e.stopPropagation()}
                      className="inline-block whitespace-nowrap rounded-md bg-indigo-600 px-2.5 py-1 text-xs font-medium text-white hover:bg-indigo-700"
                    >
                      Open incident
                    </Link>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
