using System.Globalization;
using System.Text.RegularExpressions;
using ResolveAI.Contracts;

namespace ResolveAI.App.Llm;

/// <summary>
/// Offline provider for deterministic demos and tests. INC-1042 replays a recorded analysis; other incidents get a simple
/// rule-based analysis built only from packet items. Either way the output goes through the same validation as a live model response.
/// </summary>
public sealed class RecordedProvider(int delayMs = 1200) : ILlmProvider
{
    public string Mode => "recorded";
    public string? Model => null;

    public async Task<LlmResult<InvestigationOutput>> Investigate(EvidencePacket packet, CancellationToken ct)
    {
        await Task.Delay(delayMs, ct);
        var output = packet.Incident.Key switch
        {
            "INC-1042" => CiteToolResults(Inc1042(), packet),
            "INC-1045" => CiteToolResults(Inc1045(packet), packet),
            "INC-1046" => CiteToolResults(Inc1046(packet), packet),
            _ => HeuristicInvestigation(packet),
        };
        return new LlmResult<InvestigationOutput>(output, null);
    }

    /// <summary>Replays the follow-up checks a model would plausibly ask for, so the demo shows them without a model call.</summary>
    public FollowUp FollowUp => async (packet, tools, call, maxCalls, ct) =>
    {
        var offered = tools.Select(t => t.Name).ToHashSet();
        if (!FollowUps.TryGetValue(packet.Incident.Key, out var plan)) return null;
        foreach (var (tool, args) in plan.Where(c => offered.Contains(c.Tool)).Take(maxCalls)) await call(tool, new Dictionary<string, object?>(args), ct);
        return null;
    };

    public async Task<LlmResult<RcaOutput>> DraftRca(RcaInput input, CancellationToken ct)
    {
        await Task.Delay(delayMs / 2, ct);
        return new LlmResult<RcaOutput>(HeuristicRca(input), null);
    }

    private static string Log(string inc, string name) => $"log:{EvidenceIds.Stable($"log:{inc}:{name}")}";
    private static string Met(string inc, string name) => $"met:{EvidenceIds.Stable($"met:{inc}:{name}")}";
    private static string Dep(string name) => $"dep:{EvidenceIds.Stable($"dep:{name}")}";
    private static string Hist(string name) => $"hist:{EvidenceIds.Stable($"hist:{name}")}";

    /// <summary>The packet ID of a system-check result, found by tool name (and optionally text in its result).</summary>
    private static string? ToolRef(EvidencePacket packet, string tool, string? containsPattern = null) =>
        packet.Items.FirstOrDefault(i => i.Type == "tool_result" && i.Source.Contains($": {tool}(", StringComparison.Ordinal)
            && (containsPattern is null || Regex.IsMatch(i.Excerpt, containsPattern, RegexOptions.ECMAScript)))?.Id;

    private static List<string> Refs(params string?[] xs) => xs.Where(x => !string.IsNullOrEmpty(x)).Cast<string>().ToList();

    private static RecommendedCheck Check(string step, string why, string expected) => new() { Step = step, Why = why, ExpectedSignal = expected };

    /// <summary>Follow-up checks replayed in recorded mode, as if the model had chosen them.</summary>
    private static readonly Dictionary<string, (string Tool, Dictionary<string, object?> Args)[]> FollowUps = new()
    {
        ["INC-1042"] =
        [
            ("deploys_change_summary", new() { ["service"] = "payment-api", ["commit"] = "7d03b1f" }),
            ("files_tail", new() { ["path"] = "payment-api/db-client.yaml", ["lines"] = 10L }),
        ],
        ["INC-1045"] =
        [
            ("files_tail", new() { ["path"] = "document-service/mounts.txt", ["lines"] = 10L }),
            ("files_tail", new() { ["path"] = "document-service/storage.yaml", ["lines"] = 10L }),
            ("deploys_change_summary", new() { ["service"] = "document-service", ["commit"] = "b62f0d9" }),
        ],
        ["INC-1046"] =
        [
            ("deploys_change_summary", new() { ["service"] = "order-api", ["commit"] = "c90e4a7" }),
            ("db_long_queries", new() { ["min_seconds"] = 5L }),
        ],
    };

