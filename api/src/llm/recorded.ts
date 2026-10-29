import type { InvestigationOutput, LlmHypothesis, RcaOutput } from "@resolveai/shared";
import { stableId } from "../db/seed.js";
import type { EvidencePacket, PacketItem } from "../pipeline/packet.js";
import type { CallTool, FollowUpTool, LlmProvider, LlmResult, RcaInput } from "./provider.js";

/**
 * Offline provider for deterministic demos and tests. INC-1042 replays a recorded analysis; other
 * incidents get a simple rule-based analysis built only from packet items. Either way the output goes
 * through the same validation as a live model response.
 */
export class RecordedProvider implements LlmProvider {
  readonly mode = "recorded" as const;
  readonly model = null;

  constructor(private readonly delayMs = 1200) {}

  async investigate(packet: EvidencePacket): Promise<LlmResult<InvestigationOutput>> {
    await sleep(this.delayMs);
    const fixture = FIXTURES[packet.incident.key];
    return { output: fixture ? citeToolResults(fixture(packet), packet) : heuristicInvestigation(packet), model: null };
  }

  /** Replays the follow-up checks a model would plausibly ask for, so the demo shows them without a model call. */
  async followUp(packet: EvidencePacket, tools: FollowUpTool[], call: CallTool, maxCalls: number) {
    const offered = new Set(tools.map((t) => t.name));
    const plan = (FOLLOW_UPS[packet.incident.key] ?? []).filter((c) => offered.has(c.tool)).slice(0, maxCalls);
    for (const c of plan) await call(c.tool, c.args);
    return {};
  }

  async draftRca(input: RcaInput): Promise<LlmResult<RcaOutput>> {
    await sleep(this.delayMs / 2);
    return { output: heuristicRca(input), model: null };
  }
}

const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

const log = (inc: string, name: string) => `log:${stableId(`log:${inc}:${name}`)}`;
const met = (inc: string, name: string) => `met:${stableId(`met:${inc}:${name}`)}`;
const dep = (name: string) => `dep:${stableId(`dep:${name}`)}`;
const hist = (name: string) => `hist:${stableId(`hist:${name}`)}`;

/** The packet ID of a system-check result, found by tool name (and optionally text in its result). */
function toolRef(packet: EvidencePacket, tool: string, contains?: RegExp): string | null {
  const item = packet.items.find((i) => i.type === "tool_result" && i.source.includes(`: ${tool}(`) && (!contains || contains.test(i.excerpt)));
  return item?.id ?? null;
}
const refs = (...xs: (string | null)[]) => xs.filter((x): x is string => !!x);

/** Follow-up checks replayed in recorded mode, as if the model had chosen them. */
const FOLLOW_UPS: Record<string, { tool: string; args: Record<string, unknown> }[]> = {
  "INC-1042": [
    { tool: "deploys_change_summary", args: { service: "payment-api", commit: "7d03b1f" } },
    { tool: "files_tail", args: { path: "payment-api/db-client.yaml", lines: 10 } },
  ],
  "INC-1045": [
    { tool: "files_tail", args: { path: "document-service/mounts.txt", lines: 10 } },
    { tool: "files_tail", args: { path: "document-service/storage.yaml", lines: 10 } },
    { tool: "deploys_change_summary", args: { service: "document-service", commit: "b62f0d9" } },
  ],
  "INC-1046": [
    { tool: "deploys_change_summary", args: { service: "order-api", commit: "c90e4a7" } },
    { tool: "db_long_queries", args: { min_seconds: 5 } },
  ],
};

/** System checks that support a recorded hypothesis, added to its citations when they were made. */
const TOOL_CITES: Record<string, { hypothesis: number; tool: string; contains?: RegExp }[]> = {
  "INC-1042": [
    { hypothesis: 0, tool: "deploys_change_summary" },
    { hypothesis: 0, tool: "files_tail", contains: /max: 20/ },
    { hypothesis: 0, tool: "metrics_series", contains: /DB pool connections in use/ },
  ],
};

