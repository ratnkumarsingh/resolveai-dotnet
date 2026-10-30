using System.Text.Json.Serialization;

namespace ResolveAI.Contracts;

// Property names serialise to snake_case (see Json.Options), matching the JSON the API has always returned.

public sealed class RecommendedCheck
{
    public string Step { get; set; } = "";
    public string Why { get; set; } = "";
    public string ExpectedSignal { get; set; } = "";
}

public sealed class LlmHypothesis
{
    public string Title { get; set; } = "";
    public string Explanation { get; set; } = "";
    public string Confidence { get; set; } = "low";
    public List<string> EvidenceIds { get; set; } = [];
    public List<string> CounterEvidence { get; set; } = [];
    public List<string> Unknowns { get; set; } = [];
    public List<RecommendedCheck> RecommendedChecks { get; set; } = [];
}

public sealed class SimilarIncidentPick
{
    public string IncidentId { get; set; } = "";
    public string SimilarityReason { get; set; } = "";
}

public sealed class InvestigationOutput
{
    public string Summary { get; set; } = "";
    public bool InsufficientEvidence { get; set; }
    public List<LlmHypothesis> Hypotheses { get; set; } = [];
    public List<SimilarIncidentPick> SimilarIncidents { get; set; } = [];
    public List<string> Limitations { get; set; } = [];
}

public sealed class RcaTimelineEntry
{
    public string Timestamp { get; set; } = "";
    public string Event { get; set; } = "";
    public List<string> EvidenceIds { get; set; } = [];
}

public class RcaOutput
{
    public string Impact { get; set; } = "";
    public List<RcaTimelineEntry> Timeline { get; set; } = [];
    public string RootCause { get; set; } = "";
    public List<string> ContributingFactors { get; set; } = [];
    public List<string> ResolutionSteps { get; set; } = [];
    public List<string> PreventionActions { get; set; } = [];
}

public sealed class StartInvestigationBody
{
    public string? Question { get; set; }
    public string? Mode { get; set; }
}

public sealed class CreateRcaBody
{
    public string InvestigationId { get; set; } = "";
}

public sealed class PatchRcaBody
{
    public string? Impact { get; set; }
    public string? RootCause { get; set; }
    public List<string>? ContributingFactors { get; set; }
    public List<string>? ResolutionSteps { get; set; }
    public List<string>? PreventionActions { get; set; }
    public List<RcaTimelineEntry>? Timeline { get; set; }
    public string? Status { get; set; }
}

public sealed class FeedbackBody
{
    public string? HypothesisId { get; set; }
    public bool? Helpful { get; set; }
    public string? Comment { get; set; }
    public string? VerifiedRootCause { get; set; }
}

