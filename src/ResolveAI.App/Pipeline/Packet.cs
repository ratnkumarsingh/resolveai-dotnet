using System.Collections;
using System.Text.RegularExpressions;
using ResolveAI.Contracts;

namespace ResolveAI.App.Pipeline;

public static class Packet
{
    public const int MaxLogGroups = 40;
    public const int MaxPacketChars = 40_000;
    private const int MaxExcerpt = 600;
    private const int MaxToolExcerpt = 1200;

    private const RegexOptions Ecma = RegexOptions.ECMAScript | RegexOptions.CultureInvariant;

    private static readonly Regex Identifier = new(@"\b(req|request_id|trace|span)[-=:]?[\w-]+", Ecma | RegexOptions.IgnoreCase);
    private static readonly Regex Digits = new(@"\d+", Ecma);
    private static readonly Regex ErrorOrFatal = new("^(ERROR|FATAL)", Ecma);
    private static readonly Regex Warn = new("^WARN", Ecma);
    private static readonly Regex ErrorWarnFatal = new("^(ERROR|WARN|FATAL)", Ecma);

    private static string Value(object? v) => v is IDictionary or IList ? Json.Serialize(v) : Js.Str(v);

    /// <summary>One line per row, key=value pairs, nulls dropped: compact enough to fit several rows in an excerpt.</summary>
    private static string CompactRows(IEnumerable<Dictionary<string, object?>> rows) =>
        string.Join("\n", rows.Select(r => string.Join(' ', r.Where(kv => kv.Value is not null and not "").Select(kv => $"{kv.Key}={Value(kv.Value)}"))));

    /// <summary>A tool call as a packet item, sanitised like every other excerpt.</summary>
    public static (PacketItem Item, int Redactions) ToolItem(ToolCallRecord call)
    {
        var (text, redactions) = Sanitizer.Sanitize($"{(call.Ok ? "" : "CHECK FAILED: ")}{call.Summary}{(call.Rows.Count > 0 ? $"\n{CompactRows(call.Rows)}" : "")}");
        var excerpt = Js.Clip(text, MaxToolExcerpt);
        var args = string.Join(", ", call.Args.Select(kv => $"{kv.Key}={Js.Str(kv.Value)}"));
        return (new PacketItem { Id = call.Ref, Type = "tool_result", Timestamp = null, Source = $"{call.System}: {call.Tool}({args})", Excerpt = excerpt }, redactions);
    }

    /// <summary>Adds a follow-up tool result to an existing packet (the packet stays the single record of what the model saw).</summary>
    public static PacketItem AddToolItem(EvidencePacket packet, ToolCallRecord call)
    {
        var (item, redactions) = ToolItem(call);
        packet.Items.Add(item);
        packet.Redactions += redactions;
        return item;
    }

    /// <summary>Normalises a log line so repeats that differ only by IDs/numbers collapse into one group.</summary>
    private static string LogShape(string message) => Digits.Replace(Identifier.Replace(message, "$1#"), "#");

