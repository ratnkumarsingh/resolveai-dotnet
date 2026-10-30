using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using ResolveAI.Contracts;

namespace ResolveAI.Mcp;

/// <summary>Confines a caller-supplied path to the allow-list.</summary>
public static class PathGuard
{
    private const int MaxLinkHops = 40;

    private static StringComparison Compare => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static bool IsInside(string child, string parent)
    {
        if (string.Equals(child, parent, Compare)) return true;
        var prefix = Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar;
        return child.StartsWith(prefix, Compare);
    }

    /// <summary>
    /// The path with every symlink and junction resolved, in every segment. Path.GetFullPath resolves none of them, and
    /// ResolveLinkTarget only looks at the last segment, so the walk is done here. Throws when a segment does not exist.
    /// </summary>
    public static string RealPath(string path, int hops = 0)
    {
        if (hops > MaxLinkHops) throw new IOException($"Too many links while resolving {path}.");
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? "";
        var current = root;
        foreach (var segment in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(current, segment);
            FileSystemInfo info = new FileInfo(next);
            if (!info.Exists)
            {
                info = new DirectoryInfo(next);
                if (!info.Exists) throw new FileNotFoundException($"Path not found: {next}");
            }
            current = info.LinkTarget is { } target
                ? RealPath(Path.GetFullPath(target, current), hops + 1)
                : next;
        }
        return current;
    }