function citeToolResults(output: InvestigationOutput, packet: EvidencePacket): InvestigationOutput {
  for (const c of TOOL_CITES[packet.incident.key] ?? []) {
    const ref = toolRef(packet, c.tool, c.contains);
    const h = output.hypotheses[c.hypothesis];
    if (ref && h && !h.evidence_ids.includes(ref)) h.evidence_ids.push(ref);
  }
  return output;
}

const FIXTURES: Record<string, (packet: EvidencePacket) => InvestigationOutput> = {
  "INC-1042": () => ({
    summary:
      "HTTP 500s on payment-api began about four minutes after v2.8.1 rolled out. The release moved database client settings into db-client.yaml, and the service logged pool.max=20 at startup where the 09:58 health check showed max=100. Requests then queued for connections and timed out while payments-db itself stayed healthy. This matches INC-0881, a pool-size regression after a config change. The leading hypothesis is not confirmed until an engineer compares the effective pool configuration with v2.8.0.",
    insufficient_evidence: false,
    hypotheses: [
      {
        title: "v2.8.1 config change reduced the DB connection pool (100 → 20), exhausting it under normal load",
        explanation:
          "The v2.8.1 deployment changed database client configuration. After rollout the service loaded pool.max=20, whereas the pre-deploy health check showed max=100. Within four minutes the pool was full (in_use=20, 14 waiting) and connection acquisition began timing out, which surfaced as HTTP 500s. Pool connections in use rose to exactly the new limit, while the database reported ample capacity.",
        confidence: "high",
        evidence_ids: [
          dep("pay-281"), log("INC-1042", "health"), log("INC-1042", "config"), log("INC-1042", "saturated"),
          log("INC-1042", "timeout-a"), met("INC-1042", "DB pool connections in use"), hist("hist-0881"),
        ],
        counter_evidence: ["Errors started ~4 minutes after rollout began; a traffic ramp would explain the lag but traffic volume is not in the packet."],
        unknowns: ["Whether pool.max=20 was intentional in the v2.8.1 change", "Request volume compared with the same time on previous days"],
        recommended_checks: [
          {
            step: "Compare the effective pool.max and acquireTimeoutMs between v2.8.0 and v2.8.1 (db-client.yaml and the previous config source).",
            why: "Confirms whether the release changed the pool size rather than load increasing.",
            expected_signal: "v2.8.0 resolved pool.max to 100 (or similar); v2.8.1 resolves to 20.",
          },
          {
            step: "Check the pool utilisation dashboard for every payment-api pod since 10:00.",
            why: "Pool exhaustion should be visible on all pods running v2.8.1.",
            expected_signal: "in_use pinned at max with a growing wait queue on all pods.",
          },
          {
            step: "Confirm payments-db has connection and CPU headroom for the full window.",
            why: "Rules out the database itself as the bottleneck.",
            expected_signal: "Active connections far below max_connections; CPU flat.",
          },
        ],
      },
      {
        title: "payments-db unavailable or overloaded",
        explanation:
          "Connection acquisition timeouts can be caused by a database that is slow to accept connections. The packet contains timeouts but no database-side errors.",
        confidence: "low",
        evidence_ids: [log("INC-1042", "timeout-a")],
        counter_evidence: [
          "payments-db reported active_connections=20 of max_connections=500, CPU 22% and no replication lag at 10:07.",
          "Database CPU stayed flat (21% → 23%) through the incident.",
        ],
        unknowns: ["Database slow-query log for the window"],
        recommended_checks: [
          {
            step: "Review payments-db slow-query and connection logs from 10:00 to 10:15.",
            why: "A database-side cause would show refused or slow connection handshakes.",
            expected_signal: "No refused connections or slow handshakes, which would rule this out.",
          },
        ],
      },
      {
        title: "Load increase from the checkout-web v5.2.0 release",
        explanation:
          "checkout-web deployed v5.2.0 at 09:30. If the new address autocomplete widget issued extra payment calls, increased load could exhaust the pool.",
        confidence: "low",
        evidence_ids: [dep("checkout-520")],
        counter_evidence: ["The release was 34 minutes before errors began and its change summary concerns address autocomplete, not payments."],
        unknowns: ["Payment request rate from checkout-web before and after 09:30"],
        recommended_checks: [
          {
            step: "Compare payment-api request rate from checkout-web before and after 09:30.",
            why: "Distinguishes a load increase from a capacity reduction.",
            expected_signal: "Flat request rate, which would rule this out.",
          },
        ],
      },
    ],
    similar_incidents: [
      {
        incident_id: hist("hist-0881"),
        similarity_reason:
          "Same service and the same pattern: HTTP 500s and database connection timeouts right after a configuration change, with a healthy database. INC-0881's root cause was an incorrect max connection setting.",
      },
    ],
    limitations: [
      "Request volume and per-pod pool metrics are not in the packet.",
      "The v2.8.0 configuration file itself is not available; the previous pool size comes from a health-check log line.",
      "The payment-api database connectivity runbook recommends comparing configuration with the last healthy deployment before any rollback decision.",
    ],
  }),

  "INC-1045": (packet) => {
    const errors = toolRef(packet, "files_search", /EACCES/);
    const mounts = toolRef(packet, "files_tail", /invoices-archive\s+ext4\s+ro/);
    const storage = toolRef(packet, "files_tail", /path: \/mnt\/invoices-archive/);
    const change = toolRef(packet, "deploys_change_summary", /invoices-archive/);
    return {
      summary:
        `Invoice uploads began failing about five minutes after document-service v1.4.0 moved invoice storage to /mnt/invoices-archive. Every failure is EACCES (permission denied) on a path under that folder.${mounts ? " The node's mount table shows /mnt/invoices-archive mounted read-only (ro), while the previous path /data/invoices is read-write." : ""} The leading hypothesis is that the release pointed writes at a read-only archive volume; an engineer should confirm the mount on the running pods.`,
      insufficient_evidence: false,
      hypotheses: [
        {
          title: "v1.4.0 points invoice storage at a volume that is mounted read-only",
          explanation:
            `The v1.4.0 change replaced the storage path /data/invoices with /mnt/invoices-archive. Right after rollout the service logged that the new path is mounted read-only, and every upload since has failed with EACCES on that path.${mounts ? " The captured mount table lists /mnt/invoices-archive with the ro option." : ""}`,
          confidence: mounts || storage ? "high" : "medium",
          evidence_ids: refs(dep("docs-140"), log("INC-1045", "eacces-a"), errors, mounts, storage, change),
          counter_evidence: [],
          unknowns: ["Whether the archive volume is meant to be writable by document-service", "Whether every pod has the same mount options"],
          recommended_checks: [
            {
              step: "On one document-service pod, look at the mount options for /mnt/invoices-archive and the owner of the folder.",
              why: "A read-only mount explains EACCES on every write regardless of file permissions.",
              expected_signal: "The mount shows ro, or the folder belongs to a user the service does not run as.",
            },
            {
              step: "Compare storage.yaml in v1.3.x and v1.4.0.",
              why: "Confirms the path change came from the release, not from an environment override.",
              expected_signal: "Only v1.4.0 points at /mnt/invoices-archive.",
            },
          ],
        },
        {
          title: "File ownership or permissions changed on the archive folder",
          explanation:
            "EACCES can also mean the service user lacks write permission on a writable volume, for example after a folder was recreated by another user.",
          confidence: "low",
          evidence_ids: refs(log("INC-1045", "eacces-a")),
          counter_evidence: mounts ? ["The mount table shows the volume itself is read-only, which already explains the failures."] : [],
          unknowns: ["Folder owner and mode on the archive volume"],
          recommended_checks: [
            {
              step: "Check the owner and mode of /mnt/invoices-archive/2026/10/27 on a pod.",
              why: "Separates a permissions problem from a read-only mount.",
              expected_signal: "A different owner or a mode without write for the service user.",
            },
          ],
        },
      ],
      similar_incidents: [],
      limitations: [
        "Recorded mode: this analysis was replayed, not produced by a live model call.",
        ...(mounts ? [] : ["The mount table was not checked, so a read-only mount is inferred from the startup warning only."]),
      ],
    };
  },

  "INC-1046": (packet) => {
    const errors = toolRef(packet, "files_search", /1222/);
    const change = toolRef(packet, "deploys_change_summary", /reconcileOrders/);
    const blocking = toolRef(packet, "db_blocking", /blocked request\(s\) behind/);
    const dbErrors = toolRef(packet, "db_errors", /1222/);
    const health = toolRef(packet, "db_health");
    const live = blocking ? " ResolveAI's live check of SQL Server also found a blocking chain on the order database right now." : "";
    return {
      summary:
        `Order placement started failing with SQL Server lock timeouts (error 1222) on dbo.Orders about two minutes after the hourly reconciliation job began. order-api v4.2.0, released half an hour earlier, moved that job from nightly to hourly, and the job updates every open order in one transaction.${live} The leading hypothesis is that the reconciliation transaction holds locks on dbo.Orders long enough to block order inserts and updates.`,
      insufficient_evidence: false,
      hypotheses: [
        {
          title: "Hourly reconciliation job (v4.2.0) holds long locks on dbo.Orders and blocks order placement",
          explanation:
            `The failures are lock timeouts and command timeouts on dbo.Orders, starting just after the job's start time. The v4.2.0 change runs the job every hour instead of at night, during peak order traffic.${blocking ? " The blocking check shows sessions waiting behind a head blocker that is holding an open transaction." : ""}`,
          confidence: blocking ? "high" : "medium",
          evidence_ids: refs(dep("orders-420"), log("INC-1046", "lock-a"), log("INC-1046", "timeout"), errors, change, blocking, dbErrors),
          counter_evidence: [],
          unknowns: ["How long the reconciliation transaction stays open", "Whether the job batches its updates"],
          recommended_checks: [
            {
              step: "While the job runs, look at the blocking chain on the order database and the head blocker's last statement.",
              why: "Shows directly whether the reconciliation session is the one holding the locks.",
              expected_signal: "The head blocker is the reconciliation job's session with an open transaction.",
            },
            {
              step: "Compare order failures per minute with the job's start and end times across several hours.",
              why: "A repeating spike at the top of each hour points at the hourly job.",
              expected_signal: "Failures rise at :00 and clear when the job finishes.",
            },
          ],
        },
        {
          title: "General database resource pressure slowing every query",
          explanation: "Slow queries and timeouts can come from CPU, memory or IO pressure on the database server rather than locking.",
          confidence: "low",
          evidence_ids: refs(health, met("INC-1046", "DB request p95")),
          counter_evidence: ["The errors are lock timeouts (1222), which point at blocking rather than resource waits."],
          unknowns: ["Database CPU and IO during the incident window"],
          recommended_checks: [
            {
              step: "Check the top waits on the database server for the incident window.",
              why: "Lock waits (LCK_M_*) point at blocking; IO or CPU waits point at resource pressure.",
              expected_signal: "LCK_M_* waits dominate if blocking is the cause.",
            },
          ],
        },
      ],
      similar_incidents: [],
      limitations: [
        "Recorded mode: this analysis was replayed, not produced by a live model call.",
        ...(blocking ? [] : ["SQL Server was not checked live (the database system is off or showed no blocking), so blocking is inferred from the application's lock-timeout errors."]),
      ],
    };
  },
};

