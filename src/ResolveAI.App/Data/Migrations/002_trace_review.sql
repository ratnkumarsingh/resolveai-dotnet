-- How each investigation was produced (stage timings and counts), shown in the UI as an audit trail.
ALTER TABLE investigations ADD COLUMN trace TEXT NOT NULL DEFAULT '[]';

-- RCA drafts keep their own validation notes (e.g. numbers not found in the evidence) and which mode wrote them.
ALTER TABLE rca_drafts ADD COLUMN validation_issues TEXT NOT NULL DEFAULT '[]';
ALTER TABLE rca_drafts ADD COLUMN llm_mode TEXT NOT NULL DEFAULT 'recorded';

CREATE INDEX IF NOT EXISTS idx_investigations_mode_started ON investigations(llm_mode, started_at);
