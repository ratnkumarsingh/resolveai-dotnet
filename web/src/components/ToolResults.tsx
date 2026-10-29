import type { ToolCallRecord } from "@resolveai/shared";
import { useState } from "react";
import type { InvestigationDto } from "../api";
import { rowId } from "./EvidenceTabs";
import { SYSTEM, SYSTEM_ORDER, SystemTile } from "./systems";
import { cx, Empty } from "./ui";

const MAX_COLUMNS = 7;
const FIRST_ROWS = 8;

const cell = (v: unknown) => (v === null || v === undefined ? "—" : typeof v === "object" ? JSON.stringify(v) : String(v));

function RowsTable({ rows }: { rows: Record<string, unknown>[] }) {
  const [all, setAll] = useState(false);
  // Columns in first-seen order; long text (query text, patches) goes last so short columns stay readable.
  const keys = [...new Set(rows.flatMap((r) => Object.keys(r)))];
  const long = (k: string) => rows.some((r) => cell(r[k]).length > 60);
  const columns = [...keys.filter((k) => !long(k)), ...keys.filter(long)].slice(0, MAX_COLUMNS);
  const shown = all ? rows : rows.slice(0, FIRST_ROWS);
  return (
    <div className="overflow-x-auto rounded-md border border-slate-200 bg-white">
      <table className="w-full text-left text-xs">
        <thead className="bg-slate-50 text-[11px] uppercase tracking-wide text-slate-500">
          <tr>{columns.map((c) => <th key={c} className="whitespace-nowrap px-2 py-1.5 font-semibold">{c.replace(/_/g, " ")}</th>)}</tr>
        </thead>
        <tbody className="divide-y divide-slate-100 font-mono">
          {shown.map((r, i) => (
            <tr key={i} className="align-top">
              {columns.map((c) => (
                <td key={c} className={cx("px-2 py-1 text-slate-700", long(c) ? "min-w-64 whitespace-pre-wrap break-words" : "whitespace-nowrap")}>{cell(r[c])}</td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
      {rows.length > FIRST_ROWS && (
        <button onClick={() => setAll((a) => !a)} className="w-full border-t border-slate-100 py-1 text-xs font-medium text-indigo-700 hover:bg-slate-50">
          {all ? "Show fewer" : `Show all ${rows.length} rows`}
        </button>
      )}
      {keys.length > MAX_COLUMNS && <p className="border-t border-slate-100 px-2 py-1 text-[11px] text-slate-500">{keys.length - MAX_COLUMNS} more column(s) in the raw JSON.</p>}
    </div>
  );
}

function ToolCard({ call, focused }: { call: ToolCallRecord; focused: boolean }) {
  const [raw, setRaw] = useState(false);
  const s = SYSTEM[call.system];
  const args = Object.entries(call.args);
  return (
    <article id={rowId(call.ref)} className={cx("rounded-lg border border-l-4 border-slate-200 bg-white p-3 shadow-sm", s.accent, focused && "flash")} data-testid="tool-result">
      <div className="flex flex-wrap items-center gap-2">
        <span className={cx("rounded-md px-2 py-0.5 font-mono text-xs font-semibold", s.chip)}>{call.tool}</span>
        {call.phase === "follow_up" ? (
          <span className="inline-flex items-center gap-1 rounded-full bg-violet-100 px-2 py-0.5 text-[11px] font-semibold text-violet-800" title="Claude chose to make this call">
            <svg viewBox="0 0 16 16" className="h-3 w-3" fill="currentColor" aria-hidden><path d="M8 0l1.8 5.2L15 7l-5.2 1.8L8 14l-1.8-5.2L1 7l5.2-1.8z" /></svg>
            Follow-up
          </span>
        ) : (
          <span className="rounded-full bg-slate-100 px-2 py-0.5 text-[11px] font-semibold text-slate-600" title="ResolveAI runs this check on every investigation">Baseline</span>
        )}
        {!call.ok && <span className="rounded-full bg-red-100 px-2 py-0.5 text-[11px] font-semibold text-red-700">failed</span>}
        {call.truncated && <span className="rounded-full bg-amber-100 px-2 py-0.5 text-[11px] font-semibold text-amber-800" title="More results existed than the limit">truncated</span>}
        <span className="ml-auto flex items-center gap-2 text-[11px] text-slate-500">
          <span className="rounded bg-slate-100 px-1.5 py-0.5 font-mono font-medium text-slate-600">{call.ref}</span>
          <span>{call.rows.length} row{call.rows.length === 1 ? "" : "s"}</span>
          <span className="font-mono">{call.duration_ms} ms</span>
        </span>
      </div>
      {args.length > 0 && (
        <div className="mt-1.5 flex flex-wrap gap-1">
          {args.map(([k, v]) => (
            <span key={k} className="max-w-full truncate rounded bg-slate-50 px-1.5 py-0.5 font-mono text-[11px] text-slate-600 ring-1 ring-slate-200">
              {k}=<span className="text-slate-800">{cell(v)}</span>
            </span>
          ))}
        </div>
      )}
      <p className={cx("mt-2 text-sm", call.ok ? "text-slate-800" : "text-red-800")}>{call.summary}</p>
      {call.rows.length > 0 && (
        <div className="mt-2 space-y-1">
          {raw ? <pre className="max-h-80 overflow-auto rounded-md bg-slate-900 p-3 text-[11px] leading-relaxed text-slate-100">{JSON.stringify(call.rows, null, 2)}</pre> : <RowsTable rows={call.rows} />}
          <button onClick={() => setRaw((r) => !r)} className="text-xs font-medium text-indigo-700 hover:underline">{raw ? "Show as table" : "Show raw JSON"}</button>
        </div>
      )}
    </article>
  );
}

/** Every system check of a run, grouped by system in a fixed order. */
export function ToolResultsTab({ inv, focus }: { inv: InvestigationDto | undefined; focus: string | null }) {
  const calls = inv?.tool_calls ?? [];
  if (!inv) return <Empty title="No investigation yet">Run an investigation to check the database, files, deploys and metrics.</Empty>;
  if (calls.length === 0) {
    return <Empty title="No system checks in this run">The MCP systems were off or nothing is configured for this service. See the Systems page.</Empty>;
  }
  return (
    <div className="space-y-6" data-testid="tool-results">
      <p className="text-xs text-slate-500">
        Raw results from each system, exactly as returned. Secrets are redacted before anything is sent to the model; the IDs (mcp:N) are what the findings cite.
      </p>
      {SYSTEM_ORDER.filter((sys) => calls.some((c) => c.system === sys)).map((sys) => {
        const mine = calls.filter((c) => c.system === sys);
        return (
          <section key={sys} className="space-y-2">
            <header className={cx("flex items-center gap-3 rounded-lg px-3 py-2 ring-1", SYSTEM[sys].soft, SYSTEM[sys].ring)}>
              <SystemTile system={sys} />
              <div>
                <h3 className={cx("text-sm font-semibold", SYSTEM[sys].text)}>{SYSTEM[sys].label}</h3>
                <p className="text-xs text-slate-600">
                  {mine.length} check{mine.length === 1 ? "" : "s"} · {mine.filter((c) => c.phase === "follow_up").length} follow-up
                  {mine.some((c) => !c.ok) && <> · <span className="font-medium text-red-700">{mine.filter((c) => !c.ok).length} failed</span></>}
                </p>
              </div>
            </header>
            <div className="space-y-2">{mine.map((c) => <ToolCard key={c.ref} call={c} focused={focus === c.ref} />)}</div>
          </section>
        );
      })}
    </div>
  );
}