// ---------------------------------------------------------------------------
// Rule-based fallback for incidents without a recorded fixture
// ---------------------------------------------------------------------------

const humanize = (signature: string) => signature.replace(/_/g, " ");

function heuristicInvestigation(packet: EvidencePacket): InvestigationOutput {
  const { incident, items } = packet;
  const logs = items.filter((i) => i.type === "log");
  const bySignature = new Map<string, PacketItem[]>();
  for (const l of logs) {
    const sig = /\[signature=([^\]]+)\]/.exec(l.excerpt)?.[1];
    if (sig) bySignature.set(sig, [...(bySignature.get(sig) ?? []), l]);
  }
  const weight = (xs: PacketItem[]) => xs.reduce((n, x) => n + (x.occurrences ?? 1), 0);
  const signals = [...bySignature.entries()].sort((a, b) => weight(b[1]) - weight(a[1]));
  const start = Date.parse(incident.started_at);
  const recentDeploys = items.filter(
    (i) => i.type === "deployment" && i.excerpt.startsWith(`${incident.service}`) &&
      i.timestamp && start - Date.parse(i.timestamp) <= 2 * 3600_000 && start >= Date.parse(i.timestamp),
  );
  const bigMetrics = items.filter((i) => {
    const m = /baseline ([\d.]+) .* peak ([\d.]+)/.exec(i.excerpt);
    return i.type === "metric" && m && Number(m[2]) >= Math.max(3 * Number(m[1]), Number(m[1]) + 1);
  });
  const sameServiceHistory = items.filter((i) => i.type === "historical_incident" && i.excerpt.includes(`(${incident.service})`));

  const hypotheses: LlmHypothesis[] = [];

  if (signals.length > 0) {
    const [sig, sigLogs] = signals[0];
    const n = weight(sigLogs);
    hypotheses.push({
      title: `${humanize(sig)} is the primary failure mode`,
      explanation: `The most frequent error signature in the window is "${sig}" (${n} occurrence${n === 1 ? "" : "s"}). ${bigMetrics.length ? "Related metrics moved sharply over the same period." : "No supporting metric movement is in the packet."}`,
      confidence: n >= 3 && bigMetrics.length ? "medium" : "low",
      evidence_ids: [...sigLogs.slice(0, 3).map((l) => l.id), ...bigMetrics.map((m) => m.id)],
      counter_evidence: [],
      unknowns: ["What changed to trigger this failure mode"],
      recommended_checks: [
        {
          step: `Find when "${sig}" first appeared and whether it correlates with traffic, a dependency or a configuration change.`,
          why: "Establishes the trigger rather than the symptom.",
          expected_signal: "A clear onset time that lines up with a specific change.",
        },
      ],
    });
  }

  if (recentDeploys.length > 0 && signals.length > 0) {
    const d = recentDeploys[0];
    hypotheses.push({
      title: `Regression introduced by the recent ${incident.service} deployment`,
      explanation: `A deployment of ${incident.service} landed shortly before the incident started: ${d.excerpt}`,
      confidence: "medium",
      evidence_ids: [d.id, signals[0][1][0].id],
      counter_evidence: [],
      unknowns: ["Whether the deployed change touches the failing code path"],
      recommended_checks: [
        {
          step: "Diff the deployed change against the previous version for the failing component.",
          why: "Confirms or rules out the release as the trigger.",
          expected_signal: "A change in the component named by the error signature.",
        },
      ],
    });
  }

  if (sameServiceHistory.length > 0 && signals.length > 0) {
    const h = sameServiceHistory[0];
    hypotheses.push({
      title: `Recurrence of a past incident (${h.excerpt.split(" ")[0]})`,
      explanation: `A previous incident on ${incident.service} had matching symptoms. ${h.excerpt}`,
      confidence: "low",
      evidence_ids: [h.id, signals[0][1][0].id],
      counter_evidence: [],
      unknowns: ["Whether the previous fix is still in place"],
      recommended_checks: [
        {
          step: "Check whether the previous incident's trigger is present again.",
          why: "Past root causes often recur when the fix was partial.",
          expected_signal: "The same trigger condition visible in the current window.",
        },
      ],
    });
  }

  if (signals.length > 1) {
    const [sig, sigLogs] = signals[1];
    hypotheses.push({
      title: `Secondary failure: ${humanize(sig)}`,
      explanation: `A second error signature "${sig}" appears in the window; it may be a separate issue or a downstream effect.`,
      confidence: "low",
      evidence_ids: sigLogs.slice(0, 2).map((l) => l.id),
      counter_evidence: [],
      unknowns: ["Whether this error is related to the primary failure"],
      recommended_checks: [
        {
          step: `Check whether "${sig}" also occurs outside the incident window.`,
          why: "Distinguishes a pre-existing issue from part of this incident.",
          expected_signal: "Occurrences only within the incident window.",
        },
      ],
    });
  }

  const insufficient = hypotheses.length === 0;
  return {
    summary: insufficient
      ? `The packet for ${incident.key} contains no error signatures, recent ${incident.service} deployments or significant metric changes. There is insufficient evidence to propose a root cause.`
      : `Rule-based analysis of ${incident.key} (${incident.service}) found ${signals.length} error signature(s)${recentDeploys.length ? " and a recent deployment" : ""}. The hypotheses below are candidates for an engineer to verify.`,
    insufficient_evidence: insufficient,
    hypotheses,
    similar_incidents: [],
    limitations: [
      "Recorded/offline mode: this analysis was produced by deterministic rules, not by a live model call.",
      ...(insufficient ? ["Collect error logs, deployment history and metrics for the affected window, then run the investigation again."] : []),
    ],
  };
}

