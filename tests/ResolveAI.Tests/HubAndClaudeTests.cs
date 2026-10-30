using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ResolveAI.App.Llm;
using ResolveAI.App.Mcp;
using ResolveAI.Contracts;
using ResolveAI.Mcp;

namespace ResolveAI.Tests;

public class HubTests
{
    private static readonly SystemDef Def = new("files", "test", "Test");

    private static McpServerOptions Options(IEnumerable<(string Name, bool ReadOnly, Func<CancellationToken, Task<CallToolResult>> Run)> tools)
    {
        var list = tools.ToList();
        return new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "t", Version = "0" },
            Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult
                {
                    Tools = list.Select(t => new Tool
                    {
                        Name = t.Name,
                        InputSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { } }),
                        Annotations = t.ReadOnly ? new ToolAnnotations { ReadOnlyHint = true } : null,
                    }).ToList(),
                }),
                CallToolHandler = async (r, ct) => await list.First(t => t.Name == r.Params!.Name).Run(ct),
            },
        };
    }

    private static CallToolResult Fine() => new() { Content = [new TextContentBlock { Text = """{"summary":"fine","rows":[]}""" }] };

    [Fact]
    public async Task Ignores_tools_not_marked_read_only_and_times_out_a_hung_tool()
    {
        var called = false;
        var options = Options(
        [
            ("safe_check", true, _ => Task.FromResult(Fine())),
            ("delete_everything", false, _ => { called = true; return Task.FromResult(Fine()); }),
            ("hang", true, async ct => { await Task.Delay(Timeout.Infinite, ct); return Fine(); }),
        ]);
        await using var hub = await McpHub.Connect([Hubs.InProcess(Def, options)], TimeSpan.FromMilliseconds(300));
        Assert.Equal(["safe_check", "hang"], hub.Tools().Select(t => t.Name));
        Assert.Equal("1 tool(s) ignored: not marked read-only.", hub.Status()[0].Detail);

        var refused = await hub.Call("delete_everything", new Dictionary<string, object?> { ["path"] = "/" });
        Assert.False(refused.Ok);
        Assert.Equal("No connected system offers the tool \"delete_everything\".", refused.Summary);
        Assert.False(called);

        Assert.Equal("fine", (await hub.Call("safe_check", new Dictionary<string, object?>())).Summary);
        var hung = await hub.Call("hang", new Dictionary<string, object?>());
        Assert.False(hung.Ok);
        Assert.Equal("hang failed: hang timed out after 0.3s", hung.Summary);
    }

    [Fact]
    public async Task A_server_that_fails_to_start_is_reported_not_fatal()
    {
        await using var hub = await McpHub.Connect(
        [
            new HubEntry(Def with { Server = "broken", System = "database" }, true, null, () => new StdioClientTransport(new StdioClientTransportOptions { Command = "this-command-does-not-exist-xyz" })),
            Hubs.InProcess(Def, Options([("safe_check", true, _ => Task.FromResult(Fine()))])),
        ]);
        Assert.Equal(["error", "connected"], hub.Status().Select(s => s.Status));
        Assert.Equal("fine", (await hub.Call("safe_check", new Dictionary<string, object?>())).Summary);
    }

    [Fact]
    public async Task Starts_the_real_servers_as_child_processes()
    {
        var path = Path.Combine(McpFixtures.RepoRoot, "mcp.config.example.json");
        await using var hub = await McpHub.Start(McpConfig.Load(path), k => k == "RESOLVEAI_MCP_CONFIG" ? path : Environment.GetEnvironmentVariable(k));
        // Compared with the details included, so a failure to start says why.
        var statuses = hub.Status().Select(s => s.Status == "connected" || s.Status == "disabled" ? s.Status : $"{s.Status}: {s.Detail}").ToList();
        var joined = string.Join(", ", statuses);
        Assert.True(joined == "disabled, connected, connected, connected", joined);
        Assert.Contains("setup.sql", hub.Status()[0].Detail);
        var r = await hub.Call("files_search", new Dictionary<string, object?> { ["root"] = "payment-api", ["phrase"] = "pool exhausted" });
        Assert.True(r.Ok, r.Summary);
        Assert.Equal("logs/payment-api/app.log", r.Rows[0]["file"]);
        Assert.Equal(5L, r.Rows[0]["line"]);
    }

    /// <summary>An unpinned client races a version probe against the server's startup, and loses on a busy machine.</summary>
    [Fact]
    public void The_client_pins_the_protocol_version_so_no_version_probe_can_race_startup()
    {
        Assert.False(string.IsNullOrEmpty(McpHub.ClientOptions().ProtocolVersion));
        Assert.NotEqual("2026-07-28", McpHub.ClientOptions().ProtocolVersion);
    }

    /// <summary>The servers run from the app's folder, whose libraries come from the shared framework; without this they crash at launch.</summary>
    [Fact]
    public void The_server_executable_is_configured_for_the_framework_it_runs_on()
    {
        var config = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ResolveAI.Mcp.runtimeconfig.json"));
        Assert.Contains("Microsoft.AspNetCore.App", config);
    }

    /// <summary>Runs a real child through the same transport options and reads back what it actually received.</summary>
    [Fact]
    public async Task Child_processes_never_receive_the_API_key_or_access_code()
    {
        const string apiKey = "sk-ant-LEAK-TEST-KEY";
        const string accessCode = "LEAK-TEST-CODE";
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", apiKey);
        Environment.SetEnvironmentVariable("LIVE_ACCESS_CODE", accessCode);
        var dir = Directory.CreateTempSubdirectory("resolveai-env-").FullName;
        var dump = Path.Combine(dir, "env.txt");
        try
        {
            var options = McpHub.TransportOptions("files", Environment.GetEnvironmentVariable);
            Assert.False(options.InheritEnvironmentVariables);
            // A script file, because the transport escapes shell characters in its arguments.
            if (OperatingSystem.IsWindows())
            {
                var script = Path.Combine(dir, "probe.cmd");
                File.WriteAllLines(script, [$"@set > \"{dump}\""]);
                options.Command = script;
                options.Arguments = [];
            }
            else
            {
                var script = Path.Combine(dir, "probe.sh");
                File.WriteAllLines(script, ["#!/bin/sh", $"env > '{dump}'"]);
                File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                options.Command = script;
                options.Arguments = [];
            }
            // The child isn't an MCP server, so the handshake fails; only what it wrote matters.
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await using var client = await McpClient.CreateAsync(new StdioClientTransport(options), cancellationToken: cts.Token);
            }
            catch (Exception e) when (e is not Xunit.Sdk.XunitException)
            {
                // Expected.
            }
            var seen = File.ReadAllText(dump);
            Assert.Contains("RESOLVEAI_MCP_CONFIG", seen);
            Assert.DoesNotContain(apiKey, seen);
            Assert.DoesNotContain(accessCode, seen);
            Assert.DoesNotContain("ANTHROPIC_API_KEY", seen, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("LIVE_ACCESS_CODE", seen, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
            Environment.SetEnvironmentVariable("LIVE_ACCESS_CODE", null);
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void A_SQL_login_is_forwarded_to_child_processes_when_set()
    {
        var env = McpHub.ServerEnv(k => k switch { "MSSQL_USER" => "u", "MSSQL_PASSWORD" => "p", "ANTHROPIC_API_KEY" => "secret", _ => null });
        Assert.Equal("u", env["MSSQL_USER"]);
        Assert.Equal("p", env["MSSQL_PASSWORD"]);
        Assert.False(env.ContainsKey("ANTHROPIC_API_KEY"));
    }
}

/// <summary>Exercises the live provider against a fake Claude API: no network, no cost, but real SDK requests and parsing.</summary>
public class ClaudeProviderTests
{
    private sealed class FakeClaude(params Func<JsonNode, (HttpStatusCode Status, string Body)>[] replies) : HttpMessageHandler
    {
        private int _next;
        public List<JsonNode> Requests { get; } = [];
        public List<HttpRequestMessage> Raw { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            Requests.Add(body);
            Raw.Add(request);
            var (status, reply) = replies[Math.Min(_next++, replies.Length - 1)](body);
            return new HttpResponseMessage(status) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
        }
    }

    private static string Message(string stop, object[] content) => JsonSerializer.Serialize(new
    {
        id = "msg_1", type = "message", role = "assistant", model = "claude-opus-5-5", content, stop_reason = stop, stop_sequence = (string?)null,
        usage = new { input_tokens = 11, output_tokens = 7, cache_read_input_tokens = 3, cache_creation_input_tokens = 0 },
    });

    private static string Text(string stop, string text) => Message(stop, [new { type = "text", text }]);

    private static string ToolUse(string id, string name, object input) =>
        Message("tool_use", [new { type = "tool_use", id, name, input }]);

    private const string ValidOutput = """{"summary":"s","insufficient_evidence":false,"hypotheses":[{"title":"t","explanation":"e","confidence":"high","evidence_ids":["log:1"],"counter_evidence":[],"unknowns":[],"recommended_checks":[]}],"similar_incidents":[],"limitations":[]}""";

    private static EvidencePacket Packet() => new()
    {
        Incident = new PacketIncident { Key = "INC-1", Service = "svc" },
        Items = [new PacketItem { Id = "log:1", Type = "log", Excerpt = "x" }],
    };

    private static ClaudeProvider Provider(FakeClaude fake) => new("test-key", "claude-opus-5-5", new HttpClient(fake));

    [Fact]
    public async Task Sends_the_model_schema_effort_and_fallback_and_parses_the_reply()
    {
        var fake = new FakeClaude(_ => (HttpStatusCode.OK, Text("end_turn", ValidOutput)));
        var result = await Provider(fake).Investigate(Packet(), default);

        Assert.Equal("t", result.Output.Hypotheses[0].Title);
        Assert.Equal("claude-opus-5-5", result.Model);
        Assert.Equal(11, result.Usage!["input_tokens"]);
        Assert.Equal(7, result.Usage["output_tokens"]);
        Assert.Equal(3, result.Usage["cache_read_input_tokens"]);

        var sent = fake.Requests[0];
        Assert.Equal("claude-opus-5-5", sent["model"]!.GetValue<string>());
        Assert.Equal(16000, sent["max_tokens"]!.GetValue<int>());
        Assert.Equal("default", sent["fallbacks"]!.GetValue<string>());
        Assert.Equal("medium", sent["output_config"]!["effort"]!.GetValue<string>());
        Assert.Equal("json_schema", sent["output_config"]!["format"]!["type"]!.GetValue<string>());
        Golden.AssertSame(JsonNode.Parse(Schemas.InvestigationJson), sent["output_config"]!["format"]!["schema"], "schema");
        Assert.Equal(Prompts.InvestigationSystem, sent["system"]!.GetValue<string>());
        Assert.Equal(Prompts.InvestigationUserMessage(Packet()), sent["messages"]![0]!["content"]!.GetValue<string>());
        var headers = string.Join(Environment.NewLine, fake.Raw[0].Headers.Select(h => $"{h.Key}: {string.Join(",", h.Value)}"));
        Assert.Contains("server-side-fallback-2026-07-01", headers);
        // The C# SDK documents ApiKey as being sent this way.
        Assert.Contains("Authorization: Bearer test-key", headers);
    }

    [Fact]
    public async Task A_refusal_is_a_plain_error_and_a_cut_off_or_malformed_reply_can_be_retried()
    {
        var refused = await Assert.ThrowsAsync<LlmError>(() => Provider(new FakeClaude(_ => (HttpStatusCode.OK, Text("refusal", "")))).Investigate(Packet(), default));
        Assert.False(refused.Retryable);
        Assert.Contains("declined", refused.Message);

        var cut = await Assert.ThrowsAsync<LlmError>(() => Provider(new FakeClaude(_ => (HttpStatusCode.OK, Text("max_tokens", "{")))).Investigate(Packet(), default));
        Assert.True(cut.Retryable);

        var malformed = await Assert.ThrowsAsync<LlmError>(() => Provider(new FakeClaude(_ => (HttpStatusCode.OK, Text("end_turn", "not json")))).Investigate(Packet(), default));
        Assert.True(malformed.Retryable);

        var wrongShape = await Assert.ThrowsAsync<LlmError>(() => Provider(new FakeClaude(_ => (HttpStatusCode.OK, Text("end_turn", """{"summary":"only"}""")))).Investigate(Packet(), default));
        Assert.True(wrongShape.Retryable);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "credentials")]
    [InlineData(HttpStatusCode.Forbidden, "credentials")]
    [InlineData(HttpStatusCode.TooManyRequests, "rate limiting")]
    [InlineData(HttpStatusCode.BadRequest, "rejected the request")]
    [InlineData(HttpStatusCode.InternalServerError, "returned an error (500)")]
    public async Task API_errors_become_messages_a_person_can_act_on(HttpStatusCode status, string expected)
    {
        var fake = new FakeClaude(_ => (status, """{"type":"error","error":{"type":"x","message":"boom"}}"""));
        var err = await Assert.ThrowsAsync<LlmError>(() => Provider(fake).Investigate(Packet(), default));
        Assert.Contains(expected, err.Message);
        Assert.False(err.Retryable);
    }

    [Fact]
    public async Task Follow_ups_run_tools_through_our_callback_and_feed_results_back()
    {
        var fake = new FakeClaude(
            _ => (HttpStatusCode.OK, ToolUse("toolu_1", "files_tail", new { path = "a.log", lines = 5 })),
            _ => (HttpStatusCode.OK, ToolUse("toolu_2", "metrics_list", new { service = "svc" })),
            _ => (HttpStatusCode.OK, Text("end_turn", "Checked the log.")));
        var calls = new List<(string Name, Dictionary<string, object?> Args)>();
        var schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { path = new { type = "string" } }, required = new[] { "path" } });
        FollowUpTool[] tools = [new("files_tail", "Tail a file", schema), new("metrics_list", "List metrics", schema)];

        var usage = await Provider(fake).FollowUp(Packet(), tools, (name, args, _) =>
        {
            calls.Add((name, args));
            return Task.FromResult($"[mcp:{calls.Count}] result {calls.Count}");
        }, 8, default);

        Assert.Equal(["files_tail", "metrics_list"], calls.Select(c => c.Name));
        Assert.Equal("a.log", calls[0].Args["path"]);
        Assert.Equal(5L, calls[0].Args["lines"]);
        Assert.Equal(3, fake.Requests.Count);
        Assert.Equal(33, usage!["follow_up_input_tokens"]);
        Assert.Equal(3, usage["follow_up_turns"]);

        var first = fake.Requests[0];
        Assert.Equal(4000, first["max_tokens"]!.GetValue<int>());
        Assert.Equal("low", first["output_config"]!["effort"]!.GetValue<string>());
        Assert.Equal(Prompts.FollowUpSystem, first["system"]!.GetValue<string>());
        Assert.Equal(["files_tail", "metrics_list"], first["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()));
        Assert.Equal("Tail a file", first["tools"]![0]!["description"]!.GetValue<string>());
        Assert.Equal("string", first["tools"]![0]!["input_schema"]!["properties"]!["path"]!["type"]!.GetValue<string>());

        // The tool result reaches the model with its evidence ID, and the full history is resent each turn.
        var second = fake.Requests[1]["messages"]!.AsArray();
        Assert.Equal(3, second.Count);
        Assert.Contains("[mcp:1] result 1", second[2]!.ToJsonString());
        Assert.Equal("toolu_1", second[2]!["content"]![0]!["tool_use_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Follow_ups_stop_at_the_iteration_limit_even_if_the_model_keeps_asking()
    {
        var turns = 0;
        var fake = new FakeClaude(_ =>
        {
            turns++;
            return (HttpStatusCode.OK, ToolUse($"toolu_{turns}", "files_tail", new { path = "a.log" }));
        });
        var schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { } });
        var usage = await Provider(fake).FollowUp(Packet(), [new FollowUpTool("files_tail", "d", schema)], (_, _, _) => Task.FromResult("ok"), 3, default);
        Assert.Equal(4, turns);
        Assert.Equal(4, usage!["follow_up_turns"]);
    }

    [Fact]
    public async Task A_failed_tool_call_in_a_follow_up_reports_an_error_to_the_model()
    {
        var fake = new FakeClaude(_ => (HttpStatusCode.Unauthorized, """{"type":"error","error":{"type":"x","message":"no"}}"""));
        var schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { } });
        var err = await Assert.ThrowsAsync<LlmError>(() => Provider(fake).FollowUp(Packet(), [new FollowUpTool("files_tail", "d", schema)], (_, _, _) => Task.FromResult("ok"), 3, default));
        Assert.Contains("credentials", err.Message);
    }
}
