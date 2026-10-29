# ResolveAI — Incident Investigation Copilot (hackathon MVP)

ResolveAI pulls an incident's logs, deployments, metrics, runbooks and past incidents into one workflow. It then produces **evidence-backed root-cause hypotheses**, finds **similar past incidents**, suggests **safe, read-only checks** and drafts an **RCA for human review**.

> **Demo dataset only.** Every incident, log, deployment and metric is synthetic seed data. No observability integrations are connected, and the UI says so on every page. AI output is always labelled as a draft or recommendation. Nothing is executed automatically.

## Quick start

```bash
npm install
npm run seed        # optional; the API seeds an empty database on first start
npm run dev         # API on http://127.0.0.1:8787, web on http://localhost:5173
```

Open http://localhost:5173 and pick **INC-1042**.

Requires Node 22.13+ (it uses the built-in `node:sqlite`; nothing native to compile).

## Run in Docker (local deployment)

One container serves both the API and the built web app. The port is published on `127.0.0.1` only, so nothing else on your network can reach it. Start Docker Desktop first.

```bash
cp .env.example .env              # optional: add ANTHROPIC_API_KEY and LIVE_ACCESS_CODE for live runs
docker compose up -d --build      # build the image and start it in the background
docker compose ps                 # wait for "healthy"
```

Open http://localhost:8787.

| Task | Command |
| --- | --- |
| Follow logs | `docker compose logs -f` |
| Stop (keeps data) | `docker compose stop` |
| Upgrade after `git pull` | `docker compose up -d --build` |
| Back up the database | `docker compose cp resolveai:/app/data ./backup` |
| Reset demo data | Settings → Reset demo data, or `docker compose down -v` (deletes the volume; the next start reseeds) |
| Remove everything | `docker compose down -v --rmi local` |

- **Data:** SQLite lives in the named volume `resolveai-data` and survives restarts and rebuilds.
- **Settings:** `.env` is read when present. The compose file pins `HOST`, `API_PORT`, `DB_PATH` and `WEB_DIST` for the container, so `.env` values for those are ignored there.
- **Demo reset:** callers inside Docker arrive from the bridge network rather than loopback, so the compose file sets `DEMO_RESET_ANY_IP=true`. This is safe only because of the loopback-only port binding; don't publish the port more widely without removing it.
- **Testing the container:** `BASE_URL=http://localhost:8787 npm run e2e` runs the browser test against it.

## Systems the agent can check (MCP)

Each system ResolveAI investigates has its own **read-only MCP server** in `mcp/`. The API starts them as child processes over stdio when it boots and lists them on the **Systems** page.

| System | Server | Tools |
| --- | --- | --- |
| Database | `mcp/src/sqlserver` (SQL Server) | `db_health`, `db_blocking`, `db_long_queries`, `db_deadlocks`, `db_errors`, `db_space`, `db_failed_jobs` |
| Files & logs | `mcp/src/files` | `files_search`, `files_tail`, `files_recent_changes`, `files_disk_space`, `files_check` |
| Deploys & Git | `mcp/src/deploys` | `deploys_recent`, `deploys_change_summary` |
| Metrics & host | `mcp/src/metrics` | `metrics_series`, `metrics_list`, `metrics_host` |

**How an investigation uses them**
1. **Baseline checks.** ResolveAI always runs the same checks for the incident's service and time window: errors and warnings in its log folders, recent deploys, its key metrics, and (if it has a database) health, blocking, errors and deadlocks.
2. **Follow-up checks.** In live mode Claude may then make up to `MCP_MAX_FOLLOWUP_CALLS` (default 8) extra calls, through the SDK tool runner. The limit is enforced by ResolveAI, not by the model. Recorded mode replays a fixed set of follow-ups so the demo shows them for free.
3. **Analysis.** Every result becomes an evidence item `mcp:N`. It is redacted like any other evidence, the findings must cite it, and model-written numbers are checked against it. Each call is stored (`tool_calls` table) and shown in *System checks* and in the trace.

