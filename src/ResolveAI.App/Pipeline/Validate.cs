using System.Globalization;
using System.Text.RegularExpressions;
using ResolveAI.Contracts;

namespace ResolveAI.App.Pipeline;

public sealed class ValidatedHypothesis
{
    public int Rank { get; set; }
    public string Title { get; set; } = "";
    public string Explanation { get; set; } = "";
    public string Confidence { get; set; } = "low";
    public double Score { get; set; }
    public bool Speculative { get; set; }
    public List<string> EvidenceIds { get; set; } = [];
    public List<string> CounterEvidence { get; set; } = [];
    public List<string> Unknowns { get; set; } = [];
    public List<RecommendedCheck> RecommendedChecks { get; set; } = [];
}

public sealed record ValidatedInvestigation(
    string Summary, bool InsufficientEvidence, List<ValidatedHypothesis> Hypotheses, Dictionary<string, string> SimilarReasons, List<string> Limitations, List<string> Issues);

public static partial class Validate
{
    private const int MaxHypotheses = 5;
    private const int MaxList = 8;
    private const int MaxChecks = 5;

    /// <summary>Deterministic ranking score. It orders hypotheses only; it is not a probability and is never shown as a percentage.</summary>
    public static double Score(string confidence, int supporting, int counter) =>
        (confidence switch { "high" => 3, "medium" => 2, _ => 1 }) * 2 + Math.Min(supporting, 5) - counter;

    /// <summary>Recommended checks must be read-only. This catches commands and actions that change state.</summary>
    private static readonly Regex UnsafeAction = new(
        @"\brm\s+-[a-z]*[rf]|\b(drop|truncate|delete\s+from|alter|update\s+\w+\s+set)\b.*\b(table|database|from|set)\b|\bkubectl\s+(delete|apply|scale|rollout\s+undo|edit)\b|\b(terraform|helm)\s+(apply|destroy|rollback|uninstall)\b|\bshutdown\b|\breboot\b|\bmkfs\b|\bchmod\s+777\b",
        RegexOptions.IgnoreCase | RegexOptions.ECMAScript | RegexOptions.CultureInvariant);

