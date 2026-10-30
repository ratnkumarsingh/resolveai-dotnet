using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ResolveAI.Contracts;
using ResolveAI.Mcp;

namespace ResolveAI.Tests;

public static class McpFixtures
{
    public static string RepoRoot => McpConfig.RepoRoot();
    public static McpConfig Example() => McpConfig.Load(Path.Combine(RepoRoot, "mcp.config.example.json"));

    public static McpConfig ExampleWithSql()
    {
        var cfg = Example();
        cfg.Services["order-api"].Database = "ResolveAIDemo";
        return cfg;
    }

    private static Dictionary<string, object?> R(params (string, object?)[] pairs) => Row.Of(pairs);
    private static DateTime D(string iso) => Js.ParseDate(iso).UtcDateTime;

    /// <summary>The same canned SQL Server the golden files were recorded against.</summary>
    public static readonly SqlQuery FakeSql = (sql, _, _) =>
    {
        List<Dictionary<string, object?>> rows = sql switch
        {
            SqlText.Blocked => [R(("session_id", 72), ("blocking_session_id", 61), ("database_name", "ResolveAIDemo"), ("wait_type", "LCK_M_U"), ("wait_ms", 42000), ("wait_resource", "OBJECT: 5:1:0"), ("status", "suspended"), ("command", "UPDATE"), ("query_text", "UPDATE dbo.Orders SET Status = N'placed' WHERE OrderId = 1"), ("program_name", "SQLCMD"))],
            SqlText.HeadBlockers => [R(("session_id", 61), ("status", "sleeping"), ("open_transaction_count", 1), ("last_request_end_time", D("2026-10-27T17:01:00Z")), ("program_name", "SQLCMD"), ("last_query_text", "UPDATE dbo.Orders SET ReconciledAt = SYSUTCDATETIME() WHERE Status = N'open'"))],
            SqlText.SessionsByDatabase => [R(("database_name", "ResolveAIDemo"), ("sessions", 3), ("active_requests", 2), ("blocked_requests", 1))],
            SqlText.SysInfo => [R(("cpu_count", 8), ("physical_memory_mb", 16000), ("committed_mb", 900), ("target_mb", 12000), ("sqlserver_start_time", D("2026-10-20T00:00:00Z")), ("version", "16.0.1210.3"), ("edition", "Developer Edition"))],
            SqlText.LongRequests => [R(("session_id", 72), ("database_name", "ResolveAIDemo"), ("status", "suspended"), ("command", "UPDATE"), ("elapsed_s", 42), ("cpu_ms", 1), ("logical_reads", 3), ("wait_type", "LCK_M_U"), ("query_text", "UPDATE dbo.Orders"), ("program_name", "SQLCMD"))],
            SqlText.Deadlocks => [R(("time", D("2026-10-27T17:03:00Z")), ("process", "p1"), ("spid", 72), ("database_name", "ResolveAIDemo"), ("wait_resource", "KEY: 5:1"), ("lock_mode", "U"), ("query_text", "UPDATE dbo.Orders"), ("victim", 1))],
            SqlText.SystemHealthErrors => [R(("time", D("2026-10-27T17:01:30Z")), ("error_number", 17883), ("severity", 16), ("message", "Scheduler issue"), ("captured_by", "system_health"))],
            SqlText.ResolveaiSessionErrors => [R(("time", D("2026-10-27T17:02:10Z")), ("error_number", 1222), ("severity", 16), ("message", "Lock request time out period exceeded."), ("database_name", "ResolveAIDemo"), ("captured_by", "resolveai_errors"))],
            SqlText.DatabaseState => [R(("database_name", "ResolveAIDemo"), ("state_desc", "ONLINE"), ("recovery_model_desc", "SIMPLE"), ("log_reuse_wait_desc", "NOTHING"), ("is_read_only", false))],
            SqlText.AgentStatus => [R(("status_desc", "Stopped"))],
            SqlText.FailedJobs => [R(("job_name", "Nightly"), ("step_id", 1), ("step_name", "Run"), ("run_at", D("2026-10-27T17:00:00Z")), ("message", "Failed"))],
            _ when sql == SqlText.TopWaits => [R(("wait_type", "LCK_M_U"), ("waiting_tasks_count", 4), ("wait_time_ms", 90000))],
            _ when sql.StartsWith("EXEC ") => [R(("file_name", "ResolveAIDemo"), ("type_desc", "ROWS"), ("size_mb", 8m), ("used_mb", 7.5m), ("max_mb", 8m), ("growth", "10 MB"))],
            _ => [],
        };
        return Task.FromResult(rows);
    };

