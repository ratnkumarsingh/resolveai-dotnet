import { readFile } from "node:fs/promises";
import { cpus, freemem, hostname, totalmem, uptime } from "node:os";
import { join } from "node:path";
import { z } from "zod";
import type { McpConfig } from "../common/config.js";
import { ok, ToolError } from "../common/result.js";
import { createServer, readOnlyTool } from "../common/server.js";
import { isoTime, parseWindow, within } from "../common/window.js";

/** demo-systems/metrics/<service>.json: metric name → unit and [time, value] points. */
type SeriesFile = Record<string, { unit: string; points: [string, number][] }>;

const round = (n: number) => Math.round(n * 100) / 100;

/** Average CPU busy share across cores over a short sample. */
async function cpuBusyPercent(sampleMs = 250) {
  const snap = () => cpus().map((c) => ({ idle: c.times.idle, total: Object.values(c.times).reduce((a, b) => a + b, 0) }));
  const a = snap();
  await new Promise((r) => setTimeout(r, sampleMs));
  const b = snap();
  const idle = b.reduce((s, c, i) => s + c.idle - a[i].idle, 0);
  const total = b.reduce((s, c, i) => s + c.total - a[i].total, 0);
  return total ? round(100 - (idle / total) * 100) : 0;
}

export function buildMetricsServer(cfg: McpConfig) {
  const server = createServer("resolveai-metrics");

  const load = async (service: string): Promise<SeriesFile> => {
    if (!cfg.metrics.series_dir) throw new ToolError("No metric source is configured (metrics.series_dir).");
    if (!/^[a-z0-9][a-z0-9-]*$/i.test(service)) throw new ToolError("service must be a plain service name.");
    try {
      return JSON.parse(await readFile(join(cfg.metrics.series_dir, `${service}.json`), "utf8")) as SeriesFile;
    } catch {
      throw new ToolError(`No metrics recorded for service "${service}".`);
    }
  };

  readOnlyTool(server, "metrics_list",
    "List the metric names available for a service.",
    { service: z.string() },
    async ({ service }) => {
      const file = await load(service);
      const rows = Object.entries(file).map(([name, s]) => ({ name, unit: s.unit, points: s.points.length }));
      return ok(`${rows.length} metric(s) for ${service}: ${rows.map((r) => r.name).join(", ")}.`, rows, `metrics/${service}`);
    });

  readOnlyTool(server, "metrics_series",
    "Read one metric for a service between from and to, with its baseline (first value), peak and when the peak happened.",
    { service: z.string(), name: z.string(), from: isoTime, to: isoTime.optional() },
    async ({ service, name, from, to }) => {
      const w = parseWindow(from, to);
      const file = await load(service);
      const key = Object.keys(file).find((k) => k.toLowerCase() === name.toLowerCase());
      if (!key) throw new ToolError(`No metric "${name}" for ${service}. Available: ${Object.keys(file).join(", ")}.`);
      const s = file[key];
      const pts = s.points.filter(([t]) => within(t, w));
      if (pts.length === 0) return ok(`No ${key} data for ${service} in that window.`, [], `metrics/${service}`);
      const peak = pts.reduce((m, p) => (p[1] > m[1] ? p : m));
      const avg = round(pts.reduce((a, p) => a + p[1], 0) / pts.length);
      return ok(
        `${service} ${key}: baseline ${pts[0][1]} ${s.unit}, peak ${peak[1]} ${s.unit} at ${peak[0]}, average ${avg} ${s.unit} over ${pts.length} point(s).`,
        pts.map(([time, value]) => ({ time, value, unit: s.unit })), `metrics/${service}`);
    });

  readOnlyTool(server, "metrics_host",
    "Current CPU, memory and uptime of the machine this server runs on (a point-in-time reading, not history).",
    {},
    async () => {
      const total = totalmem();
      const free = freemem();
      const row = {
        host: hostname(), read_at: new Date().toISOString(), cpu_cores: cpus().length, cpu_busy_percent: await cpuBusyPercent(),
        memory_total_gb: round(total / 1024 ** 3), memory_used_percent: round(((total - free) / total) * 100), uptime_hours: round(uptime() / 3600),
      };
      return ok(`Host ${row.host}: CPU ${row.cpu_busy_percent}% busy on ${row.cpu_cores} cores, memory ${row.memory_used_percent}% used.`, [row], "host");
    });

  return server;
}
