-- Every MCP tool call an investigation made: ResolveAI's baseline checks and Claude's follow-ups.
-- `seq` is the N in the evidence ID mcp:N that hypotheses cite.
CREATE TABLE tool_calls (
  id               TEXT PRIMARY KEY,
  investigation_id TEXT NOT NULL REFERENCES investigations(id) ON DELETE CASCADE,
  seq              INTEGER NOT NULL,
  phase            TEXT NOT NULL CHECK (phase IN ('baseline', 'follow_up')),
  system           TEXT NOT NULL,
  server           TEXT NOT NULL,
  tool             TEXT NOT NULL,
  args             TEXT NOT NULL,
  ok               INTEGER NOT NULL,
  summary          TEXT NOT NULL,
  rows             TEXT NOT NULL,
  truncated        INTEGER NOT NULL,
  duration_ms      INTEGER NOT NULL,
  started_at       TEXT NOT NULL,
  UNIQUE (investigation_id, seq)
);