    public static ToolServer Server(string name) => name switch
    {
        "sqlserver" => SqlServerServer.Build(ExampleWithSql(), FakeSql),
        "files" => FilesServer.Build(Example()),
        "deploys" => DeploysServer.Build(Example()),
        "metrics" => MetricsServer.Build(Example()),
        _ => throw new ArgumentException(name),
    };

    public static Dictionary<string, JsonElement> Args(object? args) =>
        args is null ? [] : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(args))!;

    public static async Task<(bool IsError, ToolPayload Payload)> Call(ToolServer server, string tool, object? args = null)
    {
        var res = await server.CallAsync(tool, Args(args));
        var text = string.Concat(res.Content.OfType<TextContentBlock>().Select(c => c.Text));
        return (res.IsError == true, JsonSerializer.Deserialize<ToolPayload>(text, Json.Options)!);
    }
}

public class McpGoldenTests
{
    [Theory]
    [InlineData("sqlserver")]
    [InlineData("files")]
    [InlineData("deploys")]
    [InlineData("metrics")]
    public async Task Tool_schemas_descriptions_and_annotations_match_the_Node_servers(string server)
    {
        var expected = Golden.Load("tools-list")[server]!.AsArray();
        var actual = (await McpFixtures.Server(server).ToOptions().Handlers.ListToolsHandler!(null!, default)).Tools;
        Assert.Equal(expected.Select(t => t!["name"]!.GetValue<string>()), actual.Select(t => t.Name));
        foreach (var (e, a) in expected.Zip(actual))
        {
            var name = a.Name;
            Assert.Equal(e!["description"]!.GetValue<string>(), a.Description);
            Golden.AssertSame(e["inputSchema"], JsonNode.Parse(a.InputSchema.GetRawText()), $"{server}.{name}.inputSchema");
            Golden.AssertSame(e["annotations"], new JsonObject
            {
                ["readOnlyHint"] = a.Annotations!.ReadOnlyHint,
                ["destructiveHint"] = a.Annotations.DestructiveHint,
                ["idempotentHint"] = a.Annotations.IdempotentHint,
                ["openWorldHint"] = a.Annotations.OpenWorldHint,
            }, $"{server}.{name}.annotations");
        }
    }

    public static IEnumerable<object[]> ToolCases() =>
        Enumerable.Range(0, Golden.Load("tool-results").AsArray().Count).Select(i => new object[] { i });

    [Theory]
    [MemberData(nameof(ToolCases))]
    public async Task Tool_results_match_the_Node_servers(int index)
    {
        var c = Golden.Load("tool-results").AsArray()[index]!;
        var server = McpFixtures.Server(c["server"]!.GetValue<string>());
        var tool = c["tool"]!.GetValue<string>();
        var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(c["args"]!.ToJsonString());
        var res = await server.CallAsync(tool, args);
        var text = string.Concat(res.Content.OfType<TextContentBlock>().Select(x => x.Text));
        var outcome = c["outcome"]!;
        var label = $"{tool} {c["args"]!.ToJsonString()}";
        if (outcome["rejected"] is not null)
        {
            Assert.True(res.IsError, $"{label} should be rejected: {text}");
            return;
        }
        Assert.Equal(outcome["is_error"]!.GetValue<bool>(), res.IsError == true);
        Golden.AssertSame(outcome["payload"], Golden.Normalise(JsonNode.Parse(text)), label);
    }
}

/// <summary>The files server must not read outside its allowed folders, whatever it is asked.</summary>
public sealed class FilesAttackTests : IDisposable
{
    private readonly string _base;
    private readonly string _allowed;
    private readonly ToolServer _server;

