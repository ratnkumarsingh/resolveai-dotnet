using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using ResolveAI.Contracts;

namespace ResolveAI.Mcp;

/// <summary>Hard caps every tool applies, so one call can never return an unbounded amount of data.</summary>
public static class Limits
{
    /// <summary>Longest time window a tool will search.</summary>
    public static readonly TimeSpan MaxWindow = TimeSpan.FromHours(24);
    public const int MaxRows = 200;
    /// <summary>Upper bound on the JSON text a tool returns.</summary>
    public const int MaxOutputChars = 64_000;
    /// <summary>Longest single text value (a log line, a query text) inside a result.</summary>
    public const int MaxValueChars = 500;
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    public static string Clip(string s, int max = MaxValueChars) => Js.Clip(s, max);
}

/// <summary>Thrown inside a tool for an expected, user-facing failure.</summary>
public sealed class ToolError(string message) : Exception(message);

public static class ToolResult
{
    private static CallToolResult Text(string text, bool isError = false) =>
        new() { Content = [new TextContentBlock { Text = text }], IsError = isError };

    /// <summary>Serialises a payload, dropping trailing rows until it fits the output cap.</summary>
    public static CallToolResult Ok(string summary, IEnumerable<Dictionary<string, object?>> rows, string source, bool truncated = false)
    {
        var all = rows.ToList();
        var kept = all.Take(Limits.MaxRows).ToList();
        var cut = truncated || kept.Count < all.Count;
        var text = Serialize(summary, kept, cut, source);
        while (text.Length > Limits.MaxOutputChars && kept.Count > 0)
        {
            kept = kept.Take((int)Math.Floor(kept.Count * 0.8)).ToList();
            cut = true;
            text = Serialize(summary, kept, cut, source);
        }
        return Text(text);
    }

    /// <summary>A tool-level failure the agent should see (bad path, system unavailable), not a protocol error.</summary>
    public static CallToolResult Fail(string message) => Text(Serialize(message, [], false, "error"), isError: true);

    private static string Serialize(string summary, List<Dictionary<string, object?>> rows, bool truncated, string source) =>
        Json.Serialize(new ToolPayload { Summary = summary, Rows = rows, Truncated = truncated, Source = source });
}

public readonly record struct TimeWindow(DateTimeOffset From, DateTimeOffset To)
{
    /// <summary>Parses a from/to pair; <c>to</c> defaults to now and the span is capped at 24 hours.</summary>
    public static TimeWindow Parse(string from, string? to)
    {
        var fromOk = Js.TryParseDate(from, out var f);
        var t = DateTimeOffset.UtcNow;
        var toOk = to is null || Js.TryParseDate(to, out t);
        if (!fromOk || !toOk) throw new ToolError("from/to must be ISO-8601 times.");
        if (t < f) throw new ToolError("to must be after from.");
        if (t - f > Limits.MaxWindow) throw new ToolError("The time window is limited to 24 hours; narrow from/to.");
        return new TimeWindow(f, t);
    }

    public bool Contains(string iso) => Js.TryParseDate(iso, out var t) && t >= From && t <= To;
}

/// <summary>What ResolveAI knows about one service: where its logs, code and database live.</summary>
public sealed class ServiceConfig
{
    public List<string> LogRoots { get; set; } = [];
    /// <summary>A git working copy for this service's code, if any.</summary>
    public string? Repo { get; set; }
    /// <summary>SQL Server database the service uses, if any.</summary>
    public string? Database { get; set; }
    /// <summary>Metric names worth pulling in every baseline.</summary>
    public List<string> KeyMetrics { get; set; } = [];
}

public sealed class FilesConfig
{
    /// <summary>Every files_* path must resolve inside one of these folders.</summary>
    public List<string> AllowedRoots { get; set; } = [];
}

public sealed class DeploysConfig
{
    /// <summary>Optional JSON feed of deployments (used by the demo).</summary>
    public string? Feed { get; set; }
}