**Read-only by design**
- No tool runs arbitrary SQL or shell commands; each is a fixed, parameterised check. The hub only offers tools marked `readOnlyHint` and ignores anything else a server exposes.
- Files: only inside `files.allowed_roots`; `..` and symlink/junction escapes are refused, binary files are skipped, and output is capped.
- Git: `execFile` with fixed arguments, repos only from config, commit IDs must be hex.
- SQL Server: every query runs as `EXECUTE AS LOGIN = 'resolveai_reader'`, a login with only `VIEW SERVER STATE` and `db_datareader`. So the server enforces read-only even when you connect with an admin Windows account.
- Each call has a time limit, a 24-hour window cap and a row/size cap. A server that fails to start shows as *Not working* and never stops the API.

**Configure** in `mcp.config.json` (copy `mcp.config.example.json`; the example is used when there is none). It maps each service to its log folders, git repo, database and key metrics, and sets the allowed folders. Secrets stay in `.env`.

**SQL Server (off by default).** On this machine (Windows sign-in, ODBC Driver 17):
```bash
sqlcmd -S localhost -E -C -i demo-systems/sqlserver/setup.sql      # demo DB, resolveai_reader, resolveai_errors XE session
# then in mcp.config.json: "sqlserver": { "enabled": true, "driver": "msnodesqlv8", "execute_as": "resolveai_reader" }
```
To see a live blocking chain for INC-1046, run `demo-systems/sqlserver/scenario-blocking.sql` in two terminals (instructions are in the file; it rolls back), then investigate INC-1046. `teardown.sql` removes everything. In Docker, use `"driver": "tedious"` with a SQL login (`MSSQL_USER`/`MSSQL_PASSWORD`) over TCP.

**Use them from Claude Code or Claude Desktop:** copy `.mcp.json.example` to `.mcp.json`.

**Adding a system:** add a folder under `mcp/src/<name>/` with a `tools.ts` that registers tools through `readOnlyTool()` (it adds the timeout and error handling) and a `server.ts` entry. Then add it to `SERVER_DEFS` in `mcp/src/index.ts`, a colour in `web/src/components/systems.tsx`, and its baseline checks in `api/src/mcp/baseline.ts`.

## Environment

Copy `.env.example` to `.env`. All variables are server-side; none use the `VITE_` prefix, so none can reach the browser bundle.

| Variable | Default | Purpose |
| --- | --- | --- |
| `ANTHROPIC_API_KEY` | empty | Claude API key. Empty means recorded mode. |
| `LLM_MODE` | `recorded` | `live` calls Claude; `recorded` replays fixtures and rules offline. |
| `CLAUDE_MODEL` | `claude-opus-5-5` | Model for investigations and RCA drafts. |
| `LIVE_ACCESS_CODE` | empty | With an API key, lets a run opt into live Claude while `LLM_MODE` stays `recorded`. The browser sends it from Settings → Access code. |
| `LIVE_DAILY_LIMIT` | `20` | Maximum live model calls (investigations + RCA drafts) per UTC day; further live requests get HTTP 429. |
| `DEMO_MODE` | `true` | Enables `POST /api/demo/reset` (from localhost, or with the access code). |
| `DB_PATH` | `../data/resolveai.db` | SQLite file, relative to `api/`. |
| `HOST` | `127.0.0.1` | Listen address. Docker uses `0.0.0.0` inside the container. |
| `API_PORT` | `8787` | API port. |
| `WEB_DIST` | empty | Serve a built web app from the API (set in Docker). |
| `MCP_ENABLED` | `true` | Start the MCP systems and check them during investigations. |
| `MCP_MAX_FOLLOWUP_CALLS` | `8` | Most extra tool calls Claude may make per investigation (0–20). |
| `RESOLVEAI_MCP_CONFIG` | empty | Path to the MCP config; defaults to `mcp.config.json`, then the example. |
| `MSSQL_USER` / `MSSQL_PASSWORD` | empty | SQL Server reader login for the `tedious` driver only. |

### LLM modes

- **recorded** (default): deterministic and runs without network access. INC-1042 replays a recorded analysis; other incidents get a rule-based analysis built from the same evidence packet. Both go through the same server-side validation as live output. Use this mode for a demo that must behave the same way every time.
- **live on request** (recommended for shared demos): keep `LLM_MODE=recorded`, set `ANTHROPIC_API_KEY` and `LIVE_ACCESS_CODE`. Everyone gets recorded mode; a person with the code can tick *Use live Claude* on an incident. An RCA is drafted in the same mode as its investigation. Live calls are capped by `LIVE_DAILY_LIMIT`.
- **live**: the server calls Claude through `@anthropic-ai/sdk` with structured JSON output (`output_config.format`, effort `medium`) and server-side refusal fallback (`fallbacks: "default"`). The response is then validated against the evidence packet.

