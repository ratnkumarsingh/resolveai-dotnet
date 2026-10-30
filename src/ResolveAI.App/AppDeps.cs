using ResolveAI.App.Data;
using ResolveAI.App.Llm;
using ResolveAI.App.Mcp;
using ResolveAI.App.Services;
using ResolveAI.Mcp;

namespace ResolveAI.App;

/// <summary>Everything the app runs on, built once at startup. Tests supply their own.</summary>
public sealed record AppDeps(Db Db, Providers Providers, Systems? Systems, AppSettings Settings)
{
    public static async Task<AppDeps> Create(AppSettings settings)
    {
        var db = new Db(settings.DbPath);

        // First run: start with the demo dataset so the app is never empty.
        if (db.Get<int>("SELECT COUNT(*) FROM incidents") == 0) Seed.Demo(db);

        var providers = new Providers(new RecordedProvider(), settings.ApiKey is not null ? new ClaudeProvider(settings.ApiKey, settings.Model) : null);

        // One MCP server per system. A system that fails to start is shown as an error on the Systems page; it never stops the app.
        Systems? systems = null;
        if (settings.McpEnabled)
        {
            try
            {
                var config = McpConfig.Load();
                systems = new Systems(await McpHub.Start(config), config, settings.MaxFollowUps);
                foreach (var s in systems.Hub.Status()) Console.WriteLine($"  system {s.Label}: {s.Status}{(s.Detail is not null ? $" ({s.Detail})" : "")}");
            }
            catch (Exception err) when (err is not OutOfMemoryException)
            {
                Console.Error.WriteLine($"MCP systems are unavailable: {err.Message}");
            }
        }

        // Investigations interrupted by a restart can't resume; mark them failed so the UI isn't stuck polling.
        db.Run("UPDATE investigations SET status = 'failed', stage = NULL, error_message = 'Interrupted by a server restart. Run the investigation again.' WHERE status IN ('queued', 'running')");

        return new AppDeps(db, providers, systems, settings);
    }
}