public class Incident
{
    public string Id { get; set; } = "";
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public string ServiceName { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Status { get; set; } = "";
    public string Environment { get; set; } = "";
    public string StartedAt { get; set; } = "";
    public string DetectedAt { get; set; } = "";
    public string? ResolvedAt { get; set; }
    public string Description { get; set; } = "";
    public string ImpactSummary { get; set; } = "";
}

public sealed class IncidentListItem : Incident
{
    public string ReviewState { get; set; } = "none";
}

public sealed class LogEvent
{
    public string Id { get; set; } = "";
    public string Ref { get; set; } = "";
    public string Timestamp { get; set; } = "";
    public string ServiceName { get; set; } = "";
    public string Level { get; set; } = "";
    public string Message { get; set; } = "";
    public string? ErrorSignature { get; set; }
    public string? TraceId { get; set; }
    public string Source { get; set; } = "";
}

public sealed class Deployment
{
    public string Id { get; set; } = "";
    public string Ref { get; set; } = "";
    public string ServiceName { get; set; } = "";
    public string Environment { get; set; } = "";
    public string Version { get; set; } = "";
    public string DeployedAt { get; set; } = "";
    public string ChangeSummary { get; set; } = "";
    public string? CommitRef { get; set; }
}

public sealed class MetricSnapshot
{
    public string Id { get; set; } = "";
    public string Ref { get; set; } = "";
    public string Name { get; set; } = "";
    public string Unit { get; set; } = "";
    public double Baseline { get; set; }
    public double Peak { get; set; }
    public string ObservedAt { get; set; } = "";
}

public sealed class TimelineEvent
{
    public string Id { get; set; } = "";
    public string Ref { get; set; } = "";
    public string Timestamp { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Description { get; set; } = "";
}

public sealed class KnowledgeDocument
{
    public string Id { get; set; } = "";
    public string Ref { get; set; } = "";
    public string Title { get; set; } = "";
    public string DocType { get; set; } = "";
    public string? ServiceName { get; set; }
    public string Body { get; set; } = "";
}

public sealed class HistoricalIncident
{
    public string Id { get; set; } = "";
    public string Ref { get; set; } = "";
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public string ServiceName { get; set; } = "";
    public string Symptoms { get; set; } = "";
    public string RootCause { get; set; } = "";
    public string Resolution { get; set; } = "";
    public string ResolvedAt { get; set; } = "";
    public string? RcaDocumentId { get; set; }
}

public sealed class ErrorSignal
{
    public string Signature { get; set; } = "";
    public int Count { get; set; }
    public string FirstSeen { get; set; } = "";
    public string LastSeen { get; set; } = "";
}

public sealed class IncidentEvidence
{
    public Incident Incident { get; set; } = new();
    public List<TimelineEvent> Timeline { get; set; } = [];
    public List<LogEvent> Logs { get; set; } = [];
    public List<ErrorSignal> ErrorSignals { get; set; } = [];
    public List<Deployment> Deployments { get; set; } = [];
    public List<MetricSnapshot> Metrics { get; set; } = [];
    public List<KnowledgeDocument> Runbooks { get; set; } = [];
}

public sealed class ResolvedEvidence
{
    public string Ref { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Label { get; set; } = "";
    public string Excerpt { get; set; } = "";
    public string? Timestamp { get; set; }
}

public sealed class HypothesisFeedback
{
    public bool? Helpful { get; set; }
}

public sealed class Hypothesis
{
    public string Id { get; set; } = "";
    public int Rank { get; set; }
    public string Title { get; set; } = "";
    public string Explanation { get; set; } = "";
    public string Confidence { get; set; } = "low";
    public double Score { get; set; }
    public bool Speculative { get; set; }
    public List<ResolvedEvidence> Evidence { get; set; } = [];
    public List<string> CounterEvidence { get; set; } = [];
    public List<string> Unknowns { get; set; } = [];
    public List<RecommendedCheck> RecommendedChecks { get; set; } = [];
    public HypothesisFeedback? Feedback { get; set; }
}

public sealed class SimilarIncidentResult
{
    public HistoricalIncident Historical { get; set; } = new();
    public double MatchScore { get; set; }
    public List<string> MatchedTerms { get; set; } = [];
    public string SimilarityReason { get; set; } = "";
}

/// <summary>One step of how an investigation was produced: timing plus the counts that step reported.</summary>
public sealed class TraceStage
{
    public string Stage { get; set; } = "";
    public string StartedAt { get; set; } = "";
    public long DurationMs { get; set; }
    public Dictionary<string, object?> Details { get; set; } = [];
}

/// <summary>One read-only tool call against a system. <c>Ref</c> (mcp:N) is the evidence ID hypotheses cite.</summary>
public sealed class ToolCallRecord
{
    public string Ref { get; set; } = "";
    public int Seq { get; set; }
    /// <summary>baseline = ResolveAI's fixed checks; follow_up = a call Claude chose to make.</summary>
    public string Phase { get; set; } = "baseline";
    public string System { get; set; } = "";
    public string Server { get; set; } = "";
    public string Tool { get; set; } = "";
    public Dictionary<string, object?> Args { get; set; } = [];
    public bool Ok { get; set; }
    public string Summary { get; set; } = "";
    public List<Dictionary<string, object?>> Rows { get; set; } = [];
    public bool Truncated { get; set; }
    public long DurationMs { get; set; }
    public string StartedAt { get; set; } = "";
}

public sealed class Investigation
{
    public string Id { get; set; } = "";
    public string IncidentId { get; set; } = "";
    public string Status { get; set; } = "";
    public string? Stage { get; set; }
    public string? Question { get; set; }
    public string LlmMode { get; set; } = "recorded";
    public string? ModelName { get; set; }
    public string PromptVersion { get; set; } = "";
    public string StartedAt { get; set; } = "";
    public string? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public string? Summary { get; set; }
    public bool InsufficientEvidence { get; set; }
    public List<string> Limitations { get; set; } = [];
    public List<string> ValidationIssues { get; set; } = [];
    public List<Hypothesis> Hypotheses { get; set; } = [];
    public List<SimilarIncidentResult> SimilarIncidents { get; set; } = [];
    public int EvidenceCount { get; set; }
    public List<TraceStage> Trace { get; set; } = [];
    public List<ToolCallRecord> ToolCalls { get; set; } = [];
}

public sealed class SystemTool
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
}

public sealed class SystemStatus
{
    public string System { get; set; } = "";
    public string Server { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>disabled = turned off in mcp.config.json; error = configured but failed to start.</summary>
    public string Status { get; set; } = "disabled";
    public string? Detail { get; set; }
    public List<SystemTool> Tools { get; set; } = [];
    public string CheckedAt { get; set; } = "";
}

public sealed class InvestigationRun
{
    public string Id { get; set; } = "";
    public string Status { get; set; } = "";
    public string LlmMode { get; set; } = "recorded";
    public string StartedAt { get; set; } = "";
    public string? TopHypothesis { get; set; }
}

public sealed class RcaDraft : RcaOutput
{
    public string Id { get; set; } = "";
    public string IncidentId { get; set; } = "";
    public string InvestigationId { get; set; } = "";
    public int Version { get; set; }
    public string Status { get; set; } = "draft";
    public string GeneratedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    public List<string> EvidenceIds { get; set; } = [];
    public List<string> ValidationIssues { get; set; } = [];
    public string LlmMode { get; set; } = "recorded";
}

public sealed class AppConfig
{
    /// <summary>Mode used when a run doesn't ask for live.</summary>
    public string LlmMode { get; set; } = "recorded";
    public string Model { get; set; } = "";
    /// <summary>Live Claude runs are possible on request (API key and access code are configured).</summary>
    public bool LiveAvailable { get; set; }
    public string? LiveModel { get; set; }
    public int LiveDailyLimit { get; set; }
    public int LiveRunsToday { get; set; }
    public bool DemoMode { get; set; }
    public string DataSource { get; set; } = "Demo dataset";
    /// <summary>Most extra tool calls Claude may make per live investigation.</summary>
    public int MaxFollowUpCalls { get; set; }
}

public sealed class IncidentList
{
    public List<IncidentListItem> Items { get; set; } = [];
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public List<string> Services { get; set; } = [];
}

public sealed class IncidentDetail
{
    public Incident Incident { get; set; } = new();
    public string? LatestInvestigationId { get; set; }
    public List<InvestigationRun> Investigations { get; set; } = [];
    public List<RcaDraft> RcaDrafts { get; set; } = [];
}

public sealed class SystemsResponse
{
    public List<SystemStatus> Systems { get; set; } = [];
    public bool Enabled { get; set; }
}

public sealed class ErrorResponse
{
    public string Error { get; set; } = "";
}

/// <summary>The one result shape every ResolveAI tool returns.</summary>
public sealed class ToolPayload
{
    public string Summary { get; set; } = "";
    public List<Dictionary<string, object?>> Rows { get; set; } = [];
    public bool Truncated { get; set; }
    public string Source { get; set; } = "";
}

/// <summary>Reads occurrences only when the item has repeats, so JSON matches what the model and the size budget saw.</summary>
public sealed class PacketItem
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string? Timestamp { get; set; }
    public string Source { get; set; } = "";
    public string Excerpt { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Occurrences { get; set; }
}

public sealed class PacketIncident
{
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public string Service { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Environment { get; set; } = "";
    public string StartedAt { get; set; } = "";
    public string DetectedAt { get; set; } = "";
    public string Description { get; set; } = "";
    public string ImpactSummary { get; set; } = "";
}

public sealed class EvidencePacket
{
    public PacketIncident Incident { get; set; } = new();
    public string? Question { get; set; }
    public List<PacketItem> Items { get; set; } = [];
    public List<string> SimilarCandidates { get; set; } = [];
    public int Redactions { get; set; }
    public bool Truncated { get; set; }
}
