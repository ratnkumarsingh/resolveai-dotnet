using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ResolveAI.Mcp;

/// <summary>One input field: its JSON schema, and the same rules enforced before a handler runs.</summary>
public sealed record Field(string Name, string Type, string? Description = null, bool Required = true, long? Min = null, long? Max = null, object? Default = null, Regex? Pattern = null, string? PatternText = null)
{
    public const string Iso = "ISO-8601 UTC time, e.g. 2026-10-27T10:00:00Z";

    public static Field Str(string name, string? description = null, bool required = true, int? min = null, int? max = null) =>
        new(name, "string", description, required, min, max);
    public static Field Time(string name, bool required = true) => new(name, "string", Iso, required);
    public static Field Int(string name, long min, long max, long? dflt = null) => new(name, "integer", null, dflt is null, min, max, dflt);
    public static Field Match(string name, Regex pattern, string text) => new(name, "string", Pattern: pattern, PatternText: text);

    public JsonObject ToSchema()
    {
        var o = new JsonObject();
        if (Default is not null) o["default"] = JsonValue.Create(Default);
        o["type"] = Type;
        if (Type == "string")
        {
            if (Min is not null) o["minLength"] = Min;
            if (Max is not null) o["maxLength"] = Max;
            if (PatternText is not null) o["pattern"] = PatternText;
        }
        else
        {
            if (Min is not null) o["minimum"] = Min;
            if (Max is not null) o["maximum"] = Max;
        }
        if (Description is not null) o["description"] = Description;
        return o;
    }

    /// <summary>Returns what is wrong with a supplied value, or null when it is acceptable.</summary>
    public string? Problem(JsonElement v)
    {
        if (Type == "string")
        {
            if (v.ValueKind != JsonValueKind.String) return "must be a string";
            var s = v.GetString()!;
            if (Min is not null && s.Length < Min) return $"must be at least {Min} characters";
            if (Max is not null && s.Length > Max) return $"must be at most {Max} characters";
            if (Pattern is not null && !Pattern.IsMatch(s)) return "is not in the expected format";
            return null;
        }
        if (v.ValueKind != JsonValueKind.Number) return "must be a number";
        var n = v.GetDouble();
        if (Math.Floor(n) != n) return "must be a whole number";
        if (Min is not null && n < Min) return $"must be at least {Min}";
        if (Max is not null && n > Max) return $"must be at most {Max}";
        return null;
    }
}

/// <summary>Validated arguments: only declared fields, with defaults applied.</summary>
public sealed class ToolArgs(Dictionary<string, object> values)
{
    public string Str(string name) => (string)values[name];
    public string? OptStr(string name) => values.GetValueOrDefault(name) as string;
    public int Int(string name) => Convert.ToInt32(values[name]);
}

public delegate Task<CallToolResult> ToolHandler(ToolArgs args, CancellationToken ct);

public sealed record ToolDef(string Name, string Description, Field[] Fields, ToolHandler Handler)
{
    public JsonElement InputSchema()
    {
        var props = new JsonObject();
        foreach (var f in Fields) props[f.Name] = f.ToSchema();
        var schema = new JsonObject { ["type"] = "object", ["properties"] = props };
        var required = Fields.Where(f => f.Required).Select(f => (JsonNode)JsonValue.Create(f.Name)).ToArray();
        if (required.Length > 0) schema["required"] = new JsonArray(required);
        schema["$schema"] = "http://json-schema.org/draft-07/schema#";
        return JsonSerializer.SerializeToElement(schema);
    }
}

/// <summary>
/// A read-only MCP server. Every tool gets the same guard rails: declared input only, a timeout, the read-only
/// annotations, and expected failures (ToolError) returned as an error result instead of a protocol error.
/// </summary>
public sealed class ToolServer(string name, IReadOnlyList<ToolDef> tools)
{
    public string Name => name;
    public IReadOnlyList<ToolDef> Tools => tools;
    public TimeSpan CallTimeout { get; init; } = Limits.CallTimeout;

    public async Task<CallToolResult> CallAsync(string tool, IDictionary<string, JsonElement>? arguments, CancellationToken ct = default)
    {
        var def = tools.FirstOrDefault(t => t.Name == tool);
        if (def is null) return ToolResult.Fail($"Tool {tool} not found");

        var values = new Dictionary<string, object>();
        foreach (var f in def.Fields)
        {
            if (arguments is null || !arguments.TryGetValue(f.Name, out var v) || v.ValueKind == JsonValueKind.Undefined)
            {
                if (f.Required) return ToolResult.Fail($"Invalid arguments for tool {tool}: {f.Name} is required.");
                if (f.Default is not null) values[f.Name] = f.Default;
                continue;
            }
            if (f.Problem(v) is { } problem) return ToolResult.Fail($"Invalid arguments for tool {tool}: {f.Name} {problem}.");
            values[f.Name] = f.Type == "string" ? v.GetString()! : v.GetDouble();
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CallTimeout);
        try
        {
            // WaitAsync stops waiting even when the handler itself ignores cancellation.
            return await def.Handler(new ToolArgs(values), timeout.Token).WaitAsync(timeout.Token);
        }
        catch (ToolError err)
        {
            return ToolResult.Fail(err.Message);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return ToolResult.Fail($"{tool} took longer than {CallTimeout.TotalSeconds:0}s and was stopped.");
        }
        catch (Exception err) when (err is not OperationCanceledException)
        {
            return ToolResult.Fail($"{tool} failed: {err.Message}");
        }
    }

    public McpServerOptions ToOptions() => new()
    {
        ServerInfo = new Implementation { Name = name, Version = "0.1.0" },
        Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
        Handlers = new McpServerHandlers
        {
            ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult
            {
                Tools = tools.Select(t => new Tool
                {
                    Name = t.Name,
                    Description = t.Description,
                    InputSchema = t.InputSchema(),
                    Annotations = new ToolAnnotations { ReadOnlyHint = true, DestructiveHint = false, IdempotentHint = true, OpenWorldHint = false },
                }).ToList(),
            }),
            CallToolHandler = async (request, ct) => await CallAsync(request.Params?.Name ?? "", request.Params?.Arguments, ct),
        },
    };

    /// <summary>Runs over stdio (how ResolveAI, Claude Code and Claude Desktop start it).</summary>
    public async Task ServeStdioAsync(CancellationToken ct = default)
    {
        await using var transport = new StdioServerTransport(name);
        await using var server = McpServer.Create(transport, ToOptions());
        await server.RunAsync(ct);
    }
}

public static class Row
{
    public static Dictionary<string, object?> Of(params (string Key, object? Value)[] pairs)
    {
        var row = new Dictionary<string, object?>(pairs.Length);
        foreach (var (key, value) in pairs) row[key] = value;
        return row;
    }

    /// <summary>A row with a leading column, as in { section: "server", ...row }.</summary>
    public static Dictionary<string, object?> With(string key, object? value, Dictionary<string, object?> rest)
    {
        var row = new Dictionary<string, object?>(rest.Count + 1) { [key] = value };
        foreach (var kv in rest) row[kv.Key] = kv.Value;
        return row;
    }
}
