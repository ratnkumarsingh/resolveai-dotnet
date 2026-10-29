import { execFile } from "node:child_process";
import { readFile } from "node:fs/promises";
import { promisify } from "node:util";
import { z } from "zod";
import type { McpConfig } from "../common/config.js";
import { clip, LIMITS } from "../common/limits.js";
import { ok, ToolError } from "../common/result.js";
import { createServer, readOnlyTool } from "../common/server.js";
import { isoTime, parseWindow, within } from "../common/window.js";

const run = promisify(execFile);
const PATCH_CHARS = 4000;
const SEP = "\x1f";

interface FeedEntry {
  service: string;
  version: string;
  environment?: string;
  deployed_at: string;
  change_summary: string;
  commit: string | null;
  author?: string;
  files_changed?: { path: string; added: number; removed: number }[];
  patch?: string;
}

/** Runs git with fixed arguments and no shell; the repo comes only from config. */
async function git(repo: string, args: string[]) {
  const { stdout } = await run("git", ["-C", repo, "--no-pager", ...args], { timeout: 8000, maxBuffer: 4 * 1024 * 1024, windowsHide: true });
  return stdout;
}

export function buildDeploysServer(cfg: McpConfig) {
  const server = createServer("resolveai-deploys");

  const feed = async (): Promise<FeedEntry[]> => {
    if (!cfg.deploys.feed) return [];
    return JSON.parse(await readFile(cfg.deploys.feed, "utf8")) as FeedEntry[];
  };
  const repoFor = (service: string) => cfg.services[service]?.repo ?? null;
  const knownService = async (service: string) => {
    if (cfg.services[service] || (await feed()).some((f) => f.service === service)) return;
    throw new ToolError(`Unknown service "${service}". Known: ${Object.keys(cfg.services).join(", ") || "none"}.`);
  };

  readOnlyTool(server, "deploys_recent",
    "List deployments and commits for a service (or every service when service is omitted) between from and to, newest first.",
    { service: z.string().optional(), from: isoTime, to: isoTime.optional() },
    async ({ service, from, to }) => {
      const w = parseWindow(from, to);
      if (service) await knownService(service);
      const rows: Record<string, unknown>[] = (await feed())
        .filter((f) => (!service || f.service === service) && within(f.deployed_at, w))
        .map((f) => ({
          kind: "deployment", service: f.service, version: f.version, environment: f.environment ?? "production",
          time: f.deployed_at, summary: clip(f.change_summary), commit: f.commit, author: f.author ?? null,
        }));
      const services = service ? [service] : Object.keys(cfg.services);
      for (const s of services) {
        const repo = repoFor(s);
        if (!repo) continue;
        const out = await git(repo, ["log", `--since=${w.from.toISOString()}`, `--until=${w.to.toISOString()}`,
          `--max-count=${LIMITS.maxRows}`, `--format=%h${SEP}%an${SEP}%cI${SEP}%s${SEP}%D`]);
        for (const line of out.split("\n").filter(Boolean)) {
          const [commit, author, time, subject, refs] = line.split(SEP);
          rows.push({ kind: "commit", service: s, version: /tag: ([^,]+)/.exec(refs)?.[1] ?? null, time: new Date(time).toISOString(), summary: clip(subject), commit, author });
        }
      }
      rows.sort((a, b) => String(b.time).localeCompare(String(a.time)));
      const scope = service ?? "all services";
      return ok(rows.length ? `${rows.length} deployment(s)/commit(s) for ${scope} between ${w.from.toISOString()} and ${w.to.toISOString()}.`
        : `No deployments or commits for ${scope} in that window.`, rows, service ? `deploys/${service}` : "deploys");
    });

  readOnlyTool(server, "deploys_change_summary",
    "Show what one deployment or commit changed for a service: changed files with line counts and a short patch.",
    { service: z.string(), commit: z.string().regex(/^[0-9a-f]{4,40}$/i, "commit must be a hex commit id") },
    async ({ service, commit }) => {
      await knownService(service);
      const entry = (await feed()).find((f) => f.service === service && f.commit?.toLowerCase().startsWith(commit.toLowerCase()));
      if (entry) {
        const files = entry.files_changed ?? [];
        const rows = [...files.map((f) => ({ ...f })), ...(entry.patch ? [{ patch: clip(entry.patch, PATCH_CHARS) }] : [])];
        return ok(`${service} ${entry.version} (${entry.commit}) changed ${files.length} file(s): ${clip(entry.change_summary, 200)}`, rows, `deploys/${service}`);
      }
      const repo = repoFor(service);
      if (!repo) throw new ToolError(`No deployment with commit ${commit} for ${service}, and no repo is configured for it.`);
      const head = await git(repo, ["show", "--no-patch", `--format=%h${SEP}%an${SEP}%cI${SEP}%s`, commit]).catch(() => {
        throw new ToolError(`Commit ${commit} not found in the ${service} repo.`);
      });
      const [short, author, time, subject] = head.trim().split(SEP);
      const numstat = await git(repo, ["show", "--numstat", "--format=", commit]);
      const files = numstat.split("\n").filter(Boolean).map((l) => {
        const [added, removed, path] = l.split("\t");
        return { path, added: Number(added) || 0, removed: Number(removed) || 0 };
      });
      const patch = await git(repo, ["show", "--format=", "--unified=2", "--no-color", commit]);
      return ok(`${service} commit ${short} by ${author} at ${new Date(time).toISOString()} changed ${files.length} file(s): ${clip(subject, 200)}`,
        [...files, { patch: clip(patch, PATCH_CHARS) }], `git/${service}`);
    });

  return server;
}
