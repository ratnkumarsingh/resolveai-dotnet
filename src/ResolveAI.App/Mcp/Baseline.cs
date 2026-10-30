using ResolveAI.Contracts;
using ResolveAI.Mcp;

namespace ResolveAI.App.Mcp;

public sealed record PlannedCall(string Tool, Dictionary<string, object?> Args);

public static class Baseline
{
    /// <summary>The same window the evidence loader uses: 30 minutes before the start to an hour after detection.</summary>
    public static (string From, string To) IncidentWindow(Incident incident)
    {
        var start = Js.ParseDate(incident.StartedAt);
        var detected = Js.ParseDate(incident.DetectedAt);
        return (Js.IsoSeconds(start.AddMinutes(-30)), Js.IsoSeconds((detected > start ? detected : start).AddHours(1)));
    }

    /// <summary>
    /// The fixed checks ResolveAI runs for every investigation, before any model is involved. Only tools a connected
    /// server offers are planned, so a system that is turned off simply contributes nothing.
    /// </summary>
    public static List<PlannedCall> Plan(Incident incident, McpConfig cfg, IReadOnlyList<ToolSpec> available)
    {
        var has = available.Select(t => t.Name).ToHashSet();
        var svc = cfg.Services.GetValueOrDefault(incident.ServiceName);
        var (from, to) = IncidentWindow(incident);
        var calls = new List<PlannedCall>();
        void Add(string tool, Dictionary<string, object?> args)
        {
            if (has.Contains(tool)) calls.Add(new PlannedCall(tool, args));
        }

        if (!string.IsNullOrEmpty(svc?.Database))
        {
            Add("db_health", []);
            Add("db_blocking", []);
            Add("db_errors", new() { ["from"] = from, ["to"] = to });
            Add("db_deadlocks", new() { ["from"] = from, ["to"] = to });
        }

        // The files server resolves relative paths against its first allowed root; using them keeps local
        // absolute paths out of tool arguments (and out of what the model sees).
        var root = cfg.Files.AllowedRoots.FirstOrDefault();
        string ShortPath(string p)
        {
            var rel = root is not null ? Path.GetRelativePath(root, p) : p;
            return rel.Length > 0 && !rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel) ? rel.Replace(Path.DirectorySeparatorChar, '/') : p;
        }
        foreach (var logRoot in (svc?.LogRoots ?? []).Select(ShortPath))
        {
            Add("files_search", new() { ["root"] = logRoot, ["phrase"] = "ERROR", ["from"] = from, ["to"] = to, ["max_matches"] = 30L });
            Add("files_search", new() { ["root"] = logRoot, ["phrase"] = "WARN", ["from"] = from, ["to"] = to, ["max_matches"] = 15L });
        }

        // Deploys in the 23 hours before the incident window closes (tools cap a window at 24 hours).
        Add("deploys_recent", new() { ["service"] = incident.ServiceName, ["from"] = Js.IsoSeconds(Js.ParseDate(to).AddHours(-23)), ["to"] = to });
        foreach (var name in svc?.KeyMetrics ?? []) Add("metrics_series", new() { ["service"] = incident.ServiceName, ["name"] = name, ["from"] = from, ["to"] = to });
        return calls;
    }
}