    /// <summary>System checks that support a recorded hypothesis, added to its citations when they were made.</summary>
    private static readonly Dictionary<string, (int Hypothesis, string Tool, string? Contains)[]> ToolCites = new()
    {
        ["INC-1042"] =
        [
            (0, "deploys_change_summary", null),
            (0, "files_tail", "max: 20"),
            (0, "metrics_series", "DB pool connections in use"),
        ],
    };

    private static InvestigationOutput CiteToolResults(InvestigationOutput output, EvidencePacket packet)
    {
        if (!ToolCites.TryGetValue(packet.Incident.Key, out var cites)) return output;
        foreach (var c in cites)
        {
            var reference = ToolRef(packet, c.Tool, c.Contains);
            if (reference is not null && c.Hypothesis < output.Hypotheses.Count && !output.Hypotheses[c.Hypothesis].EvidenceIds.Contains(reference))
            {
                output.Hypotheses[c.Hypothesis].EvidenceIds.Add(reference);
            }
        }
        return output;
    }

    private static InvestigationOutput Inc1042() => new()
    {
        Summary = "HTTP 500s on payment-api began about four minutes after v2.8.1 rolled out. The release moved database client settings into db-client.yaml, and the service logged pool.max=20 at startup where the 09:58 health check showed max=100. Requests then queued for connections and timed out while payments-db itself stayed healthy. This matches INC-0881, a pool-size regression after a config change. The leading hypothesis is not confirmed until an engineer compares the effective pool configuration with v2.8.0.",
        InsufficientEvidence = false,
        Hypotheses =
        [
            new()
            {
                Title = "v2.8.1 config change reduced the DB connection pool (100 → 20), exhausting it under normal load",
                Explanation = "The v2.8.1 deployment changed database client configuration. After rollout the service loaded pool.max=20, whereas the pre-deploy health check showed max=100. Within four minutes the pool was full (in_use=20, 14 waiting) and connection acquisition began timing out, which surfaced as HTTP 500s. Pool connections in use rose to exactly the new limit, while the database reported ample capacity.",
                Confidence = "high",
                EvidenceIds =
                [
                    Dep("pay-281"), Log("INC-1042", "health"), Log("INC-1042", "config"), Log("INC-1042", "saturated"),
                    Log("INC-1042", "timeout-a"), Met("INC-1042", "DB pool connections in use"), Hist("hist-0881"),
                ],
                CounterEvidence = ["Errors started ~4 minutes after rollout began; a traffic ramp would explain the lag but traffic volume is not in the packet."],
                Unknowns = ["Whether pool.max=20 was intentional in the v2.8.1 change", "Request volume compared with the same time on previous days"],
                RecommendedChecks =
                [
                    Check("Compare the effective pool.max and acquireTimeoutMs between v2.8.0 and v2.8.1 (db-client.yaml and the previous config source).",
                        "Confirms whether the release changed the pool size rather than load increasing.",
                        "v2.8.0 resolved pool.max to 100 (or similar); v2.8.1 resolves to 20."),
                    Check("Check the pool utilisation dashboard for every payment-api pod since 10:00.",
                        "Pool exhaustion should be visible on all pods running v2.8.1.",
                        "in_use pinned at max with a growing wait queue on all pods."),
                    Check("Confirm payments-db has connection and CPU headroom for the full window.",
                        "Rules out the database itself as the bottleneck.",
                        "Active connections far below max_connections; CPU flat."),
                ],
            },
            new()
            {
                Title = "payments-db unavailable or overloaded",
                Explanation = "Connection acquisition timeouts can be caused by a database that is slow to accept connections. The packet contains timeouts but no database-side errors.",
                Confidence = "low",
                EvidenceIds = [Log("INC-1042", "timeout-a")],
                CounterEvidence =
                [
                    "payments-db reported active_connections=20 of max_connections=500, CPU 22% and no replication lag at 10:07.",
                    "Database CPU stayed flat (21% → 23%) through the incident.",
                ],
                Unknowns = ["Database slow-query log for the window"],
                RecommendedChecks =
                [
                    Check("Review payments-db slow-query and connection logs from 10:00 to 10:15.",
                        "A database-side cause would show refused or slow connection handshakes.",
                        "No refused connections or slow handshakes, which would rule this out."),
                ],
            },
            new()
            {
                Title = "Load increase from the checkout-web v5.2.0 release",
                Explanation = "checkout-web deployed v5.2.0 at 09:30. If the new address autocomplete widget issued extra payment calls, increased load could exhaust the pool.",
                Confidence = "low",
                EvidenceIds = [Dep("checkout-520")],
                CounterEvidence = ["The release was 34 minutes before errors began and its change summary concerns address autocomplete, not payments."],
                Unknowns = ["Payment request rate from checkout-web before and after 09:30"],
                RecommendedChecks =
                [
                    Check("Compare payment-api request rate from checkout-web before and after 09:30.",
                        "Distinguishes a load increase from a capacity reduction.",
                        "Flat request rate, which would rule this out."),
                ],
            },
        ],
        SimilarIncidents =
        [
            new()
            {
                IncidentId = Hist("hist-0881"),
                SimilarityReason = "Same service and the same pattern: HTTP 500s and database connection timeouts right after a configuration change, with a healthy database. INC-0881's root cause was an incorrect max connection setting.",
            },
        ],
        Limitations =
        [
            "Request volume and per-pod pool metrics are not in the packet.",
            "The v2.8.0 configuration file itself is not available; the previous pool size comes from a health-check log line.",
            "The payment-api database connectivity runbook recommends comparing configuration with the last healthy deployment before any rollback decision.",
        ],
    };

