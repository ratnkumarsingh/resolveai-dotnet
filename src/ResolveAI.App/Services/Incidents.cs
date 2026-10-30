using Dapper;
using ResolveAI.App.Data;
using ResolveAI.Contracts;

namespace ResolveAI.App.Services;

public sealed record IncidentFilters(string? Status, string? Severity, string? Service, string? Q, string? Review, int Page, int PageSize);

public static class Incidents
{
    private const string Columns = "id, key, title, service_name, severity, status, environment, started_at, detected_at, resolved_at, description, impact_summary";

    /// <summary>
    /// none: never investigated to completion. verified: a human recorded the root cause and marked the latest RCA reviewed.
    /// pending: anything in between, i.e. AI output that no one has confirmed yet.
    /// </summary>
    private const string ReviewState = """
        CASE
          WHEN NOT EXISTS (SELECT 1 FROM investigations v WHERE v.incident_id = incidents.id AND v.status = 'completed') THEN 'none'
          WHEN EXISTS (SELECT 1 FROM feedback f JOIN investigations v ON v.id = f.investigation_id
                        WHERE v.incident_id = incidents.id AND f.verified_root_cause IS NOT NULL)
           AND (SELECT r.status FROM rca_drafts r WHERE r.incident_id = incidents.id ORDER BY r.version DESC LIMIT 1) = 'reviewed' THEN 'verified'
          ELSE 'pending' END
        """;

    public static IncidentList List(Db db, IncidentFilters f)
    {
        var where = new List<string>();
        var args = new DynamicParameters();
        if (f.Status is not null) { where.Add("status = @status"); args.Add("status", f.Status); }
        if (f.Severity is not null) { where.Add("severity = @severity"); args.Add("severity", f.Severity); }
        if (f.Service is not null) { where.Add("service_name = @service"); args.Add("service", f.Service); }
        if (f.Q is not null)
        {
            where.Add("(key LIKE @like OR title LIKE @like OR service_name LIKE @like)");
            args.Add("like", $"%{f.Q}%");
        }
        if (f.Review is not null) { where.Add($"({ReviewState}) = @review"); args.Add("review", f.Review); }
        var clause = where.Count > 0 ? $"WHERE {string.Join(" AND ", where)}" : "";
        var total = db.Get<int>($"SELECT COUNT(*) FROM incidents {clause}", args);
        args.Add("limit", f.PageSize);
        args.Add("offset", (f.Page - 1) * f.PageSize);
        // Active incidents first, then most severe, then newest.
        var items = db.All<IncidentListItem>(
            $"""
            SELECT {Columns}, {ReviewState} AS review_state FROM incidents {clause}
             ORDER BY CASE status WHEN 'resolved' THEN 1 ELSE 0 END, severity, started_at DESC
             LIMIT @limit OFFSET @offset
            """,
            args);
        return new IncidentList { Items = items, Total = total, Page = f.Page, PageSize = f.PageSize };
    }

    /// <summary>Looks an incident up by UUID or by human key such as "INC-1042".</summary>
    public static Incident? Find(Db db, string idOrKey) => db.Get<Incident>($"SELECT {Columns} FROM incidents WHERE id = @idOrKey OR key = @idOrKey", new { idOrKey });

    public static List<string> Services(Db db) => db.All<string>("SELECT DISTINCT service_name FROM incidents ORDER BY service_name");
}