    public FilesAttackTests()
    {
        _base = Directory.CreateTempSubdirectory("resolveai-files-").FullName;
        _allowed = Path.Combine(_base, "logs");
        Directory.CreateDirectory(Path.Combine(_allowed, "svc"));
        Directory.CreateDirectory(Path.Combine(_base, "secret"));
        Directory.CreateDirectory(Path.Combine(_base, "logs-evil"));
        File.WriteAllText(Path.Combine(_base, "secret", "passwords.txt"), "ERROR root password hunter2\n");
        File.WriteAllText(Path.Combine(_base, "logs-evil", "x.log"), "ERROR sibling secret\n");
        File.WriteAllText(Path.Combine(_allowed, "svc", "app.log"), string.Join("\n",
            "2026-10-27T10:00:00Z INFO started",
            "2026-10-27T10:04:00Z ERROR EACCES: permission denied, open '/mnt/a.pdf'",
            "2026-10-27T12:00:00Z ERROR disk quota exceeded",
            "no timestamp ERROR here"));
        File.WriteAllBytes(Path.Combine(_allowed, "svc", "blob.bin"), [0, 1, 2, 69, 82, 82, 79, 82]);
        // A directory junction that points outside the allowed root (a symlink where junctions don't exist).
        MakeDirectoryLink(Path.Combine(_allowed, "escape"), Path.Combine(_base, "secret"));
        _server = FilesServer.Build(new McpConfig { Files = new FilesConfig { AllowedRoots = [_allowed] } });
    }