    // A number in prose, optionally with a unit. Lookarounds skip versions (v2.8.1), dates, times and IDs. Written with explicit
    // ASCII classes because lookbehind rules out ECMAScript mode, and \d and \w would otherwise match all of Unicode.
    [GeneratedRegex(@"(?<![A-Za-z0-9_.:/-])([0-9]{1,3}(?:,[0-9]{3})+|[0-9]+(?:\.[0-9]+)?)[ \t\r\n\f\v]?(%|ms|s|sec|seconds?|min|minutes?|h|hours?|x)?(?![A-Za-z0-9_:/-]|\.[0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ClaimedNumber();

    [GeneratedRegex(@"[0-9]+(?:\.[0-9]+)?")]
    private static partial Regex AnyNumber();

    [GeneratedRegex("([0-9]),([0-9]{3})")]
    private static partial Regex GroupedNumber();

    private static string Normalise(string n) => Js.Num(double.Parse(n.Replace(",", ""), CultureInfo.InvariantCulture));

    /// <summary>Every number that appears anywhere in the given texts (permissive: timestamps, IDs and config values count).</summary>
    public static HashSet<string> NumbersIn(params string[] texts)
    {
        var found = new HashSet<string>();
        foreach (var t in texts)
        {
            foreach (Match m in AnyNumber().Matches(GroupedNumber().Replace(t, "$1$2"))) found.Add(Normalise(m.Value));
        }
        return found;
    }

    /// <summary>Text the model saw, for checking that the numbers it wrote come from the evidence.</summary>
    public static string PacketText(EvidencePacket packet)
    {
        var i = packet.Incident;
        return string.Join("\n", new[] { i.Title, i.Description, i.ImpactSummary, i.StartedAt, i.DetectedAt, packet.Question ?? "" }
            .Concat(packet.Items.Select(x => $"{x.Timestamp ?? ""} {x.Excerpt} {(x.Occurrences is { } n ? n.ToString(CultureInfo.InvariantCulture) : "")}")));
    }

    /// <summary>
    /// Flags numbers the model stated that don't occur in the evidence (it must not invent metrics).
    /// Unit-less numbers under 10 are ignored: counts like "3 retries" are usually the model's own tally.
    /// </summary>
    public static List<string> UnverifiedNumbers(string text, HashSet<string> known)
    {
        var output = new List<string>();
        foreach (Match m in ClaimedNumber().Matches(text))
        {
            var value = Normalise(m.Groups[1].Value);
            if (!m.Groups[2].Success && double.Parse(value, CultureInfo.InvariantCulture) < 10) continue;
            if (!known.Contains(value))
            {
                var claim = m.Value.Trim();
                if (!output.Contains(claim)) output.Add(claim);
            }
        }
        return output;
    }

    private static string Clip(string s, int n) => s.Length > n ? s[..(n - 1)] + "…" : s;

    private static List<string> ClipList(IEnumerable<string> xs, int n = MaxList, int len = 500) =>
        xs.Where(x => !string.IsNullOrWhiteSpace(x)).Take(n).Select(x => Clip(x.Trim(), len)).ToList();

    /// <summary>
    /// Enforces the output contract beyond what the JSON schema can express: citations must reference
    /// packet IDs, lists and strings are bounded, and hypotheses are ranked by a deterministic score.
    /// </summary>
    public static ValidatedInvestigation Investigation(InvestigationOutput output, EvidencePacket packet)
    {
        var issues = new List<string>();
        var knownIds = packet.Items.Select(i => i.Id).ToHashSet();
        var candidates = packet.SimilarCandidates.ToHashSet();

        if (output.Hypotheses.Count > MaxHypotheses) issues.Add($"Model returned {output.Hypotheses.Count} hypotheses; kept the first {MaxHypotheses}.");

        var hypotheses = output.Hypotheses.Take(MaxHypotheses).Select(h =>
        {
            var cited = h.EvidenceIds.Select(id => id.Trim()).Distinct().ToList();
            var valid = cited.Where(knownIds.Contains).ToList();
            foreach (var bad in cited.Where(id => !knownIds.Contains(id)))
            {
                issues.Add($"Removed citation \"{Clip(bad, 80)}\" from \"{Clip(h.Title, 60)}\": not in the evidence packet.");
            }
            var speculative = valid.Count == 0;
            var unknowns = ClipList(h.Unknowns);
            if (speculative) unknowns.Insert(0, "No supporting evidence in the packet — speculative, insufficient evidence.");
            var counter = ClipList(h.CounterEvidence);
            // A hypothesis without valid evidence cannot be rated above low confidence.
            var confidence = speculative ? "low" : h.Confidence;
            return new ValidatedHypothesis
            {
                Title = Clip(h.Title.Trim() is { Length: > 0 } t ? t : "Untitled hypothesis", 200),
                Explanation = Clip(h.Explanation.Trim(), 2000),
                Confidence = confidence,
                Score = Score(confidence, valid.Count, counter.Count),
                Speculative = speculative,
                EvidenceIds = valid,
                CounterEvidence = counter,
                Unknowns = unknowns.Take(MaxList).ToList(),
                RecommendedChecks = h.RecommendedChecks
                    .Where(c =>
                    {
                        if (!UnsafeAction.IsMatch($"{c.Step} {c.Why}")) return true;
                        issues.Add($"Removed a recommended check from \"{Clip(h.Title, 60)}\": it described a destructive or state-changing action.");
                        return false;
                    })
                    .ToList()
                    .Take(MaxChecks)
                    .Select(c => new RecommendedCheck { Step = Clip(c.Step, 400), Why = Clip(c.Why, 400), ExpectedSignal = Clip(c.ExpectedSignal, 400) })
                    .ToList(),
            };
        }).ToList();

        hypotheses = hypotheses.OrderByDescending(h => h.Score).ToList();
        for (var i = 0; i < hypotheses.Count; i++) hypotheses[i].Rank = i + 1;

        var known = NumbersIn(PacketText(packet));
        foreach (var h in hypotheses)
        {
            foreach (var n in UnverifiedNumbers($"{h.Title} {h.Explanation}", known))
            {
                issues.Add($"Unverified number \"{Clip(n, 30)}\" in hypothesis {h.Rank} (\"{Clip(h.Title, 60)}\"): it does not appear in the evidence.");
            }
        }

        var similarReasons = new Dictionary<string, string>();
        foreach (var s in output.SimilarIncidents)
        {
            if (candidates.Contains(s.IncidentId)) similarReasons[s.IncidentId] = Clip(s.SimilarityReason, 600);
            else issues.Add($"Ignored similar incident \"{Clip(s.IncidentId, 80)}\": not among retrieved candidates.");
        }

        var insufficient = output.InsufficientEvidence;
        if (!insufficient && !hypotheses.Any(h => !h.Speculative))
        {
            insufficient = true;
            issues.Add("No hypothesis had valid supporting evidence; marked as insufficient evidence.");
        }

        return new ValidatedInvestigation(Clip(output.Summary.Trim(), 2000), insufficient, hypotheses, similarReasons, ClipList(output.Limitations), issues);
    }

    /// <summary><c>sourceText</c> is everything the RCA may legitimately quote numbers from: the evidence packet plus the engineer's verified cause and notes.</summary>
    public static (RcaOutput Rca, List<string> EvidenceIds, List<string> Issues) Rca(RcaOutput output, HashSet<string> knownIds, string sourceText)
    {
        var issues = new List<string>();
        var cited = new List<string>();
        var timeline = output.Timeline.Take(30).Select(e =>
        {
            var ids = e.EvidenceIds.Where(id =>
            {
                if (knownIds.Contains(id))
                {
                    if (!cited.Contains(id)) cited.Add(id);
                    return true;
                }
                issues.Add($"Removed RCA timeline citation \"{Clip(id, 80)}\": not in the investigation evidence.");
                return false;
            }).ToList();
            return new RcaTimelineEntry { Timestamp = Clip(e.Timestamp, 40), Event = Clip(e.Event, 500), EvidenceIds = ids };
        }).ToList();
        timeline = timeline.OrderBy(e => e.Timestamp, StringComparer.Ordinal).ToList();

        var known = NumbersIn(sourceText);
        var fields = new List<(string Where, string Text)> { ("impact", output.Impact), ("root cause", output.RootCause) };
        fields.AddRange(timeline.Select(e => ($"timeline {e.Timestamp}", e.Event)));
        foreach (var (where, text) in fields)
        {
            foreach (var n in UnverifiedNumbers(text, known)) issues.Add($"Unverified number \"{Clip(n, 30)}\" in {where}: it does not appear in the evidence.");
        }

        return (new RcaOutput
        {
            Impact = Clip(output.Impact, 3000),
            Timeline = timeline,
            RootCause = Clip(output.RootCause, 3000),
            ContributingFactors = ClipList(output.ContributingFactors, 15, 1000),
            ResolutionSteps = ClipList(output.ResolutionSteps, 15, 1000),
            PreventionActions = ClipList(output.PreventionActions, 15, 1000),
        }, cited, issues);
    }
}