## Demo script (3–4 minutes)

1. The **incident queue** shows active incidents. Open **INC-1042**.
2. On **Signals & timeline**, point out that HTTP 500s rose from 0.2% to 28% and the DB pool rose from 6 to 20. **Deployments** shows payment-api v2.8.1 rolling out four minutes before the incident started.
3. Click **Run investigation**. The loading state shows each backend stage.
4. On **AI findings**, the *Observed facts* (computed from records) are kept apart from the *Inferred analysis* (model output). Click an evidence chip such as `pool.max=20` to jump to the source log line.
5. **Similar incidents** shows INC-0881, a pool-size regression after a config change. Open its historical RCA.
6. **Recommended checks** list what an engineer should verify and what signal to expect. No action is executed.
7. Open **How this was produced** under the findings: every pipeline stage with its timing and counts (records gathered, redactions, citations removed, unverified numbers).
8. Record a verified root cause, then go to **RCA**: **Generate RCA draft**, edit a field, **Save & mark reviewed**, then **Copy Markdown** or **Download .md**.
9. Back on the queue, **Needs review** lists incidents whose AI findings no one has confirmed yet; INC-1042 now shows **Verified**.
10. **Settings → Reset demo data** returns everything to a known state for the next run.

Other seeded cases:
- **INC-1043**: insufficient evidence. The system says so rather than inventing a cause.
- **INC-1044**: contains a prompt-injection log line and a fake password. The line is treated as data, and the secret is redacted before analysis.

## How it works

```
web (React + Vite)  ──/api──▶  api (Fastify)  ──▶  SQLite (node:sqlite, FTS5)
                                   │
                                   ├─ evidence.ts   deterministic gathering (time window, same-service deploys)
                                   ├─ similar.ts    FTS5 bm25 search over past incidents (no model choice)
                                   ├─ packet.ts     sanitise → dedupe → bound (≤40 log groups, ≤24k chars), typed IDs
                                   ├─ llm/          Claude (live) or recorded provider
                                   └─ validate.ts   drop unknown citations, unsafe checks, clip sizes, score + rank
```