    private static InvestigationOutput Inc1045(EvidencePacket packet)
    {
        var errors = ToolRef(packet, "files_search", "EACCES");
        var mounts = ToolRef(packet, "files_tail", @"invoices-archive\s+ext4\s+ro");
        var storage = ToolRef(packet, "files_tail", @"path: \/mnt\/invoices-archive");
        var change = ToolRef(packet, "deploys_change_summary", "invoices-archive");
        return new InvestigationOutput
        {
            Summary = $"Invoice uploads began failing about five minutes after document-service v1.4.0 moved invoice storage to /mnt/invoices-archive. Every failure is EACCES (permission denied) on a path under that folder.{(mounts is not null ? " The node's mount table shows /mnt/invoices-archive mounted read-only (ro), while the previous path /data/invoices is read-write." : "")} The leading hypothesis is that the release pointed writes at a read-only archive volume; an engineer should confirm the mount on the running pods.",
            InsufficientEvidence = false,
            Hypotheses =
            [
                new()
                {
                    Title = "v1.4.0 points invoice storage at a volume that is mounted read-only",
                    Explanation = $"The v1.4.0 change replaced the storage path /data/invoices with /mnt/invoices-archive. Right after rollout the service logged that the new path is mounted read-only, and every upload since has failed with EACCES on that path.{(mounts is not null ? " The captured mount table lists /mnt/invoices-archive with the ro option." : "")}",
                    Confidence = mounts is not null || storage is not null ? "high" : "medium",
                    EvidenceIds = Refs(Dep("docs-140"), Log("INC-1045", "eacces-a"), errors, mounts, storage, change),
                    CounterEvidence = [],
                    Unknowns = ["Whether the archive volume is meant to be writable by document-service", "Whether every pod has the same mount options"],
                    RecommendedChecks =
                    [
                        Check("On one document-service pod, look at the mount options for /mnt/invoices-archive and the owner of the folder.",
                            "A read-only mount explains EACCES on every write regardless of file permissions.",
                            "The mount shows ro, or the folder belongs to a user the service does not run as."),
                        Check("Compare storage.yaml in v1.3.x and v1.4.0.",
                            "Confirms the path change came from the release, not from an environment override.",
                            "Only v1.4.0 points at /mnt/invoices-archive."),
                    ],
                },
                new()
                {
                    Title = "File ownership or permissions changed on the archive folder",
                    Explanation = "EACCES can also mean the service user lacks write permission on a writable volume, for example after a folder was recreated by another user.",
                    Confidence = "low",
                    EvidenceIds = Refs(Log("INC-1045", "eacces-a")),
                    CounterEvidence = mounts is not null ? ["The mount table shows the volume itself is read-only, which already explains the failures."] : [],
                    Unknowns = ["Folder owner and mode on the archive volume"],
                    RecommendedChecks =
                    [
                        Check("Check the owner and mode of /mnt/invoices-archive/2026/10/27 on a pod.",
                            "Separates a permissions problem from a read-only mount.",
                            "A different owner or a mode without write for the service user."),
                    ],
                },
            ],
            SimilarIncidents = [],
            Limitations =
            [
                "Recorded mode: this analysis was replayed, not produced by a live model call.",
                .. mounts is not null ? Array.Empty<string>() : ["The mount table was not checked, so a read-only mount is inferred from the startup warning only."],
            ],
        };
    }