    private static void MakeDirectoryLink(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            using var p = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target]) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
            p.WaitForExit();
            Assert.Equal(0, p.ExitCode);
        }
        else
        {
            Directory.CreateSymbolicLink(link, target);
        }
    }

    public void Dispose()
    {
        // The junction goes first, so deleting recursively can't follow it.
        var escape = new DirectoryInfo(Path.Combine(_allowed, "escape"));
        if (escape.Exists) escape.Delete();
        Directory.Delete(_base, recursive: true);
    }

    private async Task AssertOutside(string tool, object args)
    {
        var (isError, payload) = await McpFixtures.Call(_server, tool, args);
        Assert.True(isError, $"{tool} {JsonSerializer.Serialize(args)} was allowed: {payload.Summary}");
        Assert.DoesNotContain("hunter2", JsonSerializer.Serialize(payload.Rows));
    }

    [Fact]
    public async Task Refuses_absolute_paths_dot_dot_and_junctions()
    {
        foreach (var root in new[] { Path.Combine(_base, "secret"), "../secret", "..", Path.Combine(_allowed, "..", "secret"), Path.Combine(_allowed, "escape"), "escape", "svc/../../secret" })
        {
            var (isError, payload) = await McpFixtures.Call(_server, "files_search", new { root, phrase = "password" });
            Assert.True(isError, root);
            Assert.Contains("outside the allowed folders", payload.Summary);
        }
        await AssertOutside("files_tail", new { path = Path.Combine(_allowed, "escape", "passwords.txt") });
        await AssertOutside("files_tail", new { path = "escape/passwords.txt" });
        await AssertOutside("files_check", new { path = "escape/passwords.txt" });
        await AssertOutside("files_recent_changes", new { root = "escape", since = "2000-01-01T00:00:00Z" });
        await AssertOutside("files_disk_space", new { root = "escape" });
    }

    [Fact]
    public async Task Refuses_a_sibling_folder_that_only_shares_the_prefix()
    {
        foreach (var root in new[] { Path.Combine(_base, "logs-evil"), "../logs-evil" })
        {
            var (isError, payload) = await McpFixtures.Call(_server, "files_search", new { root, phrase = "secret" });
            Assert.True(isError, root);
            Assert.Contains("outside the allowed folders", payload.Summary);
        }
    }

    [Fact]
    public async Task A_link_inside_a_deeper_segment_cannot_escape_either()
    {
        var nested = Path.Combine(_allowed, "svc", "deep");
        MakeDirectoryLink(nested, Path.Combine(_base, "secret"));
        try
        {
            await AssertOutside("files_tail", new { path = "svc/deep/passwords.txt" });
            await AssertOutside("files_tail", new { path = "svc/deep/missing-file.txt" });
        }
        finally
        {
            new DirectoryInfo(nested).Delete();
        }
    }

    [Fact]
    public async Task A_search_never_walks_through_a_link()
    {
        var (isError, payload) = await McpFixtures.Call(_server, "files_search", new { root = _allowed, phrase = "hunter2" });
        Assert.False(isError);
        Assert.Empty(payload.Rows);
        Assert.StartsWith("No lines", payload.Summary);
    }

    [Fact]
    public async Task Windows_only_forms_are_refused()
    {
        if (!OperatingSystem.IsWindows()) return;
        await AssertOutside("files_tail", new { path = $@"\\?\{Path.Combine(_base, "secret", "passwords.txt")}" });
        await AssertOutside("files_tail", new { path = $@"\\.\{Path.Combine(_base, "secret", "passwords.txt")}" });
        await AssertOutside("files_tail", new { path = "svc/app.log:hidden" });
        await AssertOutside("files_tail", new { path = "svc/app.log::$DATA" });
    }

    [Fact]
    public async Task A_wrong_case_path_is_still_confined_correctly()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (isError, payload) = await McpFixtures.Call(_server, "files_tail", new { path = Path.Combine(_allowed.ToUpperInvariant(), "svc", "app.log") });
        Assert.False(isError, payload.Summary);
        await AssertOutside("files_tail", new { path = Path.Combine(_base.ToUpperInvariant(), "SECRET", "PASSWORDS.TXT") });
    }

    [Fact]
    public async Task Finds_lines_filters_by_time_skips_binary_and_caps_matches()
    {
        var all = await McpFixtures.Call(_server, "files_search", new { root = _allowed, phrase = "error" });
        Assert.False(all.IsError);
        Assert.Equal(["2", "3", "4"], all.Payload.Rows.Select(r => Js.Str(r["line"])));
        Assert.Equal("logs/svc/app.log", all.Payload.Rows[0]["file"]);

        var windowed = await McpFixtures.Call(_server, "files_search", new { root = "svc", phrase = "ERROR", from = "2026-10-27T09:30:00Z", to = "2026-10-27T11:00:00Z" });
        Assert.Single(windowed.Payload.Rows);

        var capped = await McpFixtures.Call(_server, "files_search", new { root = _allowed, phrase = "error", max_matches = 1 });
        Assert.Single(capped.Payload.Rows);
        Assert.True(capped.Payload.Truncated);
        Assert.Contains("Not a text file", (await McpFixtures.Call(_server, "files_tail", new { path = "svc/blob.bin" })).Payload.Summary);
    }

    [Fact]
    public async Task Tails_checks_and_reports_space_without_changing_anything()
    {
        var before = File.GetLastWriteTimeUtc(Path.Combine(_allowed, "svc", "app.log"));
        var tail = await McpFixtures.Call(_server, "files_tail", new { path = "svc/app.log", lines = 2 });
        Assert.Equal(["3", "4"], tail.Payload.Rows.Select(r => Js.Str(r["line"])));

        var check = await McpFixtures.Call(_server, "files_check", new { path = "svc/app.log" });
        Assert.Equal(true, check.Payload.Rows[0]["exists"]);
        Assert.Equal(true, check.Payload.Rows[0]["readable"]);
        Assert.Equal(false, (await McpFixtures.Call(_server, "files_check", new { path = "svc/missing.log" })).Payload.Rows[0]["exists"]);

        var space = await McpFixtures.Call(_server, "files_disk_space", new { root = _allowed });
        Assert.True(Convert.ToDouble(space.Payload.Rows[0]["total_gb"]) > 0);
        var recent = await McpFixtures.Call(_server, "files_recent_changes", new { root = _allowed, since = "2000-01-01T00:00:00Z" });
        Assert.Contains("logs/svc/app.log", recent.Payload.Rows.Select(r => r["file"]));
        Assert.Equal(before, File.GetLastWriteTimeUtc(Path.Combine(_allowed, "svc", "app.log")));
    }

    [Fact]
    public async Task A_read_only_file_is_reported_without_being_touched()
    {
        var file = Path.Combine(_allowed, "svc", "locked.log");
        File.WriteAllText(file, "x");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead);
        try
        {
            var r = await McpFixtures.Call(_server, "files_check", new { path = "svc/locked.log" });
            // Root ignores file modes on Linux, so only assert this where the check can actually fail.
            if (OperatingSystem.IsWindows() || Environment.UserName != "root")
            {
                Assert.Equal(false, r.Payload.Rows[0]["writable"]);
                Assert.Contains("not writable", r.Payload.Summary);
            }
        }
        finally
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
    }
}

