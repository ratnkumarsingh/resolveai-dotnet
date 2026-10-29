import { z } from "zod";
import { LIMITS } from "./limits.js";
import { ToolError } from "./result.js";

export const isoTime = z.string().describe("ISO-8601 UTC time, e.g. 2026-10-27T10:00:00Z");

/** Parses a from/to pair; `to` defaults to now and the span is capped at 24 hours. */
export function parseWindow(from: string, to?: string): { from: Date; to: Date } {
  const f = new Date(from);
  const t = to ? new Date(to) : new Date();
  if (Number.isNaN(f.getTime()) || Number.isNaN(t.getTime())) throw new ToolError("from/to must be ISO-8601 times.");
  if (t < f) throw new ToolError("to must be after from.");
  if (t.getTime() - f.getTime() > LIMITS.maxWindowMs) throw new ToolError("The time window is limited to 24 hours; narrow from/to.");
  return { from: f, to: t };
}

export const within = (iso: string, w: { from: Date; to: Date }) => {
  const ms = Date.parse(iso);
  return ms >= w.from.getTime() && ms <= w.to.getTime();
};
