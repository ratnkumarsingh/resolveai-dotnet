import { useQuery } from "@tanstack/react-query";
import { Link, NavLink, Route, Routes } from "react-router-dom";
import { api } from "./api";
import { cx } from "./components/ui";
import IncidentDetail from "./pages/IncidentDetail";
import IncidentQueue from "./pages/IncidentQueue";
import Settings from "./pages/Settings";
import Systems from "./pages/Systems";

export default function App() {
  const config = useQuery({ queryKey: ["config"], queryFn: api.config });

  return (
    <div className="min-h-screen">
      <div aria-hidden className="h-1 bg-gradient-to-r from-sky-500 via-violet-500 via-60% to-emerald-500" />
      <header className="border-b border-slate-200 bg-white">
        <div className="mx-auto flex max-w-7xl flex-wrap items-center gap-x-6 gap-y-2 px-4 py-3">
          <Link to="/" className="flex items-center gap-2 font-semibold text-slate-900">
            <span className="grid h-7 w-7 place-items-center rounded-md bg-gradient-to-br from-indigo-600 to-violet-600 text-white shadow-sm">
              <svg viewBox="0 0 32 32" className="h-4 w-4" aria-hidden>
                <path d="M7 17l6 6 12-13" stroke="currentColor" strokeWidth="4" fill="none" strokeLinecap="round" strokeLinejoin="round" />
              </svg>
            </span>
            ResolveAI
          </Link>
          <nav className="flex gap-1 text-sm">
            {[["/", "Incidents"], ["/systems", "Systems"], ["/settings", "Settings & demo data"]].map(([to, label]) => (
              <NavLink
                key={to}
                to={to}
                end
                className={({ isActive }) => cx("rounded-md px-3 py-1.5", isActive ? "bg-slate-100 font-medium text-slate-900" : "text-slate-600 hover:text-slate-900")}
              >
                {label}
              </NavLink>
            ))}
          </nav>
          <div className="ml-auto flex items-center gap-2 text-xs">
            <span className="rounded-full bg-amber-100 px-2 py-1 font-medium text-amber-900" title="All incidents, logs and metrics are synthetic seed data">
              Data source: Demo dataset
            </span>
            {config.data && (
              <span
                className={cx("rounded-full px-2 py-1 font-medium", config.data.llm_mode === "live" ? "bg-emerald-100 text-emerald-800" : "bg-slate-200 text-slate-700")}
                title={config.data.llm_mode === "live" ? "Investigations call Claude" : "Investigations replay recorded/rule-based analysis; no model is called"}
              >
                LLM: {config.data.llm_mode === "live" ? `live · ${config.data.model}` : config.data.live_available ? "recorded · live on request" : "recorded (offline)"}
              </span>
            )}
          </div>
        </div>
      </header>
      <main className="mx-auto max-w-7xl px-4 py-6">
        <Routes>
          <Route path="/" element={<IncidentQueue />} />
          <Route path="/incidents/:id" element={<IncidentDetail />} />
          <Route path="/systems" element={<Systems />} />
          <Route path="/settings" element={<Settings />} />
          <Route path="*" element={<p className="text-slate-600">Page not found. <Link className="text-indigo-600 underline" to="/">Back to incidents</Link></p>} />
        </Routes>
      </main>
    </div>
  );
}