public sealed class DeploysAndMetricsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("resolveai-deploys-").FullName;
    private readonly string _commit;

    private string Repo => Path.Combine(_dir, "repo");

    public DeploysAndMetricsTests()
    {
        Directory.CreateDirectory(Repo);
        Git(["init", "-q"]);
        File.WriteAllText(Path.Combine(Repo, "pool.ts"), "export const max = 100;\n");
        Git(["add", "."]);
        Git(["commit", "-qm", "Initial pool config"], "2026-10-26T09:00:00Z");
        File.WriteAllText(Path.Combine(Repo, "pool.ts"), "export const max = 20;\n");
        Git(["commit", "-qam", "Lower pool max"], "2026-10-27T09:55:00Z");
        _commit = Git(["rev-parse", "--short", "HEAD"]).Trim();
        Git(["tag", "v2.8.1"]);

        File.WriteAllText(Path.Combine(_dir, "feed.json"), """
            [
              { "service": "payment-api", "version": "v2.8.1", "deployed_at": "2026-10-27T10:00:00Z", "change_summary": "Pool settings moved", "commit": "7d03b1f",
                "files_changed": [{ "path": "db-client.yaml", "added": 4, "removed": 0 }], "patch": "+pool:\n+  max: 20\n" },
              { "service": "order-api", "version": "v4.2.0", "deployed_at": "2026-10-27T13:30:00Z", "change_summary": "Hourly reconciliation", "commit": "c90e4a7" }
            ]
            """);
        Directory.CreateDirectory(Path.Combine(_dir, "metrics"));
        File.WriteAllText(Path.Combine(_dir, "metrics", "payment-api.json"), """
            { "HTTP 500 rate": { "unit": "%", "points": [["2026-10-27T10:00:00Z", 0.2], ["2026-10-27T10:05:00Z", 28], ["2026-10-27T10:10:00Z", 27]] } }
            """);
    }

    private string Git(string[] args, string? date = null)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = Repo, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) start.ArgumentList.Add(a);
        start.Environment["GIT_AUTHOR_NAME"] = start.Environment["GIT_COMMITTER_NAME"] = "dev";
        start.Environment["GIT_AUTHOR_EMAIL"] = start.Environment["GIT_COMMITTER_EMAIL"] = "dev@example.test";
        if (date is not null) start.Environment["GIT_AUTHOR_DATE"] = start.Environment["GIT_COMMITTER_DATE"] = date;
        using var p = Process.Start(start)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
        return output;
    }

    public void Dispose()
    {
        // Git marks object files read-only, which blocks a recursive delete on Windows.
        foreach (var f in new DirectoryInfo(_dir).EnumerateFiles("*", SearchOption.AllDirectories)) f.IsReadOnly = false;
        Directory.Delete(_dir, recursive: true);
    }

    private ToolServer Deploys() => DeploysServer.Build(new McpConfig
    {
        Services = new() { ["payment-api"] = new ServiceConfig { Repo = Repo }, ["order-api"] = new ServiceConfig() },
        Deploys = new DeploysConfig { Feed = Path.Combine(_dir, "feed.json") },
    });

    [Fact]
    public async Task Merges_the_feed_with_git_commits_newest_first()
    {
        var r = await McpFixtures.Call(Deploys(), "deploys_recent", new { service = "payment-api", from = "2026-10-27T08:00:00Z", to = "2026-10-27T11:00:00Z" });
        Assert.Equal(["deployment", "commit"], r.Payload.Rows.Select(x => x["kind"]));
        Assert.Equal("2026-10-27T09:55:00.000Z", r.Payload.Rows[1]["time"]);
        Assert.Equal("Lower pool max", r.Payload.Rows[1]["summary"]);
        Assert.Equal("v2.8.1", r.Payload.Rows[1]["version"]);
    }

    [Fact]
    public async Task Summarises_a_change_from_the_feed_or_from_git()
    {
        var feed = await McpFixtures.Call(Deploys(), "deploys_change_summary", new { service = "payment-api", commit = "7d03b1f" });
        Assert.Equal("db-client.yaml", feed.Payload.Rows[0]["path"]);
        var git = await McpFixtures.Call(Deploys(), "deploys_change_summary", new { service = "payment-api", commit = _commit });
        Assert.False(git.IsError, git.Payload.Summary);
        Assert.Equal("pool.ts", git.Payload.Rows[0]["path"]);
        Assert.Equal(1L, git.Payload.Rows[0]["added"]);
        Assert.Contains("+export const max = 20;", Js.Str(git.Payload.Rows[^1]["patch"]));
    }

    [Theory]
    [InlineData("--output=/tmp/x")]
    [InlineData("--upload-pack=evil")]
    [InlineData("abc123; rm -rf /")]
    [InlineData("$(whoami)")]
    [InlineData("HEAD")]
    [InlineData("abc123\n")]
    [InlineData("abc")]
    public async Task A_commit_value_that_is_not_a_hex_id_never_reaches_git(string commit)
    {
        var r = await McpFixtures.Call(Deploys(), "deploys_change_summary", new { service = "payment-api", commit });
        Assert.True(r.IsError, commit);
        Assert.Contains("Invalid arguments", r.Payload.Summary);
    }

    [Fact]
    public async Task Rejects_unknown_services_and_long_windows()
    {
        Assert.Contains("Unknown service", (await McpFixtures.Call(Deploys(), "deploys_recent", new { service = "nope", from = "2026-10-27T08:00:00Z", to = "2026-10-27T09:00:00Z" })).Payload.Summary);
        Assert.Contains("24 hours", (await McpFixtures.Call(Deploys(), "deploys_recent", new { service = "payment-api", from = "2026-10-20T00:00:00Z", to = "2026-10-27T00:00:00Z" })).Payload.Summary);
    }

    [Theory]
    [InlineData("../etc")]
    [InlineData("payment-api\n")]
    [InlineData("payment-api/../x")]
    [InlineData("..")]
    public async Task A_service_name_cannot_become_a_path(string service)
    {
        var server = MetricsServer.Build(new McpConfig { Metrics = new MetricsConfig { SeriesDir = Path.Combine(_dir, "metrics") } });
        var r = await McpFixtures.Call(server, "metrics_list", new { service });
        Assert.True(r.IsError);
        Assert.Contains("plain service name", r.Payload.Summary);
    }

    [Fact]
    public async Task Reports_a_metric_window_and_the_host()
    {
        var server = MetricsServer.Build(new McpConfig { Metrics = new MetricsConfig { SeriesDir = Path.Combine(_dir, "metrics") } });
        var r = await McpFixtures.Call(server, "metrics_series", new { service = "payment-api", name = "http 500 rate", from = "2026-10-27T09:59:00Z", to = "2026-10-27T10:20:00Z" });
        Assert.Equal("payment-api HTTP 500 rate: baseline 0.2 %, peak 28 % at 2026-10-27T10:05:00Z, average 18.4 % over 3 point(s).", r.Payload.Summary);
        var host = await McpFixtures.Call(server, "metrics_host");
        Assert.True(Convert.ToDouble(host.Payload.Rows[0]["cpu_cores"]) > 0);
        Assert.True(Convert.ToDouble(host.Payload.Rows[0]["memory_total_gb"]) > 0);
    }
}

