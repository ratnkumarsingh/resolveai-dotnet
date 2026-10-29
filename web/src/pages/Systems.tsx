import { useQuery } from "@tanstack/react-query";
import { api } from "../api";
import { StatusDot, SYSTEM, SYSTEM_ORDER, SystemTile } from "../components/systems";
import { cx, ErrorBox, formatTime, Spinner } from "../components/ui";

const STATUS_TEXT = { connected: "Connected", error: "Not working", disabled: "Turned off" } as const;

const STEPS = [
  { n: 1, title: "Baseline checks", body: "ResolveAI always runs the same read-only checks for the incident's service and time window.", tone: "from-sky-500 to-indigo-600" },
  { n: 2, title: "Follow-up checks", body: "In live mode Claude may ask a few more narrow questions, within a fixed limit.", tone: "from-violet-500 to-fuchsia-600" },
  { n: 3, title: "Analysis", body: "Every result becomes evidence (mcp:N). Findings must cite it, and numbers are checked against it.", tone: "from-emerald-500 to-teal-600" },
];

export default function Systems() {
  const systems = useQuery({ queryKey: ["systems"], queryFn: api.systems, refetchInterval: 30_000 });
  const config = useQuery({ queryKey: ["config"], queryFn: api.config });

  return (
    <div className="space-y-6">
      <section className="relative overflow-hidden rounded-2xl bg-gradient-to-br from-indigo-700 via-violet-700 to-fuchsia-700 p-6 text-white shadow-lg sm:p-8">
        <div aria-hidden className="pointer-events-none absolute -right-16 -top-16 h-56 w-56 rounded-full bg-sky-400/30 blur-3xl" />
        <div aria-hidden className="pointer-events-none absolute -bottom-20 left-1/3 h-56 w-56 rounded-full bg-emerald-400/25 blur-3xl" />
        <div className="relative">
          <p className="text-xs font-semibold uppercase tracking-widest text-indigo-100">One MCP server per system</p>
          <h1 className="mt-1 text-2xl font-bold sm:text-3xl">Systems the agent can check</h1>
          <p className="mt-2 max-w-2xl text-sm text-indigo-50">
            Each system is a separate, read-only MCP server. There is no tool that runs arbitrary SQL or commands, and nothing here can change a system.
          </p>
          <div className="mt-4 flex flex-wrap gap-2">
            {SYSTEM_ORDER.map((s) => {
              const st = systems.data?.systems.find((x) => x.system === s);
              return (
                <span key={s} className="inline-flex items-center gap-1.5 rounded-full bg-white/15 px-2.5 py-1 text-xs font-medium ring-1 ring-white/25 backdrop-blur">
                  {st ? <StatusDot status={st.status} /> : <span className="h-2.5 w-2.5 rounded-full bg-white/40" />}
                  {SYSTEM[s].label}
                </span>
              );
            })}
          </div>
        </div>
      </section>

      {systems.isPending && <div className="flex items-center gap-2 text-sm text-slate-500"><Spinner /> Checking systems…</div>}
      {systems.isError && <ErrorBox error={systems.error} onRetry={() => systems.refetch()} />}
      {systems.data && !systems.data.enabled && (
        <div className="rounded-lg border border-amber-300 bg-amber-50 p-4 text-sm text-amber-900">
          MCP systems are turned off on this server (MCP_ENABLED=false). Investigations use the demo dataset only.
        </div>
      )}

      {systems.data && systems.data.systems.length > 0 && (
        <div className="grid gap-4 md:grid-cols-2" data-testid="systems-grid">
          {SYSTEM_ORDER.map((sys) => {
            const st = systems.data.systems.find((x) => x.system === sys);
            if (!st) return null;
            const s = SYSTEM[sys];
            return (
              <article key={sys} className={cx("flex flex-col overflow-hidden rounded-xl border border-slate-200 bg-white shadow-sm", st.status === "disabled" && "opacity-90")} data-testid={`system-${sys}`}>
                <div className={cx("h-1.5", s.bar)} />
                <div className="flex flex-1 flex-col gap-3 p-4">
                  <header className="flex items-start gap-3">
                    <SystemTile system={sys} size="lg" />
                    <div className="min-w-0 flex-1">
                      <h2 className="text-base font-semibold text-slate-900">{st.label}</h2>
                      <p className="text-xs text-slate-500">{s.label} · server <span className="font-mono">{st.server}</span></p>
                    </div>
                    <span className={cx("inline-flex items-center gap-1.5 rounded-full px-2 py-0.5 text-xs font-semibold ring-1",
                      st.status === "connected" ? "bg-emerald-50 text-emerald-800 ring-emerald-200"
                        : st.status === "error" ? "bg-red-50 text-red-800 ring-red-200" : "bg-slate-50 text-slate-600 ring-slate-200")}>
                      <StatusDot status={st.status} pulse={st.status === "connected"} />
                      {STATUS_TEXT[st.status]}
                    </span>
                  </header>
                  <p className="text-sm text-slate-700">{s.blurb}</p>
                  {st.detail && (
                    <p className={cx("rounded-md px-2.5 py-1.5 text-xs", st.status === "error" ? "bg-red-50 text-red-800" : "bg-slate-50 text-slate-600")}>{st.detail}</p>
                  )}
                  {st.tools.length > 0 && (
                    <div className="flex flex-wrap gap-1.5">
                      {st.tools.map((t) => (
                        <span key={t.name} title={t.description} className={cx("rounded-md px-2 py-0.5 font-mono text-xs font-medium", s.chip)}>{t.name}</span>
                      ))}
                    </div>
                  )}
                  <p className="mt-auto pt-1 text-[11px] text-slate-400">
                    {st.tools.length} read-only tool{st.tools.length === 1 ? "" : "s"} · checked {formatTime(st.checked_at, true)}
                  </p>
                </div>
              </article>
            );
          })}
        </div>
      )}

      <section className="rounded-xl border border-slate-200 bg-white p-5 shadow-sm">
        <h2 className="text-sm font-semibold text-slate-800">How an investigation uses them</h2>
        <ol className="mt-3 grid gap-3 md:grid-cols-3">
          {STEPS.map((step) => (
            <li key={step.n} className="flex gap-3 rounded-lg bg-slate-50 p-3">
              <span className={cx("grid h-8 w-8 shrink-0 place-items-center rounded-full bg-gradient-to-br text-sm font-bold text-white shadow-sm", step.tone)}>{step.n}</span>
              <div>
                <div className="text-sm font-semibold text-slate-800">{step.title}</div>
                <p className="text-xs text-slate-600">
                  {step.body}
                  {step.n === 2 && config.data && <> Limit: <strong>{config.data.max_follow_up_calls}</strong> calls per run.</>}
                </p>
              </div>
            </li>
          ))}
        </ol>
      </section>
    </div>
  );
}
