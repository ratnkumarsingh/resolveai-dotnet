using System.Globalization;
using ResolveAI.Mcp;

namespace ResolveAI.App;

public sealed class AppSettings
{
    public string? ApiKey { get; init; }
    /// <summary>Default mode for runs that don't ask for live.</summary>
    public string LlmMode { get; init; } = "recorded";
    public string Model { get; init; } = "claude-opus-5-5";
    /// <summary>With an API key set, a run can opt into live Claude by sending this code.</summary>
    public string? LiveAccessCode { get; init; }
    public int LiveDailyLimit { get; init; } = 20;
    public bool DemoMode { get; init; } = true;
    public bool DemoResetAnyIp { get; init; }
    public string DbPath { get; init; } = ":memory:";
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 8787;
    /// <summary>Start the MCP servers (database, files, deploys, metrics) and check them during investigations.</summary>
    public bool McpEnabled { get; init; } = true;
    /// <summary>Most extra tool calls the model may make per investigation.</summary>
    public int MaxFollowUps { get; init; } = 8;

    public static AppSettings Load(Func<string, string?>? environment = null)
    {
        var env = environment ?? Environment.GetEnvironmentVariable;
        string? Trimmed(string key) => env(key)?.Trim() is { Length: > 0 } v ? v : null;

        var apiKey = Trimmed("ANTHROPIC_API_KEY");
        var dbPath = Trimmed("DB_PATH");
        return new AppSettings
        {
            ApiKey = apiKey,
            // Live mode needs a key; without one we fall back to the deterministic recorded mode.
            LlmMode = env("LLM_MODE") == "live" && apiKey is not null ? "live" : "recorded",
            Model = Trimmed("CLAUDE_MODEL") ?? "claude-opus-5-5",
            LiveAccessCode = Trimmed("LIVE_ACCESS_CODE"),
            LiveDailyLimit = int.TryParse(Trimmed("LIVE_DAILY_LIMIT"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit) && limit >= 0 ? limit : 20,
            DemoMode = (env("DEMO_MODE") ?? "true") == "true",
            DemoResetAnyIp = env("DEMO_RESET_ANY_IP") == "true",
            DbPath = dbPath == ":memory:" ? ":memory:" : Path.GetFullPath(dbPath ?? "data/resolveai.db", McpConfig.RepoRoot()),
            Host = Trimmed("HOST") ?? "127.0.0.1",
            Port = int.TryParse(env("API_PORT"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port > 0 ? port : 8787,
            McpEnabled = (env("MCP_ENABLED") ?? "true") == "true",
            MaxFollowUps = int.TryParse(Trimmed("MCP_MAX_FOLLOWUP_CALLS"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var followUps) ? Math.Clamp(followUps, 0, 20) : 8,
        };
    }

    /// <summary>Reads KEY=value lines into the environment. A variable that is already set wins, so the real environment can override the file.</summary>
    public static void LoadDotEnv(string path)
    {
        if (!File.Exists(path)) return;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && (value[0] is '"' or '\'') && value[^1] == value[0]) value = value[1..^1];
            if (Environment.GetEnvironmentVariable(key) is null) Environment.SetEnvironmentVariable(key, value);
        }
    }
}
