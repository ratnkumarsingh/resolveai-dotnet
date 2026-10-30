using System.IO.Pipelines;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ResolveAI.Contracts;
using ResolveAI.Mcp;

namespace ResolveAI.App.Mcp;

/// <summary>Which system a server covers.</summary>
public sealed record SystemDef(string System, string Server, string Label);

public sealed record ToolSpec(string System, string Server, string Name, string Description, JsonElement InputSchema);

public sealed record HubToolResult(bool Ok, string Summary, List<Dictionary<string, object?>> Rows, bool Truncated, string Source);

/// <summary>What the investigation pipeline needs from the MCP layer; tests pass a hub of in-process servers.</summary>
public interface IHub : IAsyncDisposable
{
    IReadOnlyList<ToolSpec> Tools();
    List<SystemStatus> Status();
    Task<HubToolResult> Call(string tool, IReadOnlyDictionary<string, object?> args, CancellationToken ct = default);
}

public sealed record HubEntry(SystemDef Def, bool Enabled, string? DisabledReason, Func<IClientTransport> Transport);

public sealed partial class McpHub : IHub
{
    private static readonly TimeSpan DefaultCallTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(20);

    private sealed class Connection
    {
        public required SystemDef Def { get; init; }
        public McpClient? Client { get; set; }
        public List<ToolSpec> Tools { get; init; } = [];
        public required string Status { get; set; }
        public string? Detail { get; set; }
        public required string CheckedAt { get; set; }
        public Func<CancellationToken, Task<McpClient>>? Reconnect { get; init; }
        public bool Restarted { get; set; }
    }

    private readonly List<Connection> _conns;
    private readonly TimeSpan _callTimeout;

    private McpHub(List<Connection> conns, TimeSpan callTimeout)
    {
        _conns = conns;
        _callTimeout = callTimeout;
    }

    /// <summary>Settings a server needs. Children get only these, so secrets such as the API key and access code never reach them.</summary>
    private static readonly string[] Forwarded =
    [
        "MSSQL_USER", "MSSQL_PASSWORD", "SystemRoot", "TEMP", "TMP", "LOCALAPPDATA", "ProgramData", "ProgramFiles", "GIT_EXEC_PATH",
        "DOTNET_ROOT", "DOTNET_ROOT(x86)", "HOME",
    ];

    public static Dictionary<string, string?> ServerEnv(Func<string, string?> env)
    {
        var result = new Dictionary<string, string?>();
        foreach (var (key, value) in StdioClientTransportOptions.GetDefaultEnvironmentVariables()) result[key] = value;
        foreach (var key in Forwarded)
        {
            if (env(key) is { Length: > 0 } value) result[key] = value;
        }
        result["RESOLVEAI_MCP_CONFIG"] = env("RESOLVEAI_MCP_CONFIG") is { Length: > 0 } path ? path : McpConfig.ConfigPath();
        return result;
    }

