using ResolveAI.App.Data;
using ResolveAI.App.Llm;
using ResolveAI.App.Pipeline;
using ResolveAI.Contracts;

namespace ResolveAI.App.Services;

public static class Rca
{
    private sealed class Row
    {
        public string Id { get; set; } = "";
        public string IncidentId { get; set; } = "";
        public string InvestigationId { get; set; } = "";
        public int Version { get; set; }
        public string Status { get; set; } = "draft";
        public string Impact { get; set; } = "";
        public string Timeline { get; set; } = "[]";
        public string RootCause { get; set; } = "";
        public string ContributingFactors { get; set; } = "[]";
        public string ResolutionSteps { get; set; } = "[]";
        public string PreventionActions { get; set; } = "[]";
        public string EvidenceIds { get; set; } = "[]";
        public string ValidationIssues { get; set; } = "[]";
        public string LlmMode { get; set; } = "recorded";
        public string GeneratedAt { get; set; } = "";
        public string UpdatedAt { get; set; } = "";
    }

    private static RcaDraft ToDto(Row r) => new()
    {
        Id = r.Id,
        IncidentId = r.IncidentId,
        InvestigationId = r.InvestigationId,
        Version = r.Version,
        Status = r.Status,
        Impact = r.Impact,
        Timeline = Json.Parse<List<RcaTimelineEntry>>(r.Timeline, []),
        RootCause = r.RootCause,
        ContributingFactors = Json.Parse<List<string>>(r.ContributingFactors, []),
        ResolutionSteps = Json.Parse<List<string>>(r.ResolutionSteps, []),
        PreventionActions = Json.Parse<List<string>>(r.PreventionActions, []),
        EvidenceIds = Json.Parse<List<string>>(r.EvidenceIds, []),
        ValidationIssues = Json.Parse<List<string>>(r.ValidationIssues, []),
        LlmMode = r.LlmMode,
        GeneratedAt = r.GeneratedAt,
        UpdatedAt = r.UpdatedAt,
    };

    public static RcaDraft? Get(Db db, string id)
    {
        var row = db.Get<Row>("SELECT * FROM rca_drafts WHERE id = @id", new { id });
        return row is null ? null : ToDto(row);
    }

    public static List<RcaDraft> List(Db db, string incidentId) =>
        db.All<Row>("SELECT * FROM rca_drafts WHERE incident_id = @incidentId ORDER BY version DESC", new { incidentId }).Select(ToDto).ToList();

    private sealed class FeedbackRow
    {
        public string? Comment { get; set; }
        public string? VerifiedRootCause { get; set; }
    }

    /// <summary>
    /// Generates a new RCA version from a completed investigation. Earlier versions (and any edits to
    /// them) are kept; regenerating never overwrites a human's changes.
    /// </summary>
    public static async Task<RcaDraft> Create(Db db, ILlmProvider llm, Incident incident, string investigationId)
    {
        var row = Investigations.GetRow(db, investigationId);
        if (row is null || row.IncidentId != incident.Id) throw new LlmError("That investigation does not belong to this incident.");
        if (row.Status != "completed") throw new LlmError("Run an investigation to completion before drafting an RCA.");
        var inv = Investigations.Get(db, investigationId)!;
        var packet = Investigations.GetPacket(row) ?? throw new LlmError("The investigation has no stored evidence packet.");

        var feedback = db.All<FeedbackRow>("SELECT comment, verified_root_cause FROM feedback WHERE investigation_id = @investigationId ORDER BY created_at, rowid", new { investigationId });
        var verified = feedback.Select(f => f.VerifiedRootCause).LastOrDefault(v => !string.IsNullOrEmpty(v));

        var issues = new List<string>();
        var input = new RcaInput
        {
            Packet = packet,
            Summary = inv.Summary,
            Hypotheses = inv.Hypotheses.Select(h => new RcaHypothesisInput
            {
                Rank = h.Rank,
                Title = h.Title,
                Explanation = h.Explanation,
                Confidence = h.Confidence,
                Speculative = h.Speculative,
                EvidenceIds = h.Evidence.Select(e => e.Ref).ToList(),
                Helpful = h.Feedback?.Helpful,
            }).ToList(),
            VerifiedRootCause = verified,
            EngineerNotes = feedback.Select(f => f.Comment).Where(c => !string.IsNullOrEmpty(c)).Cast<string>().ToList(),
            Similar = inv.SimilarIncidents.Select(s => new RcaSimilarInput { Id = s.Historical.Ref, Key = s.Historical.Key, RootCause = s.Historical.RootCause, Resolution = s.Historical.Resolution }).ToList(),
        };
        var result = await Investigations.WithTimeout(
            ct => Investigations.CallWithRetry(() => llm.DraftRca(input, ct), issues),
            TimeSpan.FromSeconds(180),
            "Drafting the RCA took too long and was stopped. Try again.");

        var sourceText = string.Join("\n", new[] { Validate.PacketText(packet), verified ?? "" }.Concat(feedback.Select(f => f.Comment ?? "")));
        var (rca, evidenceIds, rcaIssues) = Validate.Rca(result.Output, packet.Items.Select(i => i.Id).ToHashSet(), sourceText);
        issues.AddRange(rcaIssues);
        var version = (db.Get<int?>("SELECT MAX(version) FROM rca_drafts WHERE incident_id = @id", new { id = incident.Id }) ?? 0) + 1;
        var id = Guid.NewGuid().ToString();
        var ts = Js.IsoNow();
        db.Run(
            """
            INSERT INTO rca_drafts (id, incident_id, investigation_id, version, status, impact, timeline, root_cause,
                                    contributing_factors, resolution_steps, prevention_actions, evidence_ids, validation_issues, llm_mode,
                                    generated_at, updated_at)
            VALUES (@id, @incidentId, @investigationId, @version, 'draft', @impact, @timeline, @rootCause, @factors, @steps, @actions, @evidence, @issues, @mode, @ts, @ts)
            """,
            new
            {
                id, incidentId = incident.Id, investigationId, version, impact = rca.Impact, timeline = Json.Serialize(rca.Timeline), rootCause = rca.RootCause,
                factors = Json.Serialize(rca.ContributingFactors), steps = Json.Serialize(rca.ResolutionSteps), actions = Json.Serialize(rca.PreventionActions),
                evidence = Json.Serialize(evidenceIds), issues = Json.Serialize(issues), mode = llm.Mode, ts,
            });
        return Get(db, id)!;
    }

