using ResolveAI.App.Data;
using ResolveAI.Contracts;

namespace ResolveAI.App.Pipeline;

public static class EvidenceLoader
{
    /// <summary>Gathers the evidence for an incident deterministically: no model involvement.</summary>
    public static IncidentEvidence Load(Db db, Incident incident)
    {
        var start = Js.ParseDate(incident.StartedAt);
        var detected = Js.ParseDate(incident.DetectedAt);
        var windowFrom = Js.IsoSeconds(start.AddMinutes(-30));
        var windowTo = Js.IsoSeconds((detected > start ? detected : start).AddHours(1));

        // Logs for the affected service in the incident window, plus anything explicitly linked to it.
        var logs = db.All<LogEvent>(
            """
            SELECT id, timestamp, service_name, level, message, error_signature, trace_id, source
              FROM log_events
             WHERE (service_name = @service OR incident_id = @id) AND timestamp BETWEEN @from AND @to
             ORDER BY timestamp LIMIT 200
            """,
            new { service = incident.ServiceName, id = incident.Id, from = windowFrom, to = windowTo });
        foreach (var l in logs) l.Ref = EvidenceIds.Id("log", l.Id);

        var signals = new List<ErrorSignal>();
        foreach (var log in logs)
        {
            if (string.IsNullOrEmpty(log.ErrorSignature)) continue;
            var s = signals.Find(x => x.Signature == log.ErrorSignature);
            if (s is not null)
            {
                s.Count++;
                s.LastSeen = log.Timestamp;
            }
            else
            {
                signals.Add(new ErrorSignal { Signature = log.ErrorSignature, Count = 1, FirstSeen = log.Timestamp, LastSeen = log.Timestamp });
            }
        }

        // Same-service deployments in the 48h before the incident, and other services' deployments within ±1h.
        var deployments = db.All<Deployment>(
            """
            SELECT id, service_name, environment, version, deployed_at, change_summary, commit_ref
              FROM deployments
             WHERE (service_name = @service AND deployed_at BETWEEN @sameFrom AND @sameTo)
                OR (service_name <> @service AND deployed_at BETWEEN @otherFrom AND @otherTo)
             ORDER BY deployed_at DESC LIMIT 20
            """,
            new
            {
                service = incident.ServiceName,
                sameFrom = Js.IsoSeconds(start.AddHours(-48)),
                sameTo = windowTo,
                otherFrom = Js.IsoSeconds(start.AddHours(-1)),
                otherTo = Js.IsoSeconds(start.AddHours(1)),
            });
        foreach (var d in deployments) d.Ref = EvidenceIds.Id("dep", d.Id);

        var metrics = db.All<MetricSnapshot>("SELECT id, name, unit, baseline, peak, observed_at FROM metric_snapshots WHERE incident_id = @id ORDER BY name", new { id = incident.Id });
        foreach (var m in metrics) m.Ref = EvidenceIds.Id("met", m.Id);

        var timeline = db.All<TimelineEvent>("SELECT id, timestamp, kind, description FROM timeline_events WHERE incident_id = @id ORDER BY timestamp", new { id = incident.Id });
        foreach (var e in timeline) e.Ref = EvidenceIds.Id("tl", e.Id);

        var runbooks = db.All<KnowledgeDocument>(
            """
            SELECT id, title, doc_type, service_name, body FROM knowledge_documents
             WHERE doc_type IN ('runbook', 'guide') AND (service_name = @service OR service_name IS NULL)
             ORDER BY doc_type DESC, title LIMIT 5
            """,
            new { service = incident.ServiceName });
        foreach (var r in runbooks) r.Ref = EvidenceIds.Id("doc", r.Id);

        return new IncidentEvidence
        {
            Incident = incident,
            Timeline = timeline,
            Logs = logs,
            ErrorSignals = signals.OrderByDescending(s => s.Count).ToList(),
            Deployments = deployments,
            Metrics = metrics,
            Runbooks = runbooks,
        };
    }
}
