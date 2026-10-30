using System.Diagnostics;
using Dapper;
using ResolveAI.App.Data;
using ResolveAI.App.Llm;
using ResolveAI.App.Mcp;
using ResolveAI.App.Pipeline;
using ResolveAI.Contracts;
using ResolveAI.Mcp;

namespace ResolveAI.App.Services;

/// <summary>The MCP systems an investigation can check, and how many extra calls the model may make.</summary>
public sealed record Systems(IHub Hub, McpConfig Config, int MaxFollowUps);

public sealed class InvestigationRow
{
    public string Id { get; set; } = "";
    public string IncidentId { get; set; } = "";
    public string Status { get; set; } = "";
    public string? Stage { get; set; }
    public string? Question { get; set; }
    public string LlmMode { get; set; } = "recorded";
    public string? EvidencePacket { get; set; }
    public string? ModelName { get; set; }
    public string PromptVersion { get; set; } = "";
    public string? Summary { get; set; }
    public bool InsufficientEvidence { get; set; }
    public string Limitations { get; set; } = "[]";
    public string ValidationIssues { get; set; } = "[]";
    public string Trace { get; set; } = "[]";
    public string StartedAt { get; set; } = "";
    public string? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
}

public static class Investigations
{
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan FollowUpTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Creates a queued investigation and runs it in the background. Returns its ID immediately.</summary>
    public static string Start(Db db, ILlmProvider llm, Systems? systems, Incident incident, string? question)
    {
        var id = Guid.NewGuid().ToString();
        db.Run(
            """
            INSERT INTO investigations (id, incident_id, status, stage, question, llm_mode, prompt_version, started_at)
            VALUES (@id, @incidentId, 'queued', 'Queued', @question, @mode, @version, @now)
            """,
            new { id, incidentId = incident.Id, question, mode = llm.Mode, version = Prompts.Version, now = Js.IsoNow() });
        _ = Task.Run(async () =>
        {
            try
            {
                await Run(db, llm, systems, id, incident, question);
            }
            catch (Exception err)
            {
                Fail(db, id, err);
            }
        });
        return id;
    }

    private static void SetStage(Db db, string id, string status, string stage) =>
        db.Run("UPDATE investigations SET status = @status, stage = @stage WHERE id = @id", new { status, stage, id });

    private static void Fail(Db db, string id, Exception err)
    {
        var message = err is LlmError ? err.Message : "The investigation failed unexpectedly. The incident data is unchanged; try again.";
        if (err is not LlmError) Console.Error.WriteLine($"investigation {id} failed: {err}");
        db.Run("UPDATE investigations SET status = 'failed', stage = NULL, error_message = @message, completed_at = @now WHERE id = @id", new { message, now = Js.IsoNow(), id });
    }

