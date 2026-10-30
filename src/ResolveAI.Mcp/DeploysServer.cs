using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ResolveAI.Contracts;

namespace ResolveAI.Mcp;

public static partial class DeploysServer
{
    private const int PatchChars = 4000;
    private const char Sep = '\x1f';
    private const int MaxGitOutputBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(8);

    // \z, not $: in .NET, $ also matches before a trailing newline. A commit value reaches a git command line.
    [GeneratedRegex(@"\A[0-9a-fA-F]{4,40}\z")]
    private static partial Regex CommitId();

    [GeneratedRegex("tag: ([^,]+)")]
    private static partial Regex Tag();

    private sealed class FileChange
    {
        public string Path { get; set; } = "";
        public int Added { get; set; }
        public int Removed { get; set; }
    }

    private sealed class FeedEntry
    {
        public string Service { get; set; } = "";
        public string Version { get; set; } = "";
        public string? Environment { get; set; }
        public string DeployedAt { get; set; } = "";
        public string ChangeSummary { get; set; } = "";
        public string? Commit { get; set; }
        public string? Author { get; set; }
        public List<FileChange>? FilesChanged { get; set; }
        public string? Patch { get; set; }
    }

    /// <summary>Runs git with fixed arguments and no shell; the repo comes only from config. Killed at the timeout, and its output is capped.</summary>
    private static async Task<string> Git(string repo, IEnumerable<string> args, CancellationToken ct)
    {
        var start = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        foreach (var a in new[] { "-C", repo, "--no-pager" }.Concat(args)) start.ArgumentList.Add(a);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start git.");
        process.StandardInput.Close();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(GitTimeout);
        try
        {
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            var output = new StringBuilder();
            var buffer = new char[16 * 1024];
            int read;
            while ((read = await process.StandardOutput.ReadAsync(buffer, timeout.Token)) > 0)
            {
                output.Append(buffer, 0, read);
                if (output.Length > MaxGitOutputBytes) throw new ToolError("git returned more output than allowed.");
            }
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0) throw new InvalidOperationException($"git exited with {process.ExitCode}: {(await stderr).Trim()}");
            return output.ToString();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ToolError("git took too long and was stopped.");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    public static ToolServer Build(McpConfig cfg)
    {
        async Task<List<FeedEntry>> Feed(CancellationToken ct) =>
            string.IsNullOrEmpty(cfg.Deploys.Feed)
                ? []
                : JsonSerializer.Deserialize<List<FeedEntry>>(await File.ReadAllTextAsync(cfg.Deploys.Feed, ct), Json.Options) ?? [];

        string? RepoFor(string service) => cfg.Services.GetValueOrDefault(service)?.Repo;

        async Task KnownService(string service, CancellationToken ct)
        {
            if (cfg.Services.ContainsKey(service) || (await Feed(ct)).Any(f => f.Service == service)) return;
            throw new ToolError($"Unknown service \"{service}\". Known: {(cfg.Services.Count > 0 ? string.Join(", ", cfg.Services.Keys) : "none")}.");
        }

        return new ToolServer("resolveai-deploys",
        [
            new ToolDef("deploys_recent",
                "List deployments and commits for a service (or every service when service is omitted) between from and to, newest first.",
                [Field.Str("service", required: false), Field.Time("from"), Field.Time("to", required: false)],
                async (args, ct) =>
                {
                    var service = args.OptStr("service");
                    var w = TimeWindow.Parse(args.Str("from"), args.OptStr("to"));
                    if (service is not null) await KnownService(service, ct);
                    var rows = (await Feed(ct))
                        .Where(f => (service is null || f.Service == service) && w.Contains(f.DeployedAt))
                        .Select(f => Row.Of(("kind", "deployment"), ("service", f.Service), ("version", f.Version), ("environment", f.Environment ?? "production"),
                            ("time", f.DeployedAt), ("summary", Limits.Clip(f.ChangeSummary)), ("commit", f.Commit), ("author", f.Author)))
                        .ToList();
                    foreach (var s in service is not null ? [service] : cfg.Services.Keys.ToList())
                    {
                        var repo = RepoFor(s);
                        if (string.IsNullOrEmpty(repo)) continue;
                        var output = await Git(repo,
                            ["log", $"--since={Js.Iso(w.From)}", $"--until={Js.Iso(w.To)}", $"--max-count={Limits.MaxRows}", $"--format=%h{Sep}%an{Sep}%cI{Sep}%s{Sep}%D"], ct);
                        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                        {
                            var p = line.Split(Sep);
                            var tag = Tag().Match(p[4]);
                            rows.Add(Row.Of(("kind", "commit"), ("service", s), ("version", tag.Success ? tag.Groups[1].Value : null),
                                ("time", Js.Iso(Js.ParseDate(p[2]))), ("summary", Limits.Clip(p[3])), ("commit", p[0]), ("author", p[1])));
                        }
                    }
                    var sorted = rows.OrderByDescending(r => Js.Str(r["time"]), StringComparer.Ordinal).ToList();
                    var scope = service ?? "all services";
                    return ToolResult.Ok(
                        sorted.Count > 0 ? $"{sorted.Count} deployment(s)/commit(s) for {scope} between {Js.Iso(w.From)} and {Js.Iso(w.To)}." : $"No deployments or commits for {scope} in that window.",
                        sorted, service is not null ? $"deploys/{service}" : "deploys");
                }),

            new ToolDef("deploys_change_summary",
                "Show what one deployment or commit changed for a service: changed files with line counts and a short patch.",
                [Field.Str("service"), Field.Match("commit", CommitId(), "^[0-9a-f]{4,40}$")],
                async (args, ct) =>
                {
                    var service = args.Str("service");
                    var commit = args.Str("commit");
                    await KnownService(service, ct);
                    var entry = (await Feed(ct)).FirstOrDefault(f => f.Service == service && f.Commit is not null && f.Commit.StartsWith(commit, StringComparison.OrdinalIgnoreCase));
                    if (entry is not null)
                    {
                        var changed = entry.FilesChanged ?? [];
                        var feedRows = changed.Select(f => Row.Of(("path", f.Path), ("added", f.Added), ("removed", f.Removed))).ToList();
                        if (!string.IsNullOrEmpty(entry.Patch)) feedRows.Add(Row.Of(("patch", Limits.Clip(entry.Patch, PatchChars))));
                        return ToolResult.Ok($"{service} {entry.Version} ({entry.Commit}) changed {changed.Count} file(s): {Limits.Clip(entry.ChangeSummary, 200)}", feedRows, $"deploys/{service}");
                    }

                    var repo = RepoFor(service);
                    if (string.IsNullOrEmpty(repo)) throw new ToolError($"No deployment with commit {commit} for {service}, and no repo is configured for it.");
                    string head;
                    try
                    {
                        head = await Git(repo, ["show", "--no-patch", $"--format=%h{Sep}%an{Sep}%cI{Sep}%s", commit], ct);
                    }
                    catch (InvalidOperationException)
                    {
                        throw new ToolError($"Commit {commit} not found in the {service} repo.");
                    }
                    var h = head.Trim().Split(Sep);
                    var numstat = await Git(repo, ["show", "--numstat", "--format=", commit], ct);
                    var files = numstat.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l =>
                    {
                        var p = l.TrimEnd('\r').Split('\t');
                        return Row.Of(("path", p[2]), ("added", int.TryParse(p[0], out var a) ? a : 0), ("removed", int.TryParse(p[1], out var r) ? r : 0));
                    }).ToList();
                    var patch = await Git(repo, ["show", "--format=", "--unified=2", "--no-color", commit], ct);
                    var count = files.Count;
                    files.Add(Row.Of(("patch", Limits.Clip(patch, PatchChars))));
                    return ToolResult.Ok($"{service} commit {h[0]} by {h[1]} at {Js.Iso(Js.ParseDate(h[2]))} changed {count} file(s): {Limits.Clip(h[3], 200)}", files, $"git/{service}");
                }),
        ]);
    }
}
