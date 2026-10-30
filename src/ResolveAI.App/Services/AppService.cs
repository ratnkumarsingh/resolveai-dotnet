using System.Security.Cryptography;
using System.Text;
using ResolveAI.App.Data;
using ResolveAI.App.Llm;
using ResolveAI.Contracts;

namespace ResolveAI.App.Services;

/// <summary>A failure with an HTTP status and a message that is safe to show. The UI shows the message; the API returns both.</summary>
public sealed class AppError(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

public sealed record Providers(ILlmProvider Recorded, ILlmProvider? Live);

public sealed record KnowledgeBody(string Id, string Title, string DocType, string? ServiceName, string Body);

/// <summary>Who is asking: both the HTTP API and the Blazor UI supply this, so the same checks apply to each.</summary>
public sealed record Caller(string? AccessCode, bool IsLocal);

/// <summary>
/// Everything the app does. The HTTP routes and the Blazor components both call this, so the access code, the daily live
/// limit, input limits and ownership checks can't be bypassed from either side.
/// </summary>
public sealed class AppService(Db db, Providers providers, Systems? systems, AppSettings settings)
{
    private static readonly HashSet<string> ReviewStates = ["none", "pending", "verified"];

    private ILlmProvider DefaultProvider => settings.LlmMode == "live" && providers.Live is not null ? providers.Live : providers.Recorded;
    private bool LiveAvailable => providers.Live is not null && (settings.LlmMode == "live" || !string.IsNullOrEmpty(settings.LiveAccessCode));

    private static byte[] Digest(string s) => SHA256.HashData(Encoding.UTF8.GetBytes(s));

    private bool HasAccessCode(Caller caller) =>
        !string.IsNullOrEmpty(settings.LiveAccessCode) && !string.IsNullOrEmpty(caller.AccessCode)
        && CryptographicOperations.FixedTimeEquals(Digest(caller.AccessCode), Digest(settings.LiveAccessCode));

    /// <summary>
    /// Picks the provider for one model call. Live costs money, so unless the server runs live by default
    /// it needs the access code, and every live call counts against the daily limit.
    /// </summary>
    private ILlmProvider ProviderFor(Caller caller, string mode)
    {
        if (mode == "recorded") return providers.Recorded;
        if (providers.Live is null) throw new AppError(400, "Live mode is not configured on this server (no API key).");
        if (settings.LlmMode != "live")
        {
            if (string.IsNullOrEmpty(settings.LiveAccessCode)) throw new AppError(400, "Live mode is not enabled on this server (no access code configured).");
            if (!HasAccessCode(caller)) throw new AppError(403, "Live mode needs a valid access code. Enter it under Settings.");
        }
        if (Investigations.LiveRunsToday(db) >= settings.LiveDailyLimit)
        {
            throw new AppError(429, $"Today's live limit ({settings.LiveDailyLimit} model calls) is used up. Use recorded mode or try again tomorrow (UTC).");
        }
        return providers.Live;
    }

    private Incident IncidentOr404(string idOrKey) => Incidents.Find(db, idOrKey) ?? throw new AppError(404, "Incident not found");

    public AppConfig Config() => new()
    {
        LlmMode = DefaultProvider.Mode,
        Model = DefaultProvider.Mode == "live" ? settings.Model : "recorded fixtures",
        LiveAvailable = LiveAvailable,
        LiveModel = providers.Live is not null ? settings.Model : null,
        LiveDailyLimit = settings.LiveDailyLimit,
        LiveRunsToday = Investigations.LiveRunsToday(db),
        DemoMode = settings.DemoMode,
        DataSource = "Demo dataset",
        MaxFollowUpCalls = systems?.MaxFollowUps ?? 0,
    };

    public SystemsResponse Systems() => new() { Systems = systems?.Hub.Status() ?? [], Enabled = systems is not null };

    public IncidentList ListIncidents(string? status, string? severity, string? service, string? q, string? review, int page, int pageSize)
    {
        var filters = new IncidentFilters(
            string.IsNullOrEmpty(status) ? null : status, string.IsNullOrEmpty(severity) ? null : severity, string.IsNullOrEmpty(service) ? null : service,
            string.IsNullOrWhiteSpace(q) ? null : q.Trim(), review is not null && ReviewStates.Contains(review) ? review : null,
            Math.Max(1, page), Math.Min(100, Math.Max(1, pageSize)));
        var list = Incidents.List(db, filters);
        list.Services = Incidents.Services(db);
        return list;
    }

    public IncidentDetail IncidentDetail(string idOrKey)
    {
        var incident = IncidentOr404(idOrKey);
        return new IncidentDetail
        {
            Incident = incident,
            LatestInvestigationId = Investigations.LatestId(db, incident.Id),
            Investigations = Investigations.ListRuns(db, incident.Id),
            RcaDrafts = Rca.List(db, incident.Id),
        };
    }

    public IncidentEvidence Evidence(string idOrKey) => Pipeline.EvidenceLoader.Load(db, IncidentOr404(idOrKey));

    public (string InvestigationId, string Status) StartInvestigation(string idOrKey, StartInvestigationBody body, Caller caller)
    {
        var incident = IncidentOr404(idOrKey);
        if (body.Question?.Length > 500) throw new AppError(400, "Invalid request: question must be at most 500 characters.");
        if (body.Mode is not (null or "recorded" or "live")) throw new AppError(400, "Invalid request: mode must be recorded or live.");
        var running = db.Get<string>("SELECT id FROM investigations WHERE incident_id = @id AND status IN ('queued', 'running') LIMIT 1", new { id = incident.Id });
        if (running is not null) return (running, "running");
        var llm = ProviderFor(caller, body.Mode ?? DefaultProvider.Mode);
        var question = string.IsNullOrWhiteSpace(body.Question) ? null : body.Question.Trim();
        return (Investigations.Start(db, llm, systems, incident, question), "queued");
    }

    public Investigation Investigation(string id) => Investigations.Get(db, id) ?? throw new AppError(404, "Investigation not found");

    public string AddFeedback(string investigationId, FeedbackBody body)
    {
        var row = Investigations.GetRow(db, investigationId) ?? throw new AppError(404, "Investigation not found");
        if (body.Comment?.Length > 2000) throw new AppError(400, "Invalid request: comment must be at most 2000 characters.");
        if (body.VerifiedRootCause?.Length > 2000) throw new AppError(400, "Invalid request: verified_root_cause must be at most 2000 characters.");
        if (body.HypothesisId is not null
            && db.Get<int>("SELECT COUNT(*) FROM hypotheses WHERE id = @hypothesis AND investigation_id = @investigation", new { hypothesis = body.HypothesisId, investigation = row.Id }) == 0)
        {
            throw new AppError(400, "Hypothesis does not belong to this investigation");
        }
        var comment = string.IsNullOrWhiteSpace(body.Comment) ? null : body.Comment.Trim();
        var verified = string.IsNullOrWhiteSpace(body.VerifiedRootCause) ? null : body.VerifiedRootCause.Trim();
        if (body.Helpful is null && comment is null && verified is null) throw new AppError(400, "Feedback needs a helpful flag, a comment or a verified root cause");
        var id = Guid.NewGuid().ToString();
        db.Run(
            """
            INSERT INTO feedback (id, investigation_id, hypothesis_id, helpful, comment, verified_root_cause, created_at)
            VALUES (@id, @investigation, @hypothesis, @helpful, @comment, @verified, @now)
            """,
            new { id, investigation = row.Id, hypothesis = body.HypothesisId, helpful = body.Helpful is null ? (int?)null : body.Helpful.Value ? 1 : 0, comment, verified, now = Js.IsoNow() });
        return id;
    }

    public async Task<RcaDraft> CreateRca(string idOrKey, string investigationId, Caller caller)
    {
        var incident = IncidentOr404(idOrKey);
        var inv = Investigations.GetRow(db, investigationId);
        if (inv is null || inv.IncidentId != incident.Id) throw new AppError(400, "That investigation does not belong to this incident");
        if (inv.Status != "completed") throw new AppError(409, "Run an investigation to completion before drafting an RCA");
        // The draft is written in the same mode as the investigation it summarises.
        var llm = ProviderFor(caller, inv.LlmMode);
        return await Rca.Create(db, llm, incident, investigationId);
    }

    public RcaDraft GetRca(string id) => Rca.Get(db, id) ?? throw new AppError(404, "RCA draft not found");

    public RcaDraft PatchRca(string id, PatchRcaBody patch)
    {
        static void Text(string name, string? value, int max)
        {
            if (value?.Length > max) throw new AppError(400, $"Invalid request: {name} must be at most {max} characters.");
        }
        static void Items(string name, List<string>? values)
        {
            if (values is null) return;
            if (values.Count > 30) throw new AppError(400, $"Invalid request: {name} must have at most 30 items.");
            if (values.Any(v => v.Length > 1000)) throw new AppError(400, $"Invalid request: {name} items must be at most 1000 characters.");
        }
        Text("impact", patch.Impact, 5000);
        Text("root_cause", patch.RootCause, 5000);
        Items("contributing_factors", patch.ContributingFactors);
        Items("resolution_steps", patch.ResolutionSteps);
        Items("prevention_actions", patch.PreventionActions);
        if (patch.Status is not (null or "draft" or "reviewed")) throw new AppError(400, "Invalid request: status must be draft or reviewed.");
        return Rca.Patch(db, id, patch) ?? throw new AppError(404, "RCA draft not found");
    }

    public (string FileName, string Markdown) ExportRca(string id)
    {
        var rca = GetRca(id);
        var incident = IncidentOr404(rca.IncidentId);
        return ($"{incident.Key}-rca-v{rca.Version}.md", Rca.Markdown(rca, incident));
    }

    public KnowledgeBody Knowledge(string id) =>
        db.Get<KnowledgeBody>("SELECT id, title, doc_type, service_name, body FROM knowledge_documents WHERE id = @id", new { id }) ?? throw new AppError(404, "Document not found");

    public void DemoReset(Caller caller)
    {
        if (!settings.DemoMode) throw new AppError(404, "Not found");
        if (!settings.DemoResetAnyIp && !caller.IsLocal && !HasAccessCode(caller))
        {
            throw new AppError(403, "Demo reset is only available from localhost or with the access code");
        }
        if (db.Get<int>("SELECT COUNT(*) FROM investigations WHERE status IN ('queued', 'running')") > 0)
        {
            throw new AppError(409, "An investigation is still running; wait for it to finish before resetting");
        }
        Seed.Demo(db);
    }
}
