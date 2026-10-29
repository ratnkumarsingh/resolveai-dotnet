import type { SystemKind, SystemStatus, ToolCallRecord } from "@resolveai/shared";
import { cx } from "./ui";

/**
 * One colour per system, used everywhere a system appears: pills, cards, citations and the trace.
 * Text is always the -800 shade on a -50/-100 background (WCAG AA); saturated gradients carry only icons.
 */
export const SYSTEM: Record<SystemKind, {
  label: string;
  blurb: string;
  tile: string;
  soft: string;
  text: string;
  chip: string;
  dot: string;
  ring: string;
  bar: string;
  accent: string;
  /** A hollow dot: marks calls Claude chose to make. */
  hollow: string;
}> = {
  database: {
    label: "Database",
    blurb: "SQL Server health, blocking chains, long queries, deadlocks, errors and file space.",
    tile: "bg-gradient-to-br from-sky-500 to-indigo-600",
    soft: "bg-sky-50", text: "text-sky-800", chip: "bg-sky-100 text-sky-800", dot: "bg-sky-500", ring: "ring-sky-200",
    bar: "bg-gradient-to-r from-sky-400 to-indigo-500", accent: "border-l-sky-500", hollow: "bg-white ring-2 ring-sky-500",
  },
  files: {
    label: "Files & logs",
    blurb: "Search log files, read the end of a file, list recent changes, check a path and free disk space.",
    tile: "bg-gradient-to-br from-amber-500 to-orange-600",
    soft: "bg-amber-50", text: "text-amber-800", chip: "bg-amber-100 text-amber-800", dot: "bg-amber-500", ring: "ring-amber-200",
    bar: "bg-gradient-to-r from-amber-400 to-orange-500", accent: "border-l-amber-500", hollow: "bg-white ring-2 ring-amber-500",
  },
  deploys: {
    label: "Deploys & Git",
    blurb: "Releases and commits in a time window, and what a release changed (files and a short diff).",
    tile: "bg-gradient-to-br from-violet-500 to-fuchsia-600",
    soft: "bg-violet-50", text: "text-violet-800", chip: "bg-violet-100 text-violet-800", dot: "bg-violet-500", ring: "ring-violet-200",
    bar: "bg-gradient-to-r from-violet-400 to-fuchsia-500", accent: "border-l-violet-500", hollow: "bg-white ring-2 ring-violet-500",
  },
  metrics: {
    label: "Metrics & host",
    blurb: "A metric's baseline, peak and timing for a service, and the host's CPU and memory right now.",
    tile: "bg-gradient-to-br from-emerald-500 to-teal-600",
    soft: "bg-emerald-50", text: "text-emerald-800", chip: "bg-emerald-100 text-emerald-800", dot: "bg-emerald-500", ring: "ring-emerald-200",
    bar: "bg-gradient-to-r from-emerald-400 to-teal-500", accent: "border-l-emerald-500", hollow: "bg-white ring-2 ring-emerald-500",
  },
};

export const SYSTEM_ORDER: SystemKind[] = ["database", "files", "deploys", "metrics"];

const ICON: Record<SystemKind, React.ReactNode> = {
  database: (
    <>
      <ellipse cx="12" cy="5.5" rx="7" ry="2.5" />
      <path d="M5 5.5v6c0 1.4 3.1 2.5 7 2.5s7-1.1 7-2.5v-6M5 11.5v6c0 1.4 3.1 2.5 7 2.5s7-1.1 7-2.5v-6" />
    </>
  ),
  files: (
    <>
      <path d="M6 3h8l4 4v14H6z" />
      <path d="M14 3v4h4M9 12h6M9 16h6" />
    </>
  ),
  deploys: (
    <>
      <circle cx="6" cy="6" r="2.2" />
      <circle cx="6" cy="18" r="2.2" />
      <circle cx="18" cy="9" r="2.2" />
      <path d="M6 8.2v7.6M18 11.2c0 3-3 3.8-6 4.3-2.4.4-4.6 1-5.6 2" />
    </>
  ),
  metrics: <path d="M3 17l5-6 4 4 4-7 5 9M3 21h18" />,
};

export function SystemIcon({ system, className = "h-4 w-4" }: { system: SystemKind; className?: string }) {
  return (
    <svg viewBox="0 0 24 24" className={className} fill="none" stroke="currentColor" strokeWidth="1.9" strokeLinecap="round" strokeLinejoin="round" aria-hidden>
      {ICON[system]}
    </svg>
  );
}

