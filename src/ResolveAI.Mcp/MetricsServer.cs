using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using ResolveAI.Contracts;

namespace ResolveAI.Mcp;

public static partial class MetricsServer
{
    // \z, not $: a service name becomes a file name.
    [GeneratedRegex(@"\A[a-zA-Z0-9][a-zA-Z0-9-]*\z")]
    private static partial Regex PlainService();

    private static double Round(double n) => Math.Round(n * 100, MidpointRounding.AwayFromZero) / 100;

    private sealed record Series(string Unit, List<(string Time, double Value)> Points);

    /// <summary>demo-systems/metrics/&lt;service&gt;.json: metric name → unit and [time, value] points, in file order.</summary>
    private static async Task<List<(string Name, Series Series)>> Load(McpConfig cfg, string service, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(cfg.Metrics.SeriesDir)) throw new ToolError("No metric source is configured (metrics.series_dir).");
        if (!PlainService().IsMatch(service)) throw new ToolError("service must be a plain service name.");
        try
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(cfg.Metrics.SeriesDir, $"{service}.json"), ct));
            return doc.RootElement.EnumerateObject().Select(m => (m.Name, new Series(
                m.Value.GetProperty("unit").GetString() ?? "",
                m.Value.GetProperty("points").EnumerateArray().Select(p => (p[0].GetString() ?? "", p[1].GetDouble())).ToList()))).ToList();
        }
        catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException)
        {
            throw new ToolError($"No metrics recorded for service \"{service}\".");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
        public readonly ulong Ticks => ((ulong)High << 32) | Low;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint Load;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPage;
        public ulong AvailablePage;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtended;
    }

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "GlobalMemoryStatusEx")]
    private static extern bool GlobalMemoryStatus(ref MemoryStatus status);

    private static (ulong Idle, ulong Total) CpuTimes()
    {
        if (OperatingSystem.IsWindows())
        {
            // Kernel time already includes idle time.
            return GetSystemTimes(out var idle, out var kernel, out var user) ? (idle.Ticks, kernel.Ticks + user.Ticks) : (0, 0);
        }
        if (!File.Exists("/proc/stat")) return (0, 0);
        var parts = File.ReadLines("/proc/stat").First().Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(ulong.Parse).ToList();
        var total = parts.Aggregate(0UL, (a, b) => a + b);
        return (parts[3] + (parts.Count > 4 ? parts[4] : 0), total);
    }

    /// <summary>Average CPU busy share across cores over a short sample.</summary>
    private static async Task<double> CpuBusyPercent(CancellationToken ct)
    {
        var a = CpuTimes();
        await Task.Delay(250, ct);
        var b = CpuTimes();
        var total = b.Total - a.Total;
        return total > 0 ? Round(100 - (double)(b.Idle - a.Idle) / total * 100) : 0;
    }

    private static (double Total, double Free) Memory()
    {
        if (OperatingSystem.IsWindows())
        {
            var s = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            return GlobalMemoryStatus(ref s) ? (s.TotalPhysical, s.AvailablePhysical) : (0, 0);
        }
        if (File.Exists("/proc/meminfo"))
        {
            var info = File.ReadLines("/proc/meminfo").Select(l => l.Split(':')).Where(p => p.Length == 2)
                .ToDictionary(p => p[0], p => double.TryParse(p[1].Replace("kB", "").Trim(), out var v) ? v * 1024 : 0);
            return (info.GetValueOrDefault("MemTotal"), info.GetValueOrDefault("MemAvailable"));
        }
        return (GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, 0);
    }

    public static ToolServer Build(McpConfig cfg) => new("resolveai-metrics",
    [
        new ToolDef("metrics_list",
            "List the metric names available for a service.",
            [Field.Str("service")],
            async (args, ct) =>
            {
                var service = args.Str("service");
                var file = await Load(cfg, service, ct);
                return ToolResult.Ok($"{file.Count} metric(s) for {service}: {string.Join(", ", file.Select(f => f.Name))}.",
                    file.Select(f => Row.Of(("name", f.Name), ("unit", f.Series.Unit), ("points", f.Series.Points.Count))), $"metrics/{service}");
            }),

        new ToolDef("metrics_series",
            "Read one metric for a service between from and to, with its baseline (first value), peak and when the peak happened.",
            [Field.Str("service"), Field.Str("name"), Field.Time("from"), Field.Time("to", required: false)],
            async (args, ct) =>
            {
                var service = args.Str("service");
                var name = args.Str("name");
                var w = TimeWindow.Parse(args.Str("from"), args.OptStr("to"));
                var file = await Load(cfg, service, ct);
                var match = file.FirstOrDefault(f => f.Name.ToLowerInvariant() == name.ToLowerInvariant());
                if (match.Series is null) throw new ToolError($"No metric \"{name}\" for {service}. Available: {string.Join(", ", file.Select(f => f.Name))}.");
                var (key, s) = match;
                var pts = s.Points.Where(p => w.Contains(p.Time)).ToList();
                if (pts.Count == 0) return ToolResult.Ok($"No {key} data for {service} in that window.", [], $"metrics/{service}");
                var peak = pts.Aggregate((m, p) => p.Value > m.Value ? p : m);
                var avg = Round(pts.Aggregate(0d, (a, p) => a + p.Value) / pts.Count);
                return ToolResult.Ok(
                    $"{service} {key}: baseline {Js.Num(pts[0].Value)} {s.Unit}, peak {Js.Num(peak.Value)} {s.Unit} at {peak.Time}, average {Js.Num(avg)} {s.Unit} over {pts.Count} point(s).",
                    pts.Select(p => Row.Of(("time", p.Time), ("value", p.Value), ("unit", s.Unit))), $"metrics/{service}");
            }),

        new ToolDef("metrics_host",
            "Current CPU, memory and uptime of the machine this server runs on (a point-in-time reading, not history).",
            [],
            async (_, ct) =>
            {
                var (total, free) = Memory();
                const double Gb = 1024d * 1024 * 1024;
                var host = Environment.MachineName;
                var cores = Environment.ProcessorCount;
                var busy = await CpuBusyPercent(ct);
                var used = total > 0 ? Round((total - free) / total * 100) : 0;
                return ToolResult.Ok($"Host {host}: CPU {Js.Num(busy)}% busy on {cores} cores, memory {Js.Num(used)}% used.",
                    [Row.Of(("host", host), ("read_at", Js.IsoNow()), ("cpu_cores", cores), ("cpu_busy_percent", busy),
                        ("memory_total_gb", Round(total / Gb)), ("memory_used_percent", used), ("uptime_hours", Round(Environment.TickCount64 / 3_600_000d)))], "host");
            }),
    ]);
}