    public static RcaDraft? Patch(Db db, string id, PatchRcaBody patch)
    {
        if (Get(db, id) is null) return null;
        var sets = new List<string>();
        var args = new Dapper.DynamicParameters();
        args.Add("id", id);
        void Set(string column, object? value)
        {
            if (value is null) return;
            sets.Add($"{column} = @{column}");
            args.Add(column, value is string s ? s : Json.Serialize(value));
        }
        // Column names come only from this fixed list, never from the request.
        Set("impact", patch.Impact);
        Set("root_cause", patch.RootCause);
        Set("contributing_factors", patch.ContributingFactors);
        Set("resolution_steps", patch.ResolutionSteps);
        Set("prevention_actions", patch.PreventionActions);
        Set("timeline", patch.Timeline);
        Set("status", patch.Status);
        Set("updated_at", Js.IsoNow());
        db.Run($"UPDATE rca_drafts SET {string.Join(", ", sets)} WHERE id = @id", args);
        return Get(db, id);
    }

    private static string Bullets(List<string> xs) => xs.Count > 0 ? string.Join("\n", xs.Select(x => $"- {x}")) : "- _None recorded_";

    public static string Markdown(RcaDraft rca, Incident incident)
    {
        var timeline = rca.Timeline.Count > 0
            ? string.Join("\n", rca.Timeline.Select(e => $"- **{e.Timestamp}** — {e.Event}{(e.EvidenceIds.Count > 0 ? $" _(evidence: {string.Join(", ", e.EvidenceIds)})_" : "")}"))
            : "- _None recorded_";
        return string.Join("\n",
            $"# Post-incident report: {incident.Key} — {incident.Title}",
            "",
            $"> **AI-generated draft — review required.** Status: {rca.Status}. Version {rca.Version}, generated {rca.GeneratedAt}, last edited {rca.UpdatedAt}.",
            "> Source: ResolveAI demo dataset (synthetic data, not live telemetry).",
            "",
            "| Field | Value |",
            "| --- | --- |",
            $"| Service | {incident.ServiceName} |",
            $"| Severity | {incident.Severity} |",
            $"| Environment | {incident.Environment} |",
            $"| Started | {incident.StartedAt} |",
            $"| Detected | {incident.DetectedAt} |",
            "",
            "## Impact",
            "",
            rca.Impact,
            "",
            "## Timeline (UTC)",
            "",
            timeline,
            "",
            "## Root cause",
            "",
            rca.RootCause,
            "",
            "## Contributing factors",
            "",
            Bullets(rca.ContributingFactors),
            "",
            "## Resolution",
            "",
            Bullets(rca.ResolutionSteps),
            "",
            "## Preventive actions",
            "",
            Bullets(rca.PreventionActions),
            "");
    }
}