    private static InvestigationOutput Inc1046(EvidencePacket packet)
    {
        var errors = ToolRef(packet, "files_search", "1222");
        var change = ToolRef(packet, "deploys_change_summary", "reconcileOrders");
        var blocking = ToolRef(packet, "db_blocking", @"blocked request\(s\) behind");
        var dbErrors = ToolRef(packet, "db_errors", "1222");
        var health = ToolRef(packet, "db_health");
        var live = blocking is not null ? " ResolveAI's live check of SQL Server also found a blocking chain on the order database right now." : "";
        return new InvestigationOutput
        {
            Summary = $"Order placement started failing with SQL Server lock timeouts (error 1222) on dbo.Orders about two minutes after the hourly reconciliation job began. order-api v4.2.0, released half an hour earlier, moved that job from nightly to hourly, and the job updates every open order in one transaction.{live} The leading hypothesis is that the reconciliation transaction holds locks on dbo.Orders long enough to block order inserts and updates.",
            InsufficientEvidence = false,
            Hypotheses =
            [
                new()
                {
                    Title = "Hourly reconciliation job (v4.2.0) holds long locks on dbo.Orders and blocks order placement",
                    Explanation = $"The failures are lock timeouts and command timeouts on dbo.Orders, starting just after the job's start time. The v4.2.0 change runs the job every hour instead of at night, during peak order traffic.{(blocking is not null ? " The blocking check shows sessions waiting behind a head blocker that is holding an open transaction." : "")}",
                    Confidence = blocking is not null ? "high" : "medium",
                    EvidenceIds = Refs(Dep("orders-420"), Log("INC-1046", "lock-a"), Log("INC-1046", "timeout"), errors, change, blocking, dbErrors),
                    CounterEvidence = [],
                    Unknowns = ["How long the reconciliation transaction stays open", "Whether the job batches its updates"],
                    RecommendedChecks =
                    [
                        Check("While the job runs, look at the blocking chain on the order database and the head blocker's last statement.",
                            "Shows directly whether the reconciliation session is the one holding the locks.",
                            "The head blocker is the reconciliation job's session with an open transaction."),
                        Check("Compare order failures per minute with the job's start and end times across several hours.",
                            "A repeating spike at the top of each hour points at the hourly job.",
                            "Failures rise at :00 and clear when the job finishes."),
                    ],
                },
                new()
                {
                    Title = "General database resource pressure slowing every query",
                    Explanation = "Slow queries and timeouts can come from CPU, memory or IO pressure on the database server rather than locking.",
                    Confidence = "low",
                    EvidenceIds = Refs(health, Met("INC-1046", "DB request p95")),
                    CounterEvidence = ["The errors are lock timeouts (1222), which point at blocking rather than resource waits."],
                    Unknowns = ["Database CPU and IO during the incident window"],
                    RecommendedChecks =
                    [
                        Check("Check the top waits on the database server for the incident window.",
                            "Lock waits (LCK_M_*) point at blocking; IO or CPU waits point at resource pressure.",
                            "LCK_M_* waits dominate if blocking is the cause."),
                    ],
                },
            ],
            SimilarIncidents = [],
            Limitations =
            [
                "Recorded mode: this analysis was replayed, not produced by a live model call.",
                .. blocking is not null ? Array.Empty<string>() : ["SQL Server was not checked live (the database system is off or showed no blocking), so blocking is inferred from the application's lock-timeout errors."],
            ],
        };
    }

    // Rule-based fallback for incidents without a recorded fixture

    private static string Humanize(string signature) => signature.Replace('_', ' ');

    private static readonly Regex Signature = new(@"\[signature=([^\]]+)\]", RegexOptions.ECMAScript);
    private static readonly Regex MetricChange = new(@"baseline ([\d.]+) .* peak ([\d.]+)", RegexOptions.ECMAScript);