    /// <summary>Stops waiting at the deadline, cancels the work, and reports the timeout in words the user can act on.</summary>
    public static async Task<T> WithTimeout<T>(Func<CancellationToken, Task<T>> work, TimeSpan timeout, string message)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            return await work(cts.Token).WaitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            throw new LlmError(message);
        }
    }

    /// <summary>Calls the model, retrying once when the failure is malformed output.</summary>
    public static async Task<LlmResult<T>> CallWithRetry<T>(Func<Task<LlmResult<T>>> fn, List<string> issues)
    {
        try
        {
            return await fn();
        }
        catch (LlmError err) when (err.Retryable)
        {
            issues.Add($"First model response was rejected ({err.Message}); retried once.");
            return await fn();
        }
    }

    /// <summary>Records how long each stage took and what it reported; persisted as the investigation's audit trail.</summary>
    private sealed class Trace
    {
        public List<TraceStage> Stages { get; } = [];

        public async Task<T> Step<T>(string stage, Func<Task<T>> fn, Func<T, Dictionary<string, object?>> details)
        {
            var started = DateTimeOffset.UtcNow;
            var timer = Stopwatch.StartNew();
            var result = await fn();
            Stages.Add(new TraceStage { Stage = stage, StartedAt = Js.Iso(started), DurationMs = timer.ElapsedMilliseconds, Details = details(result) });
            return result;
        }

        public Task<T> Step<T>(string stage, Func<T> fn, Func<T, Dictionary<string, object?>> details) => Step(stage, () => Task.FromResult(fn()), details);
    }

    /// <summary>Calls tools in order, numbering each result as evidence mcp:N and recording it.</summary>
    private sealed class ToolRecorder(Db db, string investigationId, IHub hub)
    {
        public List<ToolCallRecord> Calls { get; } = [];

        public async Task<ToolCallRecord> Call(string phase, string tool, Dictionary<string, object?> args, CancellationToken ct = default)
        {
            var spec = hub.Tools().FirstOrDefault(t => t.Name == tool);
            var seq = Calls.Count + 1;
            var started = DateTimeOffset.UtcNow;
            var timer = Stopwatch.StartNew();
            var res = await hub.Call(tool, args, ct);
            var rec = new ToolCallRecord
            {
                Ref = EvidenceIds.Id("mcp", seq.ToString()),
                Seq = seq,
                Phase = phase,
                System = spec?.System ?? "files",
                Server = spec?.Server ?? "unknown",
                Tool = tool,
                Args = args,
                Ok = res.Ok,
                Summary = res.Summary,
                Rows = res.Rows,
                Truncated = res.Truncated,
                DurationMs = timer.ElapsedMilliseconds,
                StartedAt = Js.Iso(started),
            };
            Calls.Add(rec);
            db.Run(
                """
                INSERT INTO tool_calls (id, investigation_id, seq, phase, system, server, tool, args, ok, summary, rows, truncated, duration_ms, started_at)
                VALUES (@id, @investigationId, @seq, @phase, @system, @server, @tool, @args, @ok, @summary, @rows, @truncated, @duration, @started)
                """,
                new
                {
                    id = Guid.NewGuid().ToString(), investigationId, seq, phase, system = rec.System, server = rec.Server, tool, args = Json.Serialize(args),
                    ok = res.Ok ? 1 : 0, summary = res.Summary, rows = Json.Serialize(res.Rows), truncated = res.Truncated ? 1 : 0, duration = rec.DurationMs, started = rec.StartedAt,
                });
            return rec;
        }
    }

    private static void Merge(Dictionary<string, object?> into, IEnumerable<KeyValuePair<string, long>>? usage)
    {
        foreach (var (k, v) in usage ?? []) into[k] = v;
    }

    private static async Task Run(Db db, ILlmProvider llm, Systems? systems, string id, Incident incident, string? question)
    {
        var trace = new Trace();
        void SaveTrace() => db.Run("UPDATE investigations SET trace = @trace WHERE id = @id", new { trace = Json.Serialize(trace.Stages), id });

        SetStage(db, id, "running", "Gathering logs, deployments and metrics");
        var evidence = await trace.Step("Gather evidence", () => EvidenceLoader.Load(db, incident), e => new()
        {
            ["logs"] = e.Logs.Count,
            ["error_signals"] = e.ErrorSignals.Count,
            ["deployments"] = e.Deployments.Count,
            ["metrics"] = e.Metrics.Count,
            ["timeline_events"] = e.Timeline.Count,
            ["runbooks"] = e.Runbooks.Count,
        });

        SetStage(db, id, "running", "Searching incident history");
        var similar = await trace.Step("Search incident history", () => Similar.Find(db, evidence), c => new()
        {
            ["candidates"] = c.Count,
            ["top_match"] = c.FirstOrDefault()?.Historical.Key ?? "none",
        });

        var tools = systems is null ? null : new ToolRecorder(db, id, systems.Hub);
        if (systems is not null && tools is not null)
        {
            SetStage(db, id, "running", "Checking systems: database, files, deploys and metrics");
            await trace.Step("Baseline checks (MCP)", async () =>
            {
                foreach (var p in Baseline.Plan(incident, systems.Config, systems.Hub.Tools())) await tools.Call("baseline", p.Tool, p.Args);
                return tools.Calls;
            }, calls =>
            {
                var details = new Dictionary<string, object?> { ["calls"] = calls.Count, ["failed"] = calls.Count(c => !c.Ok) };
                foreach (var g in calls.GroupBy(c => c.System)) details[g.Key] = g.Count();
                var off = systems.Hub.Status().Where(s => s.Status != "connected").Select(s => s.System).ToList();
                details["systems_off"] = off.Count > 0 ? string.Join(", ", off) : "none";
                return details;
            });
            SaveTrace();
        }

        var packet = await trace.Step("Build evidence packet", () => Packet.Build(evidence, similar, question, tools?.Calls), p => new()
        {
            ["items"] = p.Items.Count,
            ["characters"] = Json.Serialize(p).Length,
            ["redactions"] = p.Redactions,
            ["truncated"] = p.Truncated,
        });
        void SavePacket() => db.Run("UPDATE investigations SET evidence_packet = @packet WHERE id = @id", new { packet = Json.Serialize(packet), id });
        SavePacket();
        SaveTrace();

        var issues = new List<string>();
        if (systems is not null && tools is not null && llm.FollowUp is { } followUp && systems.MaxFollowUps > 0)
        {
            var max = systems.MaxFollowUps;
            var before = tools.Calls.Count;
            var refused = 0;
            // The limit is enforced here, not by the provider: past it, the model only gets a refusal back.
            CallTool call = async (name, args, ct) =>
            {
                if (tools.Calls.Count - before >= max)
                {
                    Interlocked.Increment(ref refused);
                    return $"Limit reached: no more tool calls are allowed in this investigation ({max}). Stop calling tools.";
                }
                var item = Packet.AddToolItem(packet, await tools.Call("follow_up", name, args, ct));
                return $"[{item.Id}] {item.Excerpt}";
            };
            SetStage(db, id, "running", llm.Mode == "live" ? "Claude is checking systems for missing evidence" : "Running follow-up checks (recorded)");
            try
            {
                var offered = systems.Hub.Tools().Select(t => new FollowUpTool(t.Name, t.Description, t.InputSchema)).ToList();
                await trace.Step(llm.Mode == "live" ? "Follow-up checks (Claude)" : "Follow-up checks (recorded)",
                    () => WithTimeout(ct => followUp(packet, offered, call, max, ct), FollowUpTimeout, "Follow-up checks took too long and were stopped."),
                    usage =>
                    {
                        var details = new Dictionary<string, object?> { ["calls"] = tools.Calls.Count - before, ["limit"] = max, ["refused"] = refused };
                        Merge(details, usage);
                        return details;
                    });
            }
            catch (Exception err)
            {
                // Follow-ups only add evidence; if they fail, the analysis still runs on what was gathered.
                issues.Add($"Follow-up checks stopped early: {err.Message}");
            }
            SavePacket();
            SaveTrace();
        }

        SetStage(db, id, "running", llm.Mode == "live" ? "Analysing evidence with Claude" : "Analysing evidence (recorded mode)");
        LlmResult<InvestigationOutput> result;
        try
        {
            result = await trace.Step(llm.Mode == "live" ? "Analyse with Claude" : "Analyse (recorded)",
                () => WithTimeout(ct => CallWithRetry(() => llm.Investigate(packet, ct), issues), OverallTimeout, "The analysis took too long and was stopped. Try again; the incident data is unchanged."),
                r =>
                {
                    var details = new Dictionary<string, object?> { ["mode"] = llm.Mode, ["model"] = r.Model ?? "recorded", ["retried"] = issues.Count > 0 };
                    Merge(details, r.Usage);
                    return details;
                });
        }
        finally
        {
            SaveTrace();
        }

        var v = await trace.Step("Validate output", () => Validate.Investigation(result.Output, packet), r => new()
        {
            ["hypotheses"] = r.Hypotheses.Count,
            ["speculative"] = r.Hypotheses.Count(h => h.Speculative),
            ["citations_removed"] = r.Issues.Count(i => i.StartsWith("Removed citation", StringComparison.Ordinal)),
            ["unsafe_checks_removed"] = r.Issues.Count(i => i.StartsWith("Removed a recommended check", StringComparison.Ordinal)),
            ["unverified_numbers"] = r.Issues.Count(i => i.StartsWith("Unverified number", StringComparison.Ordinal)),
            ["insufficient_evidence"] = r.InsufficientEvidence,
        });
        issues.AddRange(v.Issues);
        if (packet.Redactions > 0) issues.Add($"Redacted {packet.Redactions} secret or personal value(s) before analysis.");
        if (packet.Truncated) issues.Add("Evidence packet was trimmed to fit the size limit; lower-priority log lines were left out.");

        db.Tx((conn, tx) =>
        {
            foreach (var h in v.Hypotheses)
            {
                var hid = Guid.NewGuid().ToString();
                conn.Execute(
                    """
                    INSERT INTO hypotheses (id, investigation_id, rank, title, explanation, confidence, score, speculative, counter_evidence, unknowns, recommended_checks)
                    VALUES (@hid, @id, @rank, @title, @explanation, @confidence, @score, @speculative, @counter, @unknowns, @checks)
                    """,
                    new
                    {
                        hid, id, rank = h.Rank, title = h.Title, explanation = h.Explanation, confidence = h.Confidence, score = h.Score, speculative = h.Speculative ? 1 : 0,
                        counter = Json.Serialize(h.CounterEvidence), unknowns = Json.Serialize(h.Unknowns), checks = Json.Serialize(h.RecommendedChecks),
                    }, tx);
                for (var i = 0; i < h.EvidenceIds.Count; i++)
                {
                    conn.Execute("INSERT INTO evidence_links (hypothesis_id, evidence_ref, position) VALUES (@hid, @reference, @i)", new { hid, reference = h.EvidenceIds[i], i }, tx);
                }
            }
            for (var i = 0; i < similar.Count; i++)
            {
                var c = similar[i];
                var reason = v.SimilarReasons.GetValueOrDefault(c.Historical.Ref)
                    ?? $"Matched on: {string.Join(", ", c.MatchedTerms.Take(6))}{(c.Historical.ServiceName == incident.ServiceName ? " (same service)" : "")}.";
                conn.Execute(
                    """
                    INSERT INTO investigation_similar_incidents (investigation_id, historical_incident_id, rank, match_score, matched_terms, similarity_reason)
                    VALUES (@id, @historical, @rank, @score, @terms, @reason)
                    """,
                    new { id, historical = c.Historical.Id, rank = i + 1, score = c.MatchScore, terms = Json.Serialize(c.MatchedTerms), reason }, tx);
            }
            conn.Execute(
                """
                UPDATE investigations SET status = 'completed', stage = NULL, model_name = @model, summary = @summary, insufficient_evidence = @insufficient,
                       limitations = @limitations, validation_issues = @issues, usage = @usage, trace = @trace, completed_at = @now WHERE id = @id
                """,
                new
                {
                    model = result.Model, summary = v.Summary, insufficient = v.InsufficientEvidence ? 1 : 0, limitations = Json.Serialize(v.Limitations), issues = Json.Serialize(issues),
                    usage = result.Usage is null ? null : Json.Serialize(result.Usage), trace = Json.Serialize(trace.Stages), now = Js.IsoNow(), id,
                }, tx);
        });
    }

    // Read model

    public static InvestigationRow? GetRow(Db db, string id) => db.Get<InvestigationRow>("SELECT * FROM investigations WHERE id = @id", new { id });

    public static EvidencePacket? GetPacket(InvestigationRow row) => Json.Parse<EvidencePacket?>(row.EvidencePacket, null);

    private static readonly Dictionary<string, string> KindLabel = new()
    {
        ["log"] = "Log", ["dep"] = "Deployment", ["met"] = "Metric", ["doc"] = "Runbook", ["hist"] = "Past incident", ["tl"] = "Timeline", ["mcp"] = "System check",
    };

    public static List<ResolvedEvidence> ResolveEvidence(EvidencePacket? packet, IEnumerable<string> refs)
    {
        var byId = (packet?.Items ?? []).GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.First());
        return refs.Select(reference =>
        {
            var item = byId.GetValueOrDefault(reference);
            var colon = reference.IndexOf(':');
            var kind = colon >= 0 ? reference[..colon] : reference;
            // A system check is labelled with its system and tool, e.g. "database: db_blocking".
            var paren = item?.Source.IndexOf('(') ?? -1;
            var label = kind == "mcp" && item is not null
                ? (paren >= 0 ? item.Source[..paren] : item.Source)
                : $"{KindLabel.GetValueOrDefault(kind, kind)}{(item?.Occurrences is { } n and not 0 ? $" ×{n}" : "")}";
            return new ResolvedEvidence { Ref = reference, Kind = kind, Label = label, Excerpt = item?.Excerpt ?? "(record not in packet)", Timestamp = item?.Timestamp };
        }).ToList();
    }

    private sealed class HypothesisRow
    {
        public string Id { get; set; } = "";
        public int Rank { get; set; }
        public string Title { get; set; } = "";
        public string Explanation { get; set; } = "";
        public string Confidence { get; set; } = "low";
        public double Score { get; set; }
        public bool Speculative { get; set; }
        public string CounterEvidence { get; set; } = "[]";
        public string Unknowns { get; set; } = "[]";
        public string RecommendedChecks { get; set; } = "[]";
    }

    private sealed class SimilarRow
    {
        public string Id { get; set; } = "";
        public string Key { get; set; } = "";
        public string Title { get; set; } = "";
        public string ServiceName { get; set; } = "";
        public string Symptoms { get; set; } = "";
        public string RootCause { get; set; } = "";
        public string Resolution { get; set; } = "";
        public string ResolvedAt { get; set; } = "";
        public string? RcaDocumentId { get; set; }
        public double MatchScore { get; set; }
        public string MatchedTerms { get; set; } = "[]";
        public string SimilarityReason { get; set; } = "";
    }

    public static Investigation? Get(Db db, string id)
    {
        var row = GetRow(db, id);
        if (row is null) return null;
        var packet = GetPacket(row);

        var hypotheses = db.All<HypothesisRow>("SELECT * FROM hypotheses WHERE investigation_id = @id ORDER BY rank", new { id }).Select(h =>
        {
            var refs = db.All<string>("SELECT evidence_ref FROM evidence_links WHERE hypothesis_id = @id ORDER BY position", new { id = h.Id });
            var fb = db.All<long?>("SELECT helpful FROM feedback WHERE hypothesis_id = @id AND helpful IS NOT NULL ORDER BY created_at DESC, rowid DESC LIMIT 1", new { id = h.Id });
            return new Hypothesis
            {
                Id = h.Id,
                Rank = h.Rank,
                Title = h.Title,
                Explanation = h.Explanation,
                Confidence = h.Confidence,
                Score = h.Score,
                Speculative = h.Speculative,
                Evidence = ResolveEvidence(packet, refs),
                CounterEvidence = Json.Parse<List<string>>(h.CounterEvidence, []),
                Unknowns = Json.Parse<List<string>>(h.Unknowns, []),
                RecommendedChecks = Json.Parse<List<RecommendedCheck>>(h.RecommendedChecks, []),
                Feedback = fb.Count > 0 ? new HypothesisFeedback { Helpful = fb[0] != 0 } : null,
            };
        }).ToList();

        var similar = db.All<SimilarRow>(
            """
            SELECT h.id, h.key, h.title, h.service_name, h.symptoms, h.root_cause, h.resolution, h.resolved_at, h.rca_document_id,
                   s.match_score, s.matched_terms, s.similarity_reason
              FROM investigation_similar_incidents s JOIN historical_incidents h ON h.id = s.historical_incident_id
             WHERE s.investigation_id = @id ORDER BY s.rank
            """,
            new { id }).Select(s => new SimilarIncidentResult
        {
            Historical = new HistoricalIncident
            {
                Id = s.Id, Ref = EvidenceIds.Id("hist", s.Id), Key = s.Key, Title = s.Title, ServiceName = s.ServiceName, Symptoms = s.Symptoms,
                RootCause = s.RootCause, Resolution = s.Resolution, ResolvedAt = s.ResolvedAt, RcaDocumentId = s.RcaDocumentId,
            },
            MatchScore = s.MatchScore,
            MatchedTerms = Json.Parse<List<string>>(s.MatchedTerms, []),
            SimilarityReason = s.SimilarityReason,
        }).ToList();

        return new Investigation
        {
            Id = row.Id,
            IncidentId = row.IncidentId,
            Status = row.Status,
            Stage = row.Stage,
            Question = row.Question,
            LlmMode = row.LlmMode,
            ModelName = row.ModelName,
            PromptVersion = row.PromptVersion,
            StartedAt = row.StartedAt,
            CompletedAt = row.CompletedAt,
            ErrorMessage = row.ErrorMessage,
            Summary = row.Summary,
            InsufficientEvidence = row.InsufficientEvidence,
            Limitations = Json.Parse<List<string>>(row.Limitations, []),
            ValidationIssues = Json.Parse<List<string>>(row.ValidationIssues, []),
            Hypotheses = hypotheses,
            SimilarIncidents = similar,
            EvidenceCount = packet?.Items.Count ?? 0,
            Trace = Json.Parse<List<TraceStage>>(row.Trace, []),
            ToolCalls = ListToolCalls(db, id),
        };
    }

    private sealed class ToolCallRow
    {
        public int Seq { get; set; }
        public string Phase { get; set; } = "";
        public string System { get; set; } = "";
        public string Server { get; set; } = "";
        public string Tool { get; set; } = "";
        public string Args { get; set; } = "{}";
        public bool Ok { get; set; }
        public string Summary { get; set; } = "";
        public string Rows { get; set; } = "[]";
        public bool Truncated { get; set; }
        public long DurationMs { get; set; }
        public string StartedAt { get; set; } = "";
    }

    public static List<ToolCallRecord> ListToolCalls(Db db, string investigationId) =>
        db.All<ToolCallRow>("SELECT * FROM tool_calls WHERE investigation_id = @investigationId ORDER BY seq", new { investigationId }).Select(r => new ToolCallRecord
        {
            Ref = EvidenceIds.Id("mcp", r.Seq.ToString()),
            Seq = r.Seq,
            Phase = r.Phase,
            System = r.System,
            Server = r.Server,
            Tool = r.Tool,
            Args = Json.Parse<Dictionary<string, object?>>(r.Args, []),
            Ok = r.Ok,
            Summary = r.Summary,
            Rows = Json.Parse<List<Dictionary<string, object?>>>(r.Rows, []),
            Truncated = r.Truncated,
            DurationMs = r.DurationMs,
            StartedAt = r.StartedAt,
        }).ToList();

    public static string? LatestId(Db db, string incidentId) =>
        db.Get<string>("SELECT id FROM investigations WHERE incident_id = @incidentId ORDER BY started_at DESC, rowid DESC LIMIT 1", new { incidentId });

    /// <summary>Every run for an incident, newest first, so a re-run is visibly additive rather than a replacement.</summary>
    public static List<InvestigationRun> ListRuns(Db db, string incidentId) =>
        db.All<InvestigationRun>(
            """
            SELECT i.id, i.status, i.llm_mode, i.started_at,
                   (SELECT title FROM hypotheses h WHERE h.investigation_id = i.id ORDER BY rank LIMIT 1) AS top_hypothesis
              FROM investigations i WHERE i.incident_id = @incidentId ORDER BY i.started_at DESC, i.rowid DESC
            """,
            new { incidentId });

    /// <summary>Live (paid) model calls made since midnight UTC, across investigations and RCA drafts.</summary>
    public static int LiveRunsToday(Db db)
    {
        var since = $"{Js.IsoNow()[..10]}T00:00:00.000Z";
        return db.Get<int>("SELECT COUNT(*) FROM investigations WHERE llm_mode = 'live' AND started_at >= @since", new { since })
            + db.Get<int>("SELECT COUNT(*) FROM rca_drafts WHERE llm_mode = 'live' AND generated_at >= @since", new { since });
    }
}
