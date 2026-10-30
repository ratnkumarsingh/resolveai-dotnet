using ResolveAI.Mcp;

// One read-only MCP server per system: ResolveAI.Mcp <sqlserver|files|deploys|metrics>
if (args.Length != 1 || ServerDefs.Find(args[0]) is not { } def)
{
    await Console.Error.WriteLineAsync($"Usage: ResolveAI.Mcp <{string.Join('|', ServerDefs.All.Select(d => d.Server))}>");
    return 2;
}

await def.Build(McpConfig.Load()).ServeStdioAsync();
return 0;

namespace ResolveAI.Mcp
{
    /// <summary>One MCP server per system.</summary>
    public sealed record ServerDef(string System, string Server, string Label, Func<McpConfig, bool> Enabled, string DisabledReason, Func<McpConfig, ToolServer> Build);

    public static class ServerDefs
    {
        public static readonly IReadOnlyList<ServerDef> All =
        [
            new("database", "sqlserver", "SQL Server", c => c.Sqlserver.Enabled,
                "Turned off. Run demo-systems/sqlserver/setup.sql, then set sqlserver.enabled in mcp.config.json.", c => SqlServerServer.Build(c)),
            new("files", "files", "Files & logs", c => c.Files.AllowedRoots.Count > 0,
                "No folders allowed (files.allowed_roots is empty).", FilesServer.Build),
            new("deploys", "deploys", "Deploys & Git", c => !string.IsNullOrEmpty(c.Deploys.Feed) || c.Services.Values.Any(s => !string.IsNullOrEmpty(s.Repo)),
                "No deploy feed or git repo configured.", DeploysServer.Build),
            new("metrics", "metrics", "Metrics & host", _ => true, "", MetricsServer.Build),
        ];

        public static ServerDef? Find(string server) => All.FirstOrDefault(d => d.Server == server);
    }
}