    private static double Number(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : double.NaN;

    /// <summary>Whether a deployment landed in the two hours before the incident started.</summary>
    private static bool JustBefore(string? timestamp, DateTimeOffset start) =>
        !string.IsNullOrEmpty(timestamp) && Js.TryParseDate(timestamp, out var t) && start - t <= TimeSpan.FromHours(2) && start >= t;

    private static InvestigationOutput HeuristicInvestigation(EvidencePacket packet)
    {
        var (incident, items) = (packet.Incident, packet.Items);
        var bySignature = new List<(string Sig, List<PacketItem> Logs)>();
        foreach (var l in items.Where(i => i.Type == "log"))
        {
            var m = Signature.Match(l.Excerpt);
            if (!m.Success) continue;
            var sig = m.Groups[1].Value;
            var group = bySignature.FindIndex(x => x.Sig == sig);
            if (group >= 0) bySignature[group].Logs.Add(l);
            else bySignature.Add((sig, [l]));
        }
        static int Weight(List<PacketItem> xs) => xs.Sum(x => x.Occurrences ?? 1);
        var signals = bySignature.OrderByDescending(x => Weight(x.Logs)).ToList();
        var start = Js.ParseDate(incident.StartedAt);
        var recentDeploys = items.Where(i => i.Type == "deployment" && i.Excerpt.StartsWith(incident.Service, StringComparison.Ordinal) && JustBefore(i.Timestamp, start)).ToList();
        var bigMetrics = items.Where(i =>
        {
            var m = MetricChange.Match(i.Excerpt);
            if (i.Type != "metric" || !m.Success) return false;
            var (baseline, peak) = (Number(m.Groups[1].Value), Number(m.Groups[2].Value));
            return peak >= Math.Max(3 * baseline, baseline + 1);
        }).ToList();
        var sameServiceHistory = items.Where(i => i.Type == "historical_incident" && i.Excerpt.Contains($"({incident.Service})", StringComparison.Ordinal)).ToList();

        var hypotheses = new List<LlmHypothesis>();

        if (signals.Count > 0)
        {
            var (sig, sigLogs) = signals[0];
            var n = Weight(sigLogs);
            hypotheses.Add(new LlmHypothesis
            {
                Title = $"{Humanize(sig)} is the primary failure mode",
                Explanation = $"The most frequent error signature in the window is \"{sig}\" ({n} occurrence{(n == 1 ? "" : "s")}). {(bigMetrics.Count > 0 ? "Related metrics moved sharply over the same period." : "No supporting metric movement is in the packet.")}",
                Confidence = n >= 3 && bigMetrics.Count > 0 ? "medium" : "low",
                EvidenceIds = sigLogs.Take(3).Select(l => l.Id).Concat(bigMetrics.Select(m => m.Id)).ToList(),
                Unknowns = ["What changed to trigger this failure mode"],
                RecommendedChecks =
                [
                    Check($"Find when \"{sig}\" first appeared and whether it correlates with traffic, a dependency or a configuration change.",
                        "Establishes the trigger rather than the symptom.",
                        "A clear onset time that lines up with a specific change."),
                ],
            });
        }

        if (recentDeploys.Count > 0 && signals.Count > 0)
        {
            var d = recentDeploys[0];
            hypotheses.Add(new LlmHypothesis
            {
                Title = $"Regression introduced by the recent {incident.Service} deployment",
                Explanation = $"A deployment of {incident.Service} landed shortly before the incident started: {d.Excerpt}",
                Confidence = "medium",
                EvidenceIds = [d.Id, signals[0].Logs[0].Id],
                Unknowns = ["Whether the deployed change touches the failing code path"],
                RecommendedChecks =
                [
                    Check("Diff the deployed change against the previous version for the failing component.",
                        "Confirms or rules out the release as the trigger.",
                        "A change in the component named by the error signature."),
                ],
            });
        }

        if (sameServiceHistory.Count > 0 && signals.Count > 0)
        {
            var h = sameServiceHistory[0];
            hypotheses.Add(new LlmHypothesis
            {
                Title = $"Recurrence of a past incident ({h.Excerpt.Split(' ')[0]})",
                Explanation = $"A previous incident on {incident.Service} had matching symptoms. {h.Excerpt}",
                Confidence = "low",
                EvidenceIds = [h.Id, signals[0].Logs[0].Id],
                Unknowns = ["Whether the previous fix is still in place"],
                RecommendedChecks =
                [
                    Check("Check whether the previous incident's trigger is present again.",
                        "Past root causes often recur when the fix was partial.",
                        "The same trigger condition visible in the current window."),
                ],
            });
        }

        if (signals.Count > 1)
        {
            var (sig, sigLogs) = signals[1];
            hypotheses.Add(new LlmHypothesis
            {
                Title = $"Secondary failure: {Humanize(sig)}",
                Explanation = $"A second error signature \"{sig}\" appears in the window; it may be a separate issue or a downstream effect.",
                Confidence = "low",
                EvidenceIds = sigLogs.Take(2).Select(l => l.Id).ToList(),
                Unknowns = ["Whether this error is related to the primary failure"],
                RecommendedChecks =
                [
                    Check($"Check whether \"{sig}\" also occurs outside the incident window.",
                        "Distinguishes a pre-existing issue from part of this incident.",
                        "Occurrences only within the incident window."),
                ],
            });
        }

        var insufficient = hypotheses.Count == 0;
        return new InvestigationOutput
        {
            Summary = insufficient
                ? $"The packet for {incident.Key} contains no error signatures, recent {incident.Service} deployments or significant metric changes. There is insufficient evidence to propose a root cause."
                : $"Rule-based analysis of {incident.Key} ({incident.Service}) found {signals.Count} error signature(s){(recentDeploys.Count > 0 ? " and a recent deployment" : "")}. The hypotheses below are candidates for an engineer to verify.",
            InsufficientEvidence = insufficient,
            Hypotheses = hypotheses,
            SimilarIncidents = [],
            Limitations =
            [
                "Recorded/offline mode: this analysis was produced by deterministic rules, not by a live model call.",
                .. insufficient ? ["Collect error logs, deployment history and metrics for the affected window, then run the investigation again."] : Array.Empty<string>(),
            ],
        };
    }

    private static RcaOutput HeuristicRca(RcaInput input)
    {
        var packet = input.Packet;
        var top = input.Hypotheses.FirstOrDefault(h => !h.Speculative);
        var start = Js.ParseDate(packet.Incident.StartedAt);
        var timelineStamps = packet.Items.Where(i => i.Type == "timeline").Select(i => i.Timestamp).ToHashSet();
        // Same-service deployments within 2h before the start, unless the incident timeline already records them.
        var timelineItems = packet.Items.Where(i => i.Type == "timeline" || (
            i.Type == "deployment" && i.Excerpt.StartsWith(packet.Incident.Service, StringComparison.Ordinal) && !timelineStamps.Contains(i.Timestamp) && JustBefore(i.Timestamp, start)));
        var firstError = packet.Items.FirstOrDefault(i => i.Type == "log" && i.Excerpt.StartsWith("ERROR", StringComparison.Ordinal));

        var timeline = timelineItems
            .Where(i => !string.IsNullOrEmpty(i.Timestamp))
            .Select(i => new RcaTimelineEntry { Timestamp = i.Timestamp!, Event = i.Type == "deployment" ? $"Deployment: {i.Excerpt}" : i.Excerpt, EvidenceIds = [i.Id] })
            .ToList();
        if (!string.IsNullOrEmpty(firstError?.Timestamp))
        {
            timeline.Add(new RcaTimelineEntry { Timestamp = firstError.Timestamp, Event = $"First error logged: {firstError.Excerpt}", EvidenceIds = [firstError.Id] });
        }
        timeline = timeline.OrderBy(e => e.Timestamp, StringComparer.Ordinal).ToList();

        var rootCause = !string.IsNullOrEmpty(input.VerifiedRootCause)
            ? $"Verified by engineer: {input.VerifiedRootCause}"
            : top is not null
                ? $"Suspected (not yet confirmed): {top.Title}. {top.Explanation}"
                : "Root cause not determined; evidence was insufficient.";

        return new RcaOutput
        {
            Impact = $"{packet.Incident.ImpactSummary} Severity {packet.Incident.Severity} in {packet.Incident.Environment}.",
            Timeline = timeline,
            RootCause = rootCause,
            ContributingFactors = input.Hypotheses
                .Where(h => !ReferenceEquals(h, top) && !h.Speculative)
                .Select(h => $"Considered: {h.Title} ({h.Confidence} confidence)")
                .Concat(input.EngineerNotes.Select(n => $"Engineer note: {n}"))
                .ToList(),
            ResolutionSteps =
            [
                top is not null ? $"Validate the leading hypothesis: {top.Title}." : "Gather additional evidence for the affected window.",
                "Record the verified cause and the action taken in this report before closing the incident.",
            ],
            PreventionActions =
            [
                .. input.Similar.Take(1).Select(s => $"Revisit the prevention actions from {s.Key}: {s.Resolution}."),
                "Add an alert on the leading error signature so recurrence is detected earlier.",
            ],
        };
    }
}