/** A gradient tile with the system's icon, for headers and cards. */
export function SystemTile({ system, size = "md" }: { system: SystemKind; size?: "sm" | "md" | "lg" }) {
  const s = size === "lg" ? "h-12 w-12 rounded-xl" : size === "md" ? "h-9 w-9 rounded-lg" : "h-6 w-6 rounded-md";
  const i = size === "lg" ? "h-6 w-6" : size === "md" ? "h-5 w-5" : "h-3.5 w-3.5";
  return (
    <span className={cx("grid shrink-0 place-items-center text-white shadow-sm", s, SYSTEM[system].tile)}>
      <SystemIcon system={system} className={i} />
    </span>
  );
}

export function SystemChip({ system, children }: { system: SystemKind; children?: React.ReactNode }) {
  return (
    <span className={cx("inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-xs font-medium", SYSTEM[system].chip)}>
      <SystemIcon system={system} className="h-3 w-3" />
      {children ?? SYSTEM[system].label}
    </span>
  );
}

/** Evidence labels for system checks read "<system>: <tool>"; anything else isn't a system. */
export function systemOfLabel(label: string): SystemKind | null {
  const s = label.split(":")[0] as SystemKind;
  return SYSTEM_ORDER.includes(s) ? s : null;
}

const STATUS_DOT: Record<SystemStatus["status"], string> = {
  connected: "bg-emerald-500",
  error: "bg-red-500",
  disabled: "bg-slate-300",
};

export function StatusDot({ status, pulse }: { status: SystemStatus["status"]; pulse?: boolean }) {
  return (
    <span className="relative inline-flex h-2.5 w-2.5">
      {pulse && status === "connected" && <span className="absolute inline-flex h-full w-full rounded-full bg-emerald-400 opacity-60 motion-safe:animate-ping" />}
      <span className={cx("relative inline-flex h-2.5 w-2.5 rounded-full", STATUS_DOT[status])} />
    </span>
  );
}

/**
 * "Systems checked" strip on an incident: a pill per system with how many checks it answered. While a run
 * is going, systems that haven't answered yet pulse.
 */
export function SystemsStrip({ calls, statuses, running }: { calls: ToolCallRecord[]; statuses: SystemStatus[]; running: boolean }) {
  if (statuses.length === 0) return null;
  return (
    <div className="flex flex-wrap items-center gap-2" data-testid="systems-strip">
      <span className="text-xs font-semibold uppercase tracking-wide text-slate-500">Systems checked</span>
      {SYSTEM_ORDER.map((system) => {
        const status = statuses.find((s) => s.system === system);
        const mine = calls.filter((c) => c.system === system);
        const failed = mine.filter((c) => !c.ok).length;
        const off = !status || status.status !== "connected";
        const waiting = running && !off && mine.length === 0;
        return (
          <span
            key={system}
            title={off ? status?.detail ?? "Not configured" : `${mine.length} check(s)${failed ? `, ${failed} failed` : ""}`}
            className={cx(
              "inline-flex items-center gap-1.5 rounded-full py-1 pl-1 pr-2.5 text-xs font-medium ring-1",
              off ? "bg-slate-50 text-slate-500 ring-slate-200" : cx(SYSTEM[system].soft, SYSTEM[system].text, SYSTEM[system].ring),
              waiting && "motion-safe:animate-pulse",
            )}
          >
            {off ? (
              <span className="grid h-5 w-5 place-items-center rounded-full bg-slate-200 text-slate-500"><SystemIcon system={system} className="h-3 w-3" /></span>
            ) : (
              <span className={cx("grid h-5 w-5 place-items-center rounded-full text-white", SYSTEM[system].tile)}><SystemIcon system={system} className="h-3 w-3" /></span>
            )}
            {SYSTEM[system].label}
            <span className={cx("rounded-full px-1.5 text-[11px] font-semibold", off ? "bg-slate-200 text-slate-600" : "bg-white/80")}>
              {off ? "off" : waiting ? "…" : mine.length}
            </span>
            {failed > 0 && <span className="rounded-full bg-red-100 px-1.5 text-[11px] font-semibold text-red-700">{failed} failed</span>}
          </span>
        );
      })}
    </div>
  );
}