    /// <summary>The stdio transport for one server. Inheritance is off: without this the child would see the whole parent environment.</summary>
    public static StdioClientTransportOptions TransportOptions(string server, Func<string, string?> env) => new()
    {
        Name = server,
        Command = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "ResolveAI.Mcp.exe" : "ResolveAI.Mcp"),
        Arguments = [server],
        WorkingDirectory = McpConfig.RepoRoot(),
        InheritEnvironmentVariables = false,
        EnvironmentVariables = ServerEnv(env),
    };

    /// <summary>
    /// The protocol version is pinned because we ship both ends. Left unset, the client probes for a newer version with a short timeout and
    /// falls back to the older handshake, which a slow-starting server then refuses, so a system would randomly show as not working on a busy machine.
    /// </summary>
    public static McpClientOptions ClientOptions() => new()
    {
        ClientInfo = new Implementation { Name = "resolveai", Version = "0.1.0" },
        ProtocolVersion = "2025-11-25",
    };

    /// <summary>Starts every enabled server as a child process over stdio. A server that fails to start is reported, not fatal.</summary>
    public static Task<McpHub> Start(McpConfig cfg, Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        var e = env;
        return Connect(ServerDefs.All.Select(def => new HubEntry(
            new SystemDef(def.System, def.Server, def.Label), def.Enabled(cfg), def.DisabledReason,
            () => new StdioClientTransport(TransportOptions(def.Server, e)))));
    }

    /// <summary>Connects to servers through any transport (stdio in production, in-memory in tests).</summary>
    public static async Task<McpHub> Connect(IEnumerable<HubEntry> entries, TimeSpan? callTimeout = null)
    {
        var conns = await Task.WhenAll(entries.Select(async e =>
        {
            var at = Js.IsoNow();
            if (!e.Enabled)
            {
                return new Connection { Def = e.Def, Status = "disabled", Detail = e.DisabledReason ?? "Turned off in mcp.config.json.", CheckedAt = at };
            }
            Func<CancellationToken, Task<McpClient>> open = ct => McpClient.CreateAsync(e.Transport(), ClientOptions(), cancellationToken: ct);
            try
            {
                using var startup = new CancellationTokenSource(StartTimeout);
                var client = await open(startup.Token).WaitAsync(startup.Token);
                var tools = await client.ListToolsAsync(cancellationToken: startup.Token);
                // Only read-only tools are ever offered; anything else a server exposes is ignored.
                var readOnly = tools.Where(t => t.ProtocolTool.Annotations?.ReadOnlyHint == true).ToList();
                return new Connection
                {
                    Def = e.Def,
                    Client = client,
                    Reconnect = open,
                    Status = "connected",
                    Detail = readOnly.Count < tools.Count ? $"{tools.Count - readOnly.Count} tool(s) ignored: not marked read-only." : null,
                    CheckedAt = at,
                    Tools = readOnly.Select(t => new ToolSpec(e.Def.System, e.Def.Server, t.Name, t.Description ?? "", t.ProtocolTool.InputSchema)).ToList(),
                };
            }
            catch (OperationCanceledException)
            {
                return new Connection { Def = e.Def, Status = "error", Detail = $"{e.Def.Label} did not start within 20s", CheckedAt = at };
            }
            catch (Exception err)
            {
                return new Connection { Def = e.Def, Status = "error", Detail = err.Message, CheckedAt = at };
            }
        }));
        return new McpHub([.. conns], callTimeout ?? DefaultCallTimeout);
    }

    public IReadOnlyList<ToolSpec> Tools() => _conns.SelectMany(c => c.Tools).ToList();

    public List<SystemStatus> Status() => _conns.Select(c => new SystemStatus
    {
        System = c.Def.System,
        Server = c.Def.Server,
        Label = c.Def.Label,
        Status = c.Status,
        Detail = c.Detail,
        Tools = c.Tools.Select(t => new SystemTool { Name = t.Name, Description = t.Description }).ToList(),
        CheckedAt = c.CheckedAt,
    }).ToList();

    private static HubToolResult ParsePayload(CallToolResult res)
    {
        var text = string.Concat(res.Content.OfType<TextContentBlock>().Select(c => c.Text));
        var ok = res.IsError != true;
        try
        {
            var p = JsonSerializer.Deserialize<ToolPayload>(text, Json.Options) ?? throw new JsonException();
            return new HubToolResult(ok, p.Summary, p.Rows, p.Truncated, p.Source);
        }
        catch (JsonException)
        {
            return new HubToolResult(ok, text.Length > 0 ? Js.Clip(text, 500)[..Math.Min(500, text.Length)] : "(empty result)", [], false, "");
        }
    }

    [GeneratedRegex("closed|not connected|EPIPE", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectionLost();

    private async Task<CallToolResult> CallOnce(McpClient client, string tool, IReadOnlyDictionary<string, object?> args, string timeoutMessage, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_callTimeout);
        var request = new CallToolRequestParams
        {
            Name = tool,
            Arguments = args.ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value, Json.Options)),
        };
        try
        {
            return await client.CallToolAsync(request, timeout.Token).AsTask().WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException(timeoutMessage);
        }
    }

    public async Task<HubToolResult> Call(string tool, IReadOnlyDictionary<string, object?> args, CancellationToken ct = default)
    {
        var conn = _conns.FirstOrDefault(c => c.Tools.Any(t => t.Name == tool));
        if (conn?.Client is null) return new HubToolResult(false, $"No connected system offers the tool \"{tool}\".", [], false, "");
        try
        {
            return ParsePayload(await CallOnce(conn.Client, tool, args, $"{tool} timed out after {Js.Num(_callTimeout.TotalSeconds)}s", ct));
        }
        catch (Exception err) when (err is not OperationCanceledException)
        {
            // A crashed server is restarted once; a second failure leaves it in the error state.
            if ((err is ClientTransportClosedException || ConnectionLost().IsMatch(err.Message)) && conn.Reconnect is not null && !conn.Restarted)
            {
                conn.Restarted = true;
                try
                {
                    using var startup = new CancellationTokenSource(StartTimeout);
                    conn.Client = await conn.Reconnect(startup.Token);
                    return ParsePayload(await CallOnce(conn.Client, tool, args, $"{tool} timed out", ct));
                }
                catch (Exception retryErr) when (retryErr is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    conn.Status = "error";
                    conn.Detail = retryErr.Message;
                    conn.CheckedAt = Js.IsoNow();
                }
            }
            return new HubToolResult(false, $"{tool} failed: {err.Message}", [], false, "");
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var c in _conns)
        {
            if (c.Client is null) continue;
            try
            {
                await c.Client.DisposeAsync();
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
            {
                // Already gone; nothing to clean up.
            }
        }
    }
}