    private static string? RealPathOrNull(string path)
    {
        try
        {
            return RealPath(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves links and <c>..</c> first, so a link pointing outside an allowed root is refused too. A missing path is
    /// checked through its parent folder. Relative paths are taken from the first allowed root.
    /// </summary>
    public static string Confine(string input, IReadOnlyList<string> allowedRoots)
    {
        if (allowedRoots.Count == 0) throw new ToolError("No folders are allowed. Add files.allowed_roots to the MCP config.");

        // Forms that skip normal path handling: device paths (\\?\, \\.\) and NTFS alternate streams.
        if (OperatingSystem.IsWindows() && (input.StartsWith(@"\\?\", StringComparison.Ordinal) || input.StartsWith(@"\\.\", StringComparison.Ordinal) || input.StartsWith("//?/", StringComparison.Ordinal) || input.StartsWith("//./", StringComparison.Ordinal)
            || input[(Path.IsPathRooted(input) ? (Path.GetPathRoot(input)?.Length ?? 0) : 0)..].Contains(':')))
        {
            throw new ToolError($"Path is outside the allowed folders: {input}");
        }

        var roots = allowedRoots.Select(r => RealPathOrNull(r) ?? Path.GetFullPath(r)).ToList();
        string candidate;
        try
        {
            candidate = Path.IsPathFullyQualified(input) ? Path.GetFullPath(input) : Path.GetFullPath(input, roots[0]);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ToolError($"Path not found: {input}");
        }

        var real = RealPathOrNull(candidate);
        if (real is null)
        {
            var parent = Path.GetDirectoryName(candidate);
            var realParent = parent is null ? null : RealPathOrNull(parent);
            if (realParent is null) throw new ToolError($"Path not found: {input}");
            real = Path.Combine(realParent, Path.GetFileName(candidate));
        }
        if (!roots.Any(r => IsInside(real, r))) throw new ToolError($"Path is outside the allowed folders: {input}");
        return real;
    }
}

public static partial class FilesServer
{
    private const int MaxFilesScanned = 500;
    private const long MaxFileBytes = 20 * 1024 * 1024;
    private const int TailBytes = 1024 * 1024;

    [GeneratedRegex(@"^\[?([0-9]{4}-[0-9]{2}-[0-9]{2}[T ][0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]+)?Z?)")]
    private static partial Regex LeadingTime();

    /// <summary>Lists files under a folder, depth-first, without following symlinked folders or junctions.</summary>
    private static IEnumerable<string> Walk(string dir, int[] budget)
    {
        FileSystemInfo[] entries;
        try
        {
            entries = new DirectoryInfo(dir).GetFileSystemInfos();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (var e in entries.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Name, StringComparer.Ordinal))
        {
            if (budget[0] <= 0) yield break;
            if (e.LinkTarget is not null) continue;
            if (e is DirectoryInfo)
            {
                foreach (var f in Walk(e.FullName, budget)) yield return f;
            }
            else
            {
                budget[0]--;
                yield return e.FullName;
            }
        }
    }

    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static bool IsBinary(string path)
    {
        using var fs = OpenRead(path);
        var buf = new byte[4096];
        var n = fs.Read(buf, 0, buf.Length);
        return Array.IndexOf(buf, (byte)0, 0, n) >= 0;
    }

    private static double Round1(double n) => Math.Round(n * 10, MidpointRounding.AwayFromZero) / 10;

    /// <summary>Unix access(2), so a read-only mount or permission problem reports the same codes operators expect.</summary>
    [DllImport("libc", SetLastError = true, EntryPoint = "access")]
    private static extern int UnixAccess(string path, int mode);

    /// <summary>Whether the path allows an access mode (4 read, 2 write), or the error code when it doesn't. Never opens anything for writing.</summary>
    private static object CanAccess(string path, int mode)
    {
        if (OperatingSystem.IsWindows())
        {
            if (mode == 2 && File.Exists(path) && new FileInfo(path).IsReadOnly) return "EPERM";
            return true;
        }
        if (UnixAccess(path, mode) == 0) return true;
        return Marshal.GetLastPInvokeError() switch { 13 => "EACCES", 30 => "EROFS", 1 => "EPERM", 2 => "ENOENT", _ => "error" };
    }

    public static ToolServer Build(McpConfig cfg)
    {
        var roots = cfg.Files.AllowedRoots;

        string Display(string p)
        {
            var r = roots.Select(Path.GetFullPath).FirstOrDefault(x => PathGuard.IsInside(p, x));
            if (r is null) return Path.GetFileName(p);
            var rel = Path.GetRelativePath(r, p);
            if (rel == ".") rel = "";
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(r));
            return $"{name}/{rel.Replace(Path.DirectorySeparatorChar, '/')}".TrimEnd('/');
        }

        return new ToolServer("resolveai-files",
        [
            new ToolDef("files_search",
                "Search text/log files under an allowed folder for lines containing a phrase (case-insensitive, plain text, not a regex). " +
                "Optionally keep only lines whose leading timestamp falls between from and to.",
                [
                    Field.Str("root", "Folder to search, absolute or relative to the first allowed root"),
                    Field.Str("phrase", "Plain text to look for, e.g. 'ERROR' or 'permission denied'", min: 2, max: 200),
                    Field.Time("from", required: false),
                    Field.Time("to", required: false),
                    Field.Int("max_matches", 1, Limits.MaxRows, 50),
                ],
                async (args, ct) =>
                {
                    var phrase = args.Str("phrase");
                    var dir = PathGuard.Confine(args.Str("root"), roots);
                    var window = args.OptStr("from") is { } from ? TimeWindow.Parse(from, args.OptStr("to")) : (TimeWindow?)null;
                    var maxMatches = args.Int("max_matches");
                    var needle = phrase.ToLowerInvariant();
                    var rows = new List<Dictionary<string, object?>>();
                    var files = 0;
                    var truncated = false;
                    foreach (var file in Walk(dir, [MaxFilesScanned]))
                    {
                        if (new FileInfo(file).Length > MaxFileBytes || IsBinary(file)) continue;
                        files++;
                        using var reader = new StreamReader(OpenRead(file), new UTF8Encoding(false));
                        var n = 0;
                        while (await reader.ReadLineAsync(ct) is { } line)
                        {
                            n++;
                            if (!line.ToLowerInvariant().Contains(needle, StringComparison.Ordinal)) continue;
                            var m = LeadingTime().Match(line);
                            var ts = m.Success ? m.Groups[1].Value.Replace(' ', 'T') : null;
                            if (window is { } w && !(ts is not null && w.Contains(ts.EndsWith('Z') ? ts : ts + "Z"))) continue;
                            if (rows.Count >= maxMatches)
                            {
                                truncated = true;
                                break;
                            }
                            rows.Add(Row.Of(("file", Display(file)), ("line", n), ("time", ts), ("text", Limits.Clip(line.Trim()))));
                        }
                        if (truncated) break;
                    }
                    var summary = rows.Count > 0
                        ? $"{rows.Count}{(truncated ? "+" : "")} line(s) containing \"{phrase}\" in {Display(dir)} ({files} file(s) scanned)."
                        : $"No lines containing \"{phrase}\" in {Display(dir)} ({files} file(s) scanned).";
                    return ToolResult.Ok(summary, rows, Display(dir), truncated);
                }),

            new ToolDef("files_tail",
                "Read the last lines of one text/log file inside an allowed folder.",
                [Field.Str("path"), Field.Int("lines", 1, Limits.MaxRows, 50)],
                async (args, ct) =>
                {
                    var path = args.Str("path");
                    var lines = args.Int("lines");
                    var file = PathGuard.Confine(path, roots);
                    var info = new FileInfo(file);
                    if (!info.Exists) throw new ToolError($"Not a file: {path}");
                    if (IsBinary(file)) throw new ToolError($"Not a text file: {path}");
                    string text;
                    await using (var fs = OpenRead(file))
                    {
                        var start = Math.Max(0, info.Length - TailBytes);
                        fs.Seek(start, SeekOrigin.Begin);
                        var buf = new byte[info.Length - start];
                        await fs.ReadExactlyAsync(buf, ct);
                        text = new UTF8Encoding(false).GetString(buf);
                    }
                    var parts = text.Split('\n').Select(l => l.EndsWith('\r') ? l[..^1] : l).ToList();
                    var all = parts.Where((l, i) => l.Length > 0 || i < parts.Count - 1).ToList();
                    var tail = all.Skip(Math.Max(0, all.Count - lines)).ToList();
                    var firstLine = all.Count - tail.Count + 1;
                    return ToolResult.Ok(
                        $"Last {tail.Count} line(s) of {Display(file)} ({info.Length.ToString("N0", CultureInfo.InvariantCulture)} bytes, modified {Js.Iso(info.LastWriteTimeUtc)}).",
                        tail.Select((t, i) => Row.Of(("line", firstLine + i), ("text", Limits.Clip(t)))), Display(file));
                }),

            new ToolDef("files_recent_changes",
                "List files under an allowed folder that were modified at or after a time, newest first.",
                [Field.Str("root"), Field.Time("since")],
                (args, _) =>
                {
                    var since = args.Str("since");
                    var dir = PathGuard.Confine(args.Str("root"), roots);
                    if (!Js.TryParseDate(since, out var t)) throw new ToolError("since must be an ISO-8601 time.");
                    var rows = Walk(dir, [MaxFilesScanned])
                        .Select(f => new FileInfo(f))
                        .Where(f => f.LastWriteTimeUtc >= t.UtcDateTime)
                        .Select(f => (Modified: Js.Iso(f.LastWriteTimeUtc), Row: Row.Of(("file", Display(f.FullName)), ("size_bytes", f.Length), ("modified", Js.Iso(f.LastWriteTimeUtc)))))
                        .OrderByDescending(x => x.Modified, StringComparer.Ordinal)
                        .Select(x => x.Row)
                        .ToList();
                    return Task.FromResult(ToolResult.Ok($"{rows.Count} file(s) in {Display(dir)} changed since {since}.", rows, Display(dir)));
                }),

            new ToolDef("files_disk_space",
                "Free and used space on the drive that holds an allowed folder.",
                [Field.Str("root")],
                (args, _) =>
                {
                    var dir = PathGuard.Confine(args.Str("root"), roots);
                    var drive = new DriveInfo(dir);
                    double total = drive.TotalSize;
                    double free = drive.AvailableFreeSpace;
                    var usedPct = total > 0 ? Math.Round((total - free) / total * 1000, MidpointRounding.AwayFromZero) / 10 : 0;
                    double Gb(double b) => Round1(b / (1024d * 1024 * 1024));
                    return Task.FromResult(ToolResult.Ok(
                        $"Drive holding {Display(dir)} is {Js.Num(usedPct)}% used ({Js.Num(Gb(free))} GB free of {Js.Num(Gb(total))} GB).",
                        [Row.Of(("folder", Display(dir)), ("total_gb", Gb(total)), ("free_gb", Gb(free)), ("used_percent", usedPct))], Display(dir)));
                }),

            new ToolDef("files_check",
                "Check one file or folder inside an allowed folder: whether it exists, its size and modified time, and whether it can be read and written.",
                [Field.Str("path")],
                (args, _) =>
                {
                    var p = PathGuard.Confine(args.Str("path"), roots);
                    FileSystemInfo info = File.Exists(p) ? new FileInfo(p) : new DirectoryInfo(p);
                    if (!info.Exists)
                    {
                        return Task.FromResult(ToolResult.Ok($"{Display(p)} does not exist.", [Row.Of(("path", Display(p)), ("exists", false))], Display(p)));
                    }
                    var isFile = info is FileInfo;
                    var size = isFile ? ((FileInfo)info).Length : 0;
                    var readable = CanAccess(p, 4);
                    var writable = CanAccess(p, 2);
                    string? openError = null;
                    if (isFile)
                    {
                        try
                        {
                            using var fs = OpenRead(p);
                        }
                        catch (UnauthorizedAccessException)
                        {
                            openError = "EACCES";
                        }
                        catch (IOException)
                        {
                            openError = "error";
                        }
                    }
                    var problem = openError ?? (readable is not true ? Js.Str(readable) : writable is not true ? $"not writable ({Js.Str(writable)})" : null);
                    var type = isFile ? "file" : "folder";
                    return Task.FromResult(ToolResult.Ok(
                        $"{Display(p)} exists ({type}, {size.ToString("N0", CultureInfo.InvariantCulture)} bytes){(problem is not null ? $"; problem: {problem}" : "")}.",
                        [Row.Of(("path", Display(p)), ("exists", true), ("type", type), ("size_bytes", size), ("modified", Js.Iso(info.LastWriteTimeUtc)),
                            ("readable", readable is true), ("writable", writable is true), ("problem", problem))], Display(p)));
                }),
        ]);
    }
}
