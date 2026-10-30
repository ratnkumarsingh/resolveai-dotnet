-- ResolveAI MVP schema. IDs are UUID strings; JSON columns hold serialized arrays/objects.

CREATE TABLE incidents (
  id              TEXT PRIMARY KEY,
  key             TEXT NOT NULL UNIQUE,
  title           TEXT NOT NULL,
  service_name    TEXT NOT NULL,
  severity        TEXT NOT NULL CHECK (severity IN ('SEV-1','SEV-2','SEV-3','SEV-4')),
  status          TEXT NOT NULL CHECK (status IN ('open','investigating','mitigated','resolved')),
  environment     TEXT NOT NULL,
  started_at      TEXT NOT NULL,
  detected_at     TEXT NOT NULL,
  resolved_at     TEXT,
  description     TEXT NOT NULL,
  impact_summary  TEXT NOT NULL,
  created_at      TEXT NOT NULL
);
CREATE INDEX idx_incidents_status_started ON incidents(status, started_at);

-- Raw telemetry is immutable: sanitization happens when an evidence packet is built.
CREATE TABLE log_events (
  id               TEXT PRIMARY KEY,
  incident_id      TEXT REFERENCES incidents(id) ON DELETE SET NULL,
  timestamp        TEXT NOT NULL,
  service_name     TEXT NOT NULL,
  level            TEXT NOT NULL,
  message          TEXT NOT NULL,
  error_signature  TEXT,
  trace_id         TEXT,
  source           TEXT NOT NULL
);
CREATE INDEX idx_log_events_incident_ts ON log_events(incident_id, timestamp);
CREATE INDEX idx_log_events_service_ts ON log_events(service_name, timestamp);
CREATE TRIGGER log_events_immutable BEFORE UPDATE ON log_events
BEGIN
  SELECT RAISE(ABORT, 'log_events are immutable');
END;

CREATE TABLE deployments (
  id              TEXT PRIMARY KEY,
  service_name    TEXT NOT NULL,
  environment     TEXT NOT NULL,
  version         TEXT NOT NULL,
  deployed_at     TEXT NOT NULL,
  change_summary  TEXT NOT NULL,
  commit_ref      TEXT,
  incident_id     TEXT REFERENCES incidents(id) ON DELETE SET NULL
);
CREATE INDEX idx_deployments_service_deployed ON deployments(service_name, deployed_at);

CREATE TABLE metric_snapshots (
  id           TEXT PRIMARY KEY,
  incident_id  TEXT NOT NULL REFERENCES incidents(id) ON DELETE CASCADE,
  name         TEXT NOT NULL,
  unit         TEXT NOT NULL,
  baseline     REAL NOT NULL,
  peak         REAL NOT NULL,
  observed_at  TEXT NOT NULL
);
CREATE INDEX idx_metric_snapshots_incident ON metric_snapshots(incident_id);

CREATE TABLE timeline_events (
  id           TEXT PRIMARY KEY,
  incident_id  TEXT NOT NULL REFERENCES incidents(id) ON DELETE CASCADE,
  timestamp    TEXT NOT NULL,
  kind         TEXT NOT NULL,
  description  TEXT NOT NULL
);
CREATE INDEX idx_timeline_events_incident_ts ON timeline_events(incident_id, timestamp);

CREATE TABLE knowledge_documents (
  id            TEXT PRIMARY KEY,
  title         TEXT NOT NULL,
  doc_type      TEXT NOT NULL CHECK (doc_type IN ('runbook','rca','guide')),
  service_name  TEXT,
  body          TEXT NOT NULL,
  source_uri    TEXT,
  tags          TEXT NOT NULL DEFAULT '[]',
  created_at    TEXT NOT NULL,
  updated_at    TEXT NOT NULL
);
CREATE VIRTUAL TABLE knowledge_fts USING fts5(id UNINDEXED, title, body, tags);

CREATE TABLE historical_incidents (
  id               TEXT PRIMARY KEY,
  key              TEXT NOT NULL UNIQUE,
  title            TEXT NOT NULL,
  service_name     TEXT NOT NULL,
  symptoms         TEXT NOT NULL,
  root_cause       TEXT NOT NULL,
  resolution       TEXT NOT NULL,
  rca_document_id  TEXT REFERENCES knowledge_documents(id) ON DELETE SET NULL,
  resolved_at      TEXT NOT NULL,
  tags             TEXT NOT NULL DEFAULT '[]'
);
CREATE VIRTUAL TABLE historical_fts USING fts5(id UNINDEXED, title, service_name, symptoms, root_cause, resolution, tags);

