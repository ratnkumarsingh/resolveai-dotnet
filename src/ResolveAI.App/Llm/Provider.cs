using System.Text.Json;
using ResolveAI.Contracts;

namespace ResolveAI.App.Llm;

public sealed class RcaHypothesisInput
{
    public int Rank { get; set; }
    public string Title { get; set; } = "";
    public string Explanation { get; set; } = "";
    public string Confidence { get; set; } = "";
    public bool Speculative { get; set; }
    public List<string> EvidenceIds { get; set; } = [];
    public bool? Helpful { get; set; }
}

public sealed class RcaSimilarInput
{
    public string Id { get; set; } = "";
    public string Key { get; set; } = "";
    public string RootCause { get; set; } = "";
    public string Resolution { get; set; } = "";
}

public sealed class RcaInput
{
    public EvidencePacket Packet { get; set; } = new();
    public string? Summary { get; set; }
    public List<RcaHypothesisInput> Hypotheses { get; set; } = [];
    public string? VerifiedRootCause { get; set; }
    public List<string> EngineerNotes { get; set; } = [];
    public List<RcaSimilarInput> Similar { get; set; } = [];
}

public sealed record LlmResult<T>(T Output, string? Model, Dictionary<string, long>? Usage = null);

/// <summary>A read-only tool the model may call during follow-up checks.</summary>
public sealed record FollowUpTool(string Name, string Description, JsonElement InputSchema);

/// <summary>
/// Runs one tool for the model and returns the text it sees, which starts with the evidence ID to cite.
/// It enforces the call limit itself, so a provider can't exceed it.
/// </summary>
public delegate Task<string> CallTool(string name, Dictionary<string, object?> args, CancellationToken ct);

/// <summary>Optional extra evidence gathering before analysis: the provider decides which tools to call.</summary>
public delegate Task<Dictionary<string, long>?> FollowUp(EvidencePacket packet, IReadOnlyList<FollowUpTool> tools, CallTool call, int maxCalls, CancellationToken ct);

public interface ILlmProvider
{
    string Mode { get; }
    string? Model { get; }
    Task<LlmResult<InvestigationOutput>> Investigate(EvidencePacket packet, CancellationToken ct);
    Task<LlmResult<RcaOutput>> DraftRca(RcaInput input, CancellationToken ct);
    FollowUp? FollowUp { get; }
}

/// <summary>An error whose message is safe to show the user. <c>Retryable</c> marks malformed-output failures.</summary>
public sealed class LlmError(string message, bool retryable = false) : Exception(message)
{
    public bool Retryable { get; } = retryable;
}