    /// <summary>
    /// Builds the bounded, sanitised, ordered evidence the model is allowed to see and cite.
    /// Every item has a stable typed ID; the validator only accepts citations of these IDs.
    /// </summary>
    public static EvidencePacket Build(IncidentEvidence evidence, List<SimilarCandidate> similar, string? question, IReadOnlyList<ToolCallRecord>? toolCalls = null)
    {
        var redactions = 0;
        string Clean(string s)
        {
            var (text, n) = Sanitizer.Sanitize(s);
            redactions += n;
            return Js.Clip(text, MaxExcerpt);
        }

        var items = new List<PacketItem>();
        foreach (var e in evidence.Timeline)
        {
            items.Add(new PacketItem { Id = e.Ref, Type = "timeline", Timestamp = e.Timestamp, Source = e.Kind, Excerpt = Clean(e.Description) });
        }

        // Deduplicate logs by shape, keeping the first occurrence's ID and counting repeats.
        var groups = new Dictionary<string, PacketItem>();
        var logGroups = new List<PacketItem>();
        foreach (var log in evidence.Logs)
        {
            var key = $"{log.ServiceName}|{log.Level}|{LogShape(log.Message)}";
            if (groups.TryGetValue(key, out var existing))
            {
                existing.Occurrences = (existing.Occurrences ?? 1) + 1;
                continue;
            }
            var item = new PacketItem
            {
                Id = log.Ref,
                Type = "log",
                Timestamp = log.Timestamp,
                Source = $"{log.Source}/{log.ServiceName}",
                Excerpt = Clean($"{log.Level} {log.Message}{(string.IsNullOrEmpty(log.ErrorSignature) ? "" : $" [signature={log.ErrorSignature}]")}"),
            };
            groups[key] = item;
            logGroups.Add(item);
        }

        // Prefer errors and warnings when over the cap, but keep chronological order in the packet.
        var prioritized = logGroups
            .Select((g, i) => (G: g, I: i, P: ErrorOrFatal.IsMatch(g.Excerpt) ? 0 : Warn.IsMatch(g.Excerpt) ? 1 : 2))
            .OrderBy(x => x.P).ThenBy(x => x.I)
            .Take(MaxLogGroups)
            .OrderBy(x => x.I)
            .Select(x => x.G)
            .ToList();
        var truncated = logGroups.Count > prioritized.Count;
        items.AddRange(prioritized);

        foreach (var d in evidence.Deployments)
        {
            items.Add(new PacketItem
            {
                Id = d.Ref,
                Type = "deployment",
                Timestamp = d.DeployedAt,
                Source = $"deploys/{d.ServiceName}",
                Excerpt = Clean($"{d.ServiceName} {d.Version} deployed to {d.Environment}: {d.ChangeSummary}{(string.IsNullOrEmpty(d.CommitRef) ? "" : $" (commit {d.CommitRef})")}"),
            });
        }
        foreach (var m in evidence.Metrics)
        {
            items.Add(new PacketItem
            {
                Id = m.Ref,
                Type = "metric",
                Timestamp = m.ObservedAt,
                Source = "metrics",
                Excerpt = $"{m.Name}: baseline {Js.Num(m.Baseline)} {m.Unit} → peak {Js.Num(m.Peak)} {m.Unit}",
            });
        }
        foreach (var r in evidence.Runbooks)
        {
            items.Add(new PacketItem { Id = r.Ref, Type = "runbook", Timestamp = null, Source = r.DocType, Excerpt = Clean($"{r.Title}: {r.Body}") });
        }
        foreach (var c in similar)
        {
            var h = c.Historical;
            items.Add(new PacketItem
            {
                Id = h.Ref,
                Type = "historical_incident",
                Timestamp = h.ResolvedAt,
                Source = "incident-history",
                Excerpt = Clean($"{h.Key} {h.Title} ({h.ServiceName}). Symptoms: {h.Symptoms}. Root cause: {h.RootCause}. Resolution: {h.Resolution}."),
            });
        }

        items = items.OrderBy(i => i.Timestamp ?? "9999", StringComparer.Ordinal).ToList();

        // Tool results come last, in call order (they describe what the systems showed when checked).
        foreach (var call in toolCalls ?? [])
        {
            var (item, n) = ToolItem(call);
            items.Add(item);
            redactions += n;
        }

        // Enforce the overall size budget by dropping the lowest-priority items (info logs first).
        while (Json.Serialize(items).Length > MaxPacketChars)
        {
            var idx = items.FindLastIndex(i => i.Type == "log" && !ErrorWarnFatal.IsMatch(i.Excerpt));
            var victim = idx >= 0 ? idx : items.FindLastIndex(i => i.Type == "log");
            if (victim < 0) break;
            items.RemoveAt(victim);
            truncated = true;
        }

        var inc = evidence.Incident;
        return new EvidencePacket
        {
            Incident = new PacketIncident
            {
                Key = inc.Key,
                Title = Clean(inc.Title),
                Service = inc.ServiceName,
                Severity = inc.Severity,
                Environment = inc.Environment,
                StartedAt = inc.StartedAt,
                DetectedAt = inc.DetectedAt,
                Description = Clean(inc.Description),
                ImpactSummary = Clean(inc.ImpactSummary),
            },
            Question = string.IsNullOrEmpty(question) ? null : Clean(question),
            Items = items,
            SimilarCandidates = similar.Select(c => c.Historical.Ref).ToList(),
            Redactions = redactions,
            Truncated = truncated,
        };
    }
}