CREATE TABLE investigations (
  id                     TEXT PRIMARY KEY,
  incident_id            TEXT NOT NULL REFERENCES incidents(id) ON DELETE CASCADE,
  status                 TEXT NOT NULL CHECK (status IN ('queued','running','completed','failed')),
  stage                  TEXT,
  question               TEXT,
  llm_mode               TEXT NOT NULL,
  evidence_packet        TEXT,
  model_name             TEXT,
  prompt_version         TEXT NOT NULL,
  summary                TEXT,
  insufficient_evidence  INTEGER NOT NULL DEFAULT 0,
  limitations            TEXT NOT NULL DEFAULT '[]',
  validation_issues      TEXT NOT NULL DEFAULT '[]',
  usage                  TEXT,
  started_at             TEXT NOT NULL,
  completed_at           TEXT,
  error_message          TEXT
);
CREATE INDEX idx_investigations_incident ON investigations(incident_id, started_at);

CREATE TABLE hypotheses (
  id                  TEXT PRIMARY KEY,
  investigation_id    TEXT NOT NULL REFERENCES investigations(id) ON DELETE CASCADE,
  rank                INTEGER NOT NULL,
  title               TEXT NOT NULL,
  explanation         TEXT NOT NULL,
  confidence          TEXT NOT NULL CHECK (confidence IN ('low','medium','high')),
  score               REAL NOT NULL,
  speculative         INTEGER NOT NULL DEFAULT 0,
  counter_evidence    TEXT NOT NULL DEFAULT '[]',
  unknowns            TEXT NOT NULL DEFAULT '[]',
  recommended_checks  TEXT NOT NULL DEFAULT '[]'
);
CREATE INDEX idx_hypotheses_investigation ON hypotheses(investigation_id, rank);

-- One row per (hypothesis, cited evidence). evidence_ref is a typed ID such as "log:<uuid>".
CREATE TABLE evidence_links (
  hypothesis_id  TEXT NOT NULL REFERENCES hypotheses(id) ON DELETE CASCADE,
  evidence_ref   TEXT NOT NULL,
  position       INTEGER NOT NULL,
  PRIMARY KEY (hypothesis_id, evidence_ref)
);

CREATE TABLE investigation_similar_incidents (
  investigation_id        TEXT NOT NULL REFERENCES investigations(id) ON DELETE CASCADE,
  historical_incident_id  TEXT NOT NULL REFERENCES historical_incidents(id) ON DELETE CASCADE,
  rank                    INTEGER NOT NULL,
  match_score             REAL NOT NULL,
  matched_terms           TEXT NOT NULL DEFAULT '[]',
  similarity_reason       TEXT NOT NULL,
  PRIMARY KEY (investigation_id, historical_incident_id)
);

CREATE TABLE rca_drafts (
  id                    TEXT PRIMARY KEY,
  incident_id           TEXT NOT NULL REFERENCES incidents(id) ON DELETE CASCADE,
  investigation_id      TEXT NOT NULL REFERENCES investigations(id) ON DELETE CASCADE,
  version               INTEGER NOT NULL,
  status                TEXT NOT NULL CHECK (status IN ('draft','reviewed')),
  impact                TEXT NOT NULL,
  timeline              TEXT NOT NULL DEFAULT '[]',
  root_cause            TEXT NOT NULL,
  contributing_factors  TEXT NOT NULL DEFAULT '[]',
  resolution_steps      TEXT NOT NULL DEFAULT '[]',
  prevention_actions    TEXT NOT NULL DEFAULT '[]',
  evidence_ids          TEXT NOT NULL DEFAULT '[]',
  generated_at          TEXT NOT NULL,
  updated_at            TEXT NOT NULL,
  UNIQUE (incident_id, version)
);

CREATE TABLE feedback (
  id                   TEXT PRIMARY KEY,
  investigation_id     TEXT NOT NULL REFERENCES investigations(id) ON DELETE CASCADE,
  hypothesis_id        TEXT REFERENCES hypotheses(id) ON DELETE CASCADE,
  helpful              INTEGER,
  comment              TEXT,
  verified_root_cause  TEXT,
  created_at           TEXT NOT NULL
);
CREATE INDEX idx_feedback_investigation ON feedback(investigation_id);
