using System.Text.RegularExpressions;
using ResolveAI.App.Data;
using ResolveAI.Contracts;

namespace ResolveAI.App.Pipeline;

public sealed record SimilarCandidate(HistoricalIncident Historical, double MatchScore, List<string> MatchedTerms);

public static partial class Similar
{
    private static readonly HashSet<string> Stopwords =
        "the and for with from that this are was were has have had not but all any can its into out our over per than then they after before about been being did does more most some such only very request requests failed error errors".Split(' ').ToHashSet();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonWord();

    public static List<string> Terms(string text) =>
        NonWord().Split(text.ToLowerInvariant())
            .Where(w => w.Length >= 3 && !Stopwords.Contains(w) && !w.All(char.IsAsciiDigit))
            .Distinct()
            .ToList();

    private sealed class Row
    {
        public string Id { get; set; } = "";
        public string Key { get; set; } = "";
        public string Title { get; set; } = "";
        public string ServiceName { get; set; } = "";
        public string Symptoms { get; set; } = "";
        public string RootCause { get; set; } = "";
        public string Resolution { get; set; } = "";
        public string ResolvedAt { get; set; } = "";
        public string? RcaDocumentId { get; set; }
        public double Rank { get; set; }
    }

    /// <summary>
    /// Deterministic similar-incident retrieval: FTS5 bm25 over historical incidents, boosted for the same service.
    /// The model never chooses which incidents are similar; it only explains the matches.
    /// </summary>
    public static List<SimilarCandidate> Find(Db db, IncidentEvidence evidence, int limit = 5)
    {
        var incident = evidence.Incident;
        var queryText = string.Join(' ', new[] { incident.Title, incident.Description }
            .Concat(evidence.ErrorSignals.Select(s => s.Signature))
            .Concat(evidence.Deployments.Where(d => d.ServiceName == incident.ServiceName).Select(d => d.ChangeSummary)));
        var queryTerms = Terms(queryText);
        if (queryTerms.Count == 0) return [];

        var match = string.Join(" OR ", queryTerms.Select(w => $"\"{w}\""));
        var rows = db.All<Row>(
            """
            SELECT h.id, h.key, h.title, h.service_name, h.symptoms, h.root_cause, h.resolution, h.resolved_at, h.rca_document_id,
                   bm25(historical_fts) AS rank
              FROM historical_fts JOIN historical_incidents h ON h.id = historical_fts.id
             WHERE historical_fts MATCH @match
             ORDER BY rank LIMIT 20
            """,
            new { match });

        var scored = rows.Select(h =>
            {
                var haystack = Terms($"{h.Title} {h.Symptoms} {h.RootCause} {h.Resolution}").ToHashSet();
                var matched = queryTerms.Where(haystack.Contains).ToList();
                // bm25 is negative (lower is better); flip it and add a same-service boost.
                var score = -h.Rank * 10 + matched.Count + (h.ServiceName == incident.ServiceName ? 3 : 0);
                return new SimilarCandidate(new HistoricalIncident
                {
                    Id = h.Id, Ref = EvidenceIds.Id("hist", h.Id), Key = h.Key, Title = h.Title, ServiceName = h.ServiceName, Symptoms = h.Symptoms,
                    RootCause = h.RootCause, Resolution = h.Resolution, ResolvedAt = h.ResolvedAt, RcaDocumentId = h.RcaDocumentId,
                }, Js.Round(score * 100) / 100, matched);
            })
            .Where(c => c.MatchedTerms.Count >= 2)
            .OrderByDescending(c => c.MatchScore)
            .ToList();
        // Drop weak tail matches: anything scoring under 35% of the best candidate is noise.
        var best = scored.FirstOrDefault()?.MatchScore ?? 0;
        return scored.Where(c => c.MatchScore >= best * 0.35).Take(limit).ToList();
    }
}