function heuristicRca(input: RcaInput): RcaOutput {
  const { packet } = input;
  const top = input.hypotheses.find((h) => !h.speculative);
  const start = Date.parse(packet.incident.started_at);
  const timelineStamps = new Set(packet.items.filter((i) => i.type === "timeline").map((i) => i.timestamp));
  // Same-service deployments within 2h before the start, unless the incident timeline already records them.
  const timelineItems = packet.items.filter((i) => i.type === "timeline" || (
    i.type === "deployment" && i.excerpt.startsWith(packet.incident.service) && i.timestamp && !timelineStamps.has(i.timestamp) &&
    start - Date.parse(i.timestamp) <= 2 * 3600_000 && start >= Date.parse(i.timestamp)
  ));
  const firstError = packet.items.find((i) => i.type === "log" && /^ERROR/.test(i.excerpt));

  const timeline = timelineItems
    .filter((i) => i.timestamp)
    .map((i) => ({ timestamp: i.timestamp!, event: i.type === "deployment" ? `Deployment: ${i.excerpt}` : i.excerpt, evidence_ids: [i.id] }));
  if (firstError?.timestamp) {
    timeline.push({ timestamp: firstError.timestamp, event: `First error logged: ${firstError.excerpt}`, evidence_ids: [firstError.id] });
  }
  timeline.sort((a, b) => a.timestamp.localeCompare(b.timestamp));

  const rootCause = input.verified_root_cause
    ? `Verified by engineer: ${input.verified_root_cause}`
    : top
      ? `Suspected (not yet confirmed): ${top.title}. ${top.explanation}`
      : "Root cause not determined; evidence was insufficient.";

  return {
    impact: `${packet.incident.impact_summary} Severity ${packet.incident.severity} in ${packet.incident.environment}.`,
    timeline,
    root_cause: rootCause,
    contributing_factors: input.hypotheses
      .filter((h) => h !== top && !h.speculative)
      .map((h) => `Considered: ${h.title} (${h.confidence} confidence)`)
      .concat(input.engineer_notes.map((n) => `Engineer note: ${n}`)),
    resolution_steps: [
      top ? `Validate the leading hypothesis: ${top.title}.` : "Gather additional evidence for the affected window.",
      "Record the verified cause and the action taken in this report before closing the incident.",
    ],
    prevention_actions: [
      ...input.similar.slice(0, 1).map((s) => `Revisit the prevention actions from ${s.key}: ${s.resolution}.`),
      "Add an alert on the leading error signature so recurrence is detected earlier.",
    ],
  };
}
