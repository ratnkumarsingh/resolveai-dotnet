/** Hard caps every tool applies, so one call can never return an unbounded amount of data. */
export const LIMITS = {
  /** Longest time window a tool will search. */
  maxWindowMs: 24 * 60 * 60_000,
  maxRows: 200,
  /** Upper bound on the JSON text a tool returns. */
  maxOutputChars: 64_000,
  /** Longest single text value (a log line, a query text) inside a result. */
  maxValueChars: 500,
  callTimeoutMs: 10_000,
} as const;

export const clip = (s: string, max: number = LIMITS.maxValueChars) => (s.length > max ? `${s.slice(0, max)}…` : s);