- **Typed evidence IDs** (`log:`, `dep:`, `met:`, `tl:`, `doc:`, `hist:`) are given to the model. Any citation the server can't find in the packet is removed and recorded under *Validation notes*. A hypothesis left with no valid evidence is marked **speculative** and forced to low confidence.
- **Ranking score** = `2 × confidence weight (1/2/3) + min(supporting citations, 5) − counter-evidence count`. It is only used for ordering, never shown as a probability.
- **Similar incidents are retrieved by code.** The model only writes the explanation for each retrieved candidate.
- **Sanitisation** redacts bearer tokens, API keys, `password=`-style secrets, URL credentials, emails and card-like numbers when the packet is built. Stored logs are immutable; a trigger rejects UPDATEs.
- **Safety:** the system prompt treats all evidence as untrusted data. Recommended checks that describe destructive actions (`rm -rf`, `kubectl delete`, `DROP TABLE`, …) are stripped server-side.
- **Lifecycle:** `POST …/investigations` returns 202 and runs in the background (`queued → running → completed | failed`). The UI polls every 1.5 s. Malformed model output is retried once. Provider errors become a readable message and the incident page stays usable.
- **RCA versions:** generating again creates a new version, so edits to earlier versions are kept.
- **Numbers are checked, not trusted.** Any percentage, duration or count of 10 or more that the model writes in a hypothesis or RCA, and that does not appear in the evidence (or the engineer's own notes), is listed under *Validation notes* / *Check before publishing*. Flagged text is kept, not deleted, so a person decides.
- **Trace:** each investigation stores its stage timings and counts, shown as *How this was produced*.
- **Review state:** an incident is *Needs review* from its first completed investigation until a person records a verified root cause **and** marks the latest RCA reviewed. Re-running adds a run; earlier runs stay selectable.

### API

| Endpoint | Purpose |
| --- | --- |
| `GET /api/config` | Default mode, whether live is available, today's live usage, follow-up call limit |
| `GET /api/systems` | Each MCP system: status, detail and read-only tools |
| `GET /api/incidents?status=&severity=&service=&q=&review=&page=&page_size=` | List and filter incidents; `review=pending` is the review queue |
| `GET /api/incidents/:id` | Detail, latest investigation ID, all previous runs, RCA versions (`:id` = UUID or key) |
| `GET /api/incidents/:id/evidence` | Logs, error signals, deployments, metrics, timeline, runbooks |
| `POST /api/incidents/:id/investigations` | Start an investigation `{ question?, mode? }`, returns 202. `mode: "live"` needs the `x-access-code` header (403 without it, 429 over the daily limit) |
| `GET /api/investigations/:id` | Status, stage, hypotheses with resolved evidence, similar incidents, stage trace, every system check (`tool_calls`) |
| `POST /api/investigations/:id/feedback` | `{ hypothesis_id?, helpful?, comment?, verified_root_cause? }` |
| `POST /api/incidents/:id/rca-drafts` | Generate an RCA version from a completed investigation (same mode as the investigation) |
| `GET/PATCH /api/rca-drafts/:id` | Read or save RCA edits |
| `GET /api/rca-drafts/:id/export.md` | Markdown export |
| `GET /api/knowledge/:id` | Runbook or historical RCA document |
| `POST /api/demo/reset` | Reseed (requires `DEMO_MODE=true` and a localhost caller or the access code) |

## Tests

```bash
npm test            # Vitest: MCP servers, then pipeline + API (in-memory DB, recorded/scripted providers)
MSSQL_TEST=1 npm test -w mcp   # also run every db_* tool against local SQL Server (after setup.sql)
npm run e2e         # Playwright smoke: queue → investigation → evidence → similar → RCA edit/save/export
npm run typecheck
```

Covered: happy path, insufficient evidence, invalid evidence IDs, prompt injection, secret redaction, retry on malformed output, provider failure, RCA edit persistence and versioning, demo reset (including determinism across runs), log immutability, live access code / daily limit / reset rules, unverified-number flags, stage trace, review queue states, run history, and static serving for the Docker image. For the MCP systems: path and junction escapes, binary files and caps (files); feed + real git repo and argument injection (deploys); windows and peaks (metrics); allow-listed databases, degraded permissions and the `EXECUTE AS` wrapper (SQL Server); the hub ignoring non-read-only tools and timing out a hung one; baseline + follow-up calls cited as `mcp:N`; secrets in tool output redacted before the model; the follow-up limit; follow-up failure not failing the run; and starting the real servers as child processes.

## Known limitations

- The demo systems are synthetic (log files, a deploy feed and metric series in `demo-systems/`). SQL Server checks are real once enabled. Point `mcp.config.json` at real folders and repos to use your own.
- Suggested next systems: an HTTP endpoint probe, message-queue backlog and dead letters, Kubernetes/cloud events, and config/feature-flag changes. Metrics could read Prometheus behind the same `metrics_series` tool.
- No user accounts. The API binds to 127.0.0.1 (Docker: published on 127.0.0.1 only); the shared access code only gates paid live runs and remote reset.
- Retrieval is keyword/FTS5 only; there is no vector search.
- Investigations run in-process. A server restart marks in-flight runs as failed.
- Model confidence is qualitative and uncalibrated.

### Ideas taken from, and rejected from, Settlement Sentinel

A similar open-source project ([AvinJoseph29/Settlement-Sentinel](https://github.com/AvinJoseph29/Settlement-Sentinel)) was reviewed. Adopted: checking model-written numbers against the evidence, a per-run audit trail, a human-review queue, and keeping prior runs visible. Not adopted: automatic escalation/retry actions and Slack posts (the spec rules out autonomous actions), confidence derived from historical resolution rates (it measures how often similar incidents got fixed, not whether this diagnosis is right), and a three-database stack (SQLite FTS5 is enough at this scale).

## Deviations from the original spec

- **SQLite (`node:sqlite`) instead of Supabase**, so a reviewer can run the app from this README with no cloud account. The schema maps directly to Postgres.
- **The RCA comes from a separate call** after the investigation, instead of being part of the investigation output. This leaves one path for generating RCAs, and lets the RCA use engineer feedback.
- **Added tables** `metric_snapshots`, `timeline_events`, `evidence_links` and `investigation_similar_incidents`, which the spec's UI and API needed but its data model lacked.