public class ToolGuardRailTests
{
    [Fact]
    public async Task A_hung_tool_is_stopped_at_the_timeout_even_if_it_ignores_cancellation()
    {
        var server = new ToolServer("t", [new ToolDef("hang", "x", [], async (_, _) => { await Task.Delay(Timeout.Infinite, CancellationToken.None); throw new InvalidOperationException(); })]) { CallTimeout = TimeSpan.FromMilliseconds(100) };
        var r = await McpFixtures.Call(server, "hang");
        Assert.True(r.IsError);
        Assert.Contains("was stopped", r.Payload.Summary);
    }

    [Fact]
    public async Task Output_is_capped_by_rows_and_by_size()
    {
        var many = Enumerable.Range(0, 500).Select(i => Row.Of(("n", i)));
        var server = new ToolServer("t", [new ToolDef("big", "x", [], (_, _) => Task.FromResult(ToolResult.Ok("s", many, "src")))]);
        var rows = await McpFixtures.Call(server, "big");
        Assert.Equal(Limits.MaxRows, rows.Payload.Rows.Count);
        Assert.True(rows.Payload.Truncated);

        var huge = Enumerable.Range(0, 100).Select(_ => Row.Of(("text", new string('x', 2000))));
        var wide = new ToolServer("t", [new ToolDef("wide", "x", [], (_, _) => Task.FromResult(ToolResult.Ok("s", huge, "src")))]);
        var res = await wide.CallAsync("wide", null);
        var text = string.Concat(res.Content.OfType<TextContentBlock>().Select(c => c.Text));
        Assert.True(text.Length <= Limits.MaxOutputChars);
        Assert.Contains("\"truncated\":true", text);
    }