public sealed class MetricsConfig
{
    public string? SeriesDir { get; set; }
}

public sealed class SqlServerConfig
{
    public bool Enabled { get; set; }
    public string Server { get; set; } = "localhost";
    /// <summary>msnodesqlv8 = Windows sign-in; tedious = SQL login over TCP. Names kept so existing config files stay valid; both use SqlClient.</summary>
    public string Driver { get; set; } = "tedious";
    public int? Port { get; set; }
    public bool TrustServerCertificate { get; set; } = true;
    /// <summary>Run every query as this read-only login (EXECUTE AS LOGIN), so the server enforces read-only even for an admin connection.</summary>
    public string? ExecuteAs { get; set; }
}

public sealed partial class McpConfig
{
    public Dictionary<string, ServiceConfig> Services { get; set; } = [];
    public FilesConfig Files { get; set; } = new();
    public DeploysConfig Deploys { get; set; } = new();
    public MetricsConfig Metrics { get; set; } = new();
    public SqlServerConfig Sqlserver { get; set; } = new();

    // \z, not $: in .NET, $ also matches before a trailing newline. This value is written into SQL text.
    [GeneratedRegex(@"\A[A-Za-z0-9_.-]+\z")]
    private static partial Regex LoginName();

    /// <summary>Finds the repository (or the deployed app folder) by the committed example config.</summary>
    public static string RepoRoot()
    {
        // From the executable's folder for a normal build, then the working folder for a published copy run from the repository.
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "mcp.config.example.json"))) return dir.FullName;
            }
        }
        throw new InvalidOperationException($"Could not find mcp.config.example.json above {AppContext.BaseDirectory} or {Directory.GetCurrentDirectory()}");
    }

    /// <summary>Finds the config file: RESOLVEAI_MCP_CONFIG, then mcp.config.json, then the committed example.</summary>
    public static string ConfigPath(string? explicitPath = null)
    {
        explicitPath ??= Environment.GetEnvironmentVariable("RESOLVEAI_MCP_CONFIG");
        if (!string.IsNullOrEmpty(explicitPath)) return Path.GetFullPath(explicitPath);
        var root = RepoRoot();
        var local = Path.Combine(root, "mcp.config.json");
        return File.Exists(local) ? local : Path.Combine(root, "mcp.config.example.json");
    }

    /// <summary>Loads and validates the config, resolving every relative path against the config file's folder.</summary>
    public static McpConfig Load(string? path = null)
    {
        path = ConfigPath(path);
        var cfg = JsonSerializer.Deserialize<McpConfig>(File.ReadAllText(path), Json.Options) ?? new McpConfig();
        if (cfg.Sqlserver.Driver is not ("msnodesqlv8" or "tedious")) throw new InvalidDataException($"sqlserver.driver must be msnodesqlv8 or tedious, not {cfg.Sqlserver.Driver}.");
        if (cfg.Sqlserver.ExecuteAs is not null && !LoginName().IsMatch(cfg.Sqlserver.ExecuteAs)) throw new InvalidDataException("sqlserver.execute_as is not a plain login name.");

        var folder = Path.GetDirectoryName(path)!;
        string Abs(string p) => Path.GetFullPath(p, folder);
        foreach (var s in cfg.Services.Values)
        {
            s.LogRoots = s.LogRoots.Select(Abs).ToList();
            if (!string.IsNullOrEmpty(s.Repo)) s.Repo = Abs(s.Repo);
        }
        cfg.Files.AllowedRoots = cfg.Files.AllowedRoots.Select(Abs).ToList();
        if (!string.IsNullOrEmpty(cfg.Deploys.Feed)) cfg.Deploys.Feed = Abs(cfg.Deploys.Feed);
        if (!string.IsNullOrEmpty(cfg.Metrics.SeriesDir)) cfg.Metrics.SeriesDir = Abs(cfg.Metrics.SeriesDir);
        return cfg;
    }
}