    [Fact]
    public async Task Every_tool_is_marked_read_only()
    {
        foreach (var name in new[] { "sqlserver", "files", "deploys", "metrics" })
        {
            var list = await McpFixtures.Server(name).ToOptions().Handlers.ListToolsHandler!(null!, default);
            Assert.All(list.Tools, t =>
            {
                Assert.True(t.Annotations?.ReadOnlyHint);
                Assert.False(t.Annotations?.DestructiveHint);
            });
        }
    }
}

public class SqlServerToolTests
{
    private static ToolServer Server(SqlQuery? query = null) => SqlServerServer.Build(McpFixtures.ExampleWithSql(), query ?? McpFixtures.FakeSql);

    [Fact]
    public void Statements_run_as_the_reader_login_and_always_revert()
    {
        var wrapped = SqlServerConnector.AsLogin("resolveai_reader", SqlText.Deadlocks);
        Assert.StartsWith("EXECUTE AS LOGIN = N'resolveai_reader';", wrapped);
        Assert.Contains("BEGIN CATCH\n  REVERT;\n  THROW;", wrapped);
        Assert.EndsWith("REVERT;", wrapped.Trim());
    }

    [Theory]
    [InlineData("reader'; DROP LOGIN x;--")]
    [InlineData("reader\n")]
    [InlineData("réader")]
    public void A_login_name_that_could_alter_the_SQL_is_refused_when_loading_config(string login)
    {
        var dir = Directory.CreateTempSubdirectory("resolveai-cfg-").FullName;
        try
        {
            var path = Path.Combine(dir, "c.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new { sqlserver = new { execute_as = login } }));
            Assert.Throws<InvalidDataException>(() => McpConfig.Load(path));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Existing_config_driver_names_stay_valid_and_others_are_refused()
    {
        var dir = Directory.CreateTempSubdirectory("resolveai-cfg-").FullName;
        try
        {
            var path = Path.Combine(dir, "c.json");
            foreach (var driver in new[] { "msnodesqlv8", "tedious" })
            {
                File.WriteAllText(path, JsonSerializer.Serialize(new { sqlserver = new { driver } }));
                Assert.Equal(driver, McpConfig.Load(path).Sqlserver.Driver);
            }
            File.WriteAllText(path, JsonSerializer.Serialize(new { sqlserver = new { driver = "other" } }));
            Assert.Throws<InvalidDataException>(() => McpConfig.Load(path));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Connection_strings_are_read_intent_and_a_SQL_login_comes_only_from_the_environment()
    {
        var windows = SqlServerConnector.ConnectionString(new SqlServerConfig { Driver = "msnodesqlv8" }, _ => null);
        Assert.Contains("Integrated Security=True", windows);
        Assert.Contains("Application Intent=ReadOnly", windows);
        Assert.Contains("Max Pool Size=2", windows);
        var err = Assert.Throws<ToolError>(() => SqlServerConnector.ConnectionString(new SqlServerConfig { Driver = "tedious" }, _ => null));
        Assert.Contains("MSSQL_USER and MSSQL_PASSWORD", err.Message);
        var login = SqlServerConnector.ConnectionString(new SqlServerConfig { Driver = "tedious" }, k => k == "MSSQL_USER" ? "u" : "p");
        Assert.Contains("User ID=u", login);
    }

    [Fact]
    public async Task Only_allow_listed_databases_are_touched_quoted_through_sp_executesql()
    {
        var ran = new List<(string Sql, IReadOnlyDictionary<string, object>? Args)>();
        SqlQuery recording = (sql, args, ct) =>
        {
            lock (ran) ran.Add((sql, args));
            return McpFixtures.FakeSql(sql, args, ct);
        };
        var ok = await McpFixtures.Call(Server(recording), "db_space");
        Assert.Contains("ResolveAIDemo is ONLINE; log reuse wait: NOTHING; 1 file(s), 1 near max size", ok.Payload.Summary);
        Assert.Contains(ran, r => r.Sql == "EXEC [ResolveAIDemo].sys.sp_executesql @stmt" && Equals(r.Args?["stmt"], SqlText.FileSpace));

        ran.Clear();
        var bad = await McpFixtures.Call(Server(recording), "db_space", new { database = "master]; DROP TABLE x;--" });
        Assert.True(bad.IsError);
        Assert.Contains("not in the allow-list", bad.Payload.Summary);
        Assert.Empty(ran);
    }

    [Fact]
    public async Task Error_sources_merge_and_degrade_when_one_is_unreadable()
    {
        SqlQuery query = (sql, args, ct) => sql == SqlText.SystemHealthErrors ? throw new InvalidOperationException("VIEW SERVER STATE permission was denied") : McpFixtures.FakeSql(sql, args, ct);
        var r = await McpFixtures.Call(Server(query), "db_errors", new { from = "2026-10-27T14:00:00Z", to = "2026-10-27T18:00:00Z" });
        Assert.False(r.IsError);
        Assert.Equal("1 SQL Server error(s) in the window (most common: 1222 ×1).", r.Payload.Summary);
    }

    [Fact]
    public async Task Says_when_SQL_Agent_is_missing()
    {
        SqlQuery none = (_, _, _) => Task.FromResult(new List<Dictionary<string, object?>>());
        Assert.Equal("SQL Agent is not available on this server.",
            (await McpFixtures.Call(Server(none), "db_failed_jobs", new { from = "2026-10-27T14:00:00Z", to = "2026-10-27T15:00:00Z" })).Payload.Summary);
    }

    /// <summary>Opt-in check against a real local SQL Server after running demo-systems/sqlserver/setup.sql (MSSQL_TEST=1).</summary>
    [Fact]
    public async Task Integration_every_tool_runs_as_the_reader_and_the_reader_cannot_write()
    {
        if (Environment.GetEnvironmentVariable("MSSQL_TEST") != "1") return;
        var cfg = McpFixtures.ExampleWithSql();
        cfg.Sqlserver = new SqlServerConfig { Enabled = true, Driver = "msnodesqlv8", ExecuteAs = "resolveai_reader" };
        var server = SqlServerServer.Build(cfg);
        var from = Js.Iso(DateTimeOffset.UtcNow.AddHours(-1));
        foreach (var (tool, args) in new (string, object?)[]
        {
            ("db_health", null), ("db_blocking", null), ("db_long_queries", new { min_seconds = 5 }), ("db_space", null),
            ("db_errors", new { from }), ("db_deadlocks", new { from }), ("db_failed_jobs", new { from }),
        })
        {
            var r = await McpFixtures.Call(server, tool, args);
            Assert.False(r.IsError, $"{tool}: {r.Payload.Summary}");
        }

        var query = SqlServerConnector.Connect(cfg.Sqlserver);
        var whoami = await query("SELECT SUSER_SNAME() AS who", null, default);
        Assert.Equal("resolveai_reader", whoami[0]["who"]);
        await Assert.ThrowsAnyAsync<Exception>(() => query("UPDATE [ResolveAIDemo].dbo.Orders SET Status = N'tampered' WHERE OrderId = 1", null, default));
        // Reverting must have worked, or a pooled connection would keep running as the reader.
        var raw = SqlServerConnector.Connect(new SqlServerConfig { Driver = "msnodesqlv8" });
        Assert.NotEqual("resolveai_reader", (await raw("SELECT SUSER_SNAME() AS who", null, default))[0]["who"]);
        var tampered = await raw("SELECT COUNT(*) AS n FROM [ResolveAIDemo].dbo.Orders WHERE Status = N'tampered'", null, default);
        Assert.Equal(0, tampered[0]["n"]);
    }
}
