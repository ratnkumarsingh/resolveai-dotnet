import { constants, createReadStream } from "node:fs";
import { access, open, readdir, realpath, stat, statfs } from "node:fs/promises";
import { basename, dirname, isAbsolute, join, relative, resolve, sep } from "node:path";
import { createInterface } from "node:readline";
import { z } from "zod";
import type { McpConfig } from "../common/config.js";
import { clip, LIMITS } from "../common/limits.js";
import { ok, ToolError } from "../common/result.js";
import { createServer, readOnlyTool } from "../common/server.js";
import { isoTime, parseWindow, within } from "../common/window.js";

const MAX_FILES_SCANNED = 500;
const MAX_FILE_BYTES = 20 * 1024 * 1024;
const TAIL_BYTES = 1024 * 1024;
const LEADING_TIME = /^\[?(\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:\.\d+)?Z?)/;

const isInside = (child: string, parent: string) => child === parent || child.startsWith(parent.endsWith(sep) ? parent : parent + sep);

/**
 * Confines a caller-supplied path to the allow-list. Symlinks and `..` are resolved first, so a link
 * pointing outside an allowed root is refused too. A missing path is checked through its parent folder.
 */
export async function confine(input: string, allowedRoots: string[]): Promise<string> {
  if (allowedRoots.length === 0) throw new ToolError("No folders are allowed. Add files.allowed_roots to the MCP config.");
  const roots = await Promise.all(allowedRoots.map((r) => realpath(r).catch(() => resolve(r))));
  // Relative paths are taken from the first allowed root.
  const candidate = isAbsolute(input) ? resolve(input) : resolve(roots[0], input);
  let real: string;
  try {
    real = await realpath(candidate);
  } catch {
    const parent = await realpath(dirname(candidate)).catch(() => null);
    if (!parent) throw new ToolError(`Path not found: ${input}`);
    real = join(parent, basename(candidate));
  }
  const root = roots.find((r) => isInside(real, r));
  if (!root) throw new ToolError(`Path is outside the allowed folders: ${input}`);
  return real;
}

/** Lists files under a folder, depth-first, without following symlinked folders. */
async function* walk(dir: string, budget = { left: MAX_FILES_SCANNED }): AsyncGenerator<string> {
  let entries;
  try {
    entries = await readdir(dir, { withFileTypes: true });
  } catch {
    return;
  }
  for (const e of entries.sort((a, b) => a.name.localeCompare(b.name))) {
    if (budget.left <= 0) return;
    const p = join(dir, e.name);
    if (e.isDirectory()) yield* walk(p, budget);
    else if (e.isFile()) {
      budget.left--;
      yield p;
    }
  }
}

async function isBinary(path: string) {
  const fh = await open(path, "r");
  try {
    const buf = Buffer.alloc(4096);
    const { bytesRead } = await fh.read(buf, 0, buf.length, 0);
    return buf.subarray(0, bytesRead).includes(0);
  } finally {
    await fh.close();
  }
}

export function buildFilesServer(cfg: McpConfig) {
  const server = createServer("resolveai-files");
  const roots = cfg.files.allowed_roots;
  const display = (p: string) => {
    const r = roots.map((x) => resolve(x)).find((x) => isInside(p, x));
    return r ? `${basename(r)}/${relative(r, p).split(sep).join("/")}`.replace(/\/$/, "") : basename(p);
  };

  readOnlyTool(server, "files_search",
    "Search text/log files under an allowed folder for lines containing a phrase (case-insensitive, plain text, not a regex). " +
    "Optionally keep only lines whose leading timestamp falls between from and to.",
    {
      root: z.string().describe("Folder to search, absolute or relative to the first allowed root"),
      phrase: z.string().min(2).max(200).describe("Plain text to look for, e.g. 'ERROR' or 'permission denied'"),
      from: isoTime.optional(),
      to: isoTime.optional(),
      max_matches: z.number().int().min(1).max(LIMITS.maxRows).default(50),
    },
    async ({ root, phrase, from, to, max_matches }) => {
      const dir = await confine(root, roots);
      const window = from ? parseWindow(from, to) : null;
      const needle = phrase.toLowerCase();
      const rows: Record<string, unknown>[] = [];
      let files = 0;
      let truncated = false;
      for await (const file of walk(dir)) {
        const s = await stat(file);
        if (s.size > MAX_FILE_BYTES || (await isBinary(file))) continue;
        files++;
        const lines = createInterface({ input: createReadStream(file, { encoding: "utf8" }), crlfDelay: Infinity });
        let n = 0;
        for await (const line of lines) {
          n++;
          if (!line.toLowerCase().includes(needle)) continue;
          const ts = LEADING_TIME.exec(line)?.[1]?.replace(" ", "T");
          if (window && !(ts && within(ts.endsWith("Z") ? ts : `${ts}Z`, window))) continue;
          if (rows.length >= max_matches) {
            truncated = true;
            break;
          }
          rows.push({ file: display(file), line: n, time: ts ?? null, text: clip(line.trim()) });
        }
        lines.close();
        if (truncated) break;
      }
      const summary = rows.length
        ? `${rows.length}${truncated ? "+" : ""} line(s) containing "${phrase}" in ${display(dir)} (${files} file(s) scanned).`
        : `No lines containing "${phrase}" in ${display(dir)} (${files} file(s) scanned).`;
      return ok(summary, rows, display(dir), truncated);
    });

  readOnlyTool(server, "files_tail",
    "Read the last lines of one text/log file inside an allowed folder.",
    { path: z.string(), lines: z.number().int().min(1).max(LIMITS.maxRows).default(50) },
    async ({ path, lines }) => {
      const file = await confine(path, roots);
      const s = await stat(file).catch(() => null);
      if (!s?.isFile()) throw new ToolError(`Not a file: ${path}`);
      if (await isBinary(file)) throw new ToolError(`Not a text file: ${path}`);
      const fh = await open(file, "r");
      let text: string;
      try {
        const start = Math.max(0, s.size - TAIL_BYTES);
        const buf = Buffer.alloc(s.size - start);
        await fh.read(buf, 0, buf.length, start);
        text = buf.toString("utf8");
      } finally {
        await fh.close();
      }
      const all = text.split(/\r?\n/).filter((l, i, a) => l.length > 0 || i < a.length - 1);
      const tail = all.slice(-lines);
      const firstLine = all.length - tail.length + 1;
      return ok(`Last ${tail.length} line(s) of ${display(file)} (${s.size.toLocaleString("en-US")} bytes, modified ${s.mtime.toISOString()}).`,
        tail.map((t, i) => ({ line: firstLine + i, text: clip(t) })), display(file));
    });

  readOnlyTool(server, "files_recent_changes",
    "List files under an allowed folder that were modified at or after a time, newest first.",
    { root: z.string(), since: isoTime },
    async ({ root, since }) => {
      const dir = await confine(root, roots);
      const t = Date.parse(since);
      if (Number.isNaN(t)) throw new ToolError("since must be an ISO-8601 time.");
      const rows: { file: string; size_bytes: number; modified: string }[] = [];
      for await (const file of walk(dir)) {
        const s = await stat(file);
        if (s.mtimeMs >= t) rows.push({ file: display(file), size_bytes: s.size, modified: s.mtime.toISOString() });
      }
      rows.sort((a, b) => b.modified.localeCompare(a.modified));
      return ok(`${rows.length} file(s) in ${display(dir)} changed since ${since}.`, rows, display(dir));
    });

  readOnlyTool(server, "files_disk_space",
    "Free and used space on the drive that holds an allowed folder.",
    { root: z.string() },
    async ({ root }) => {
      const dir = await confine(root, roots);
      const s = await statfs(dir);
      const total = s.blocks * s.bsize;
      const free = s.bavail * s.bsize;
      const usedPct = total ? Math.round(((total - free) / total) * 1000) / 10 : 0;
      const gb = (b: number) => Math.round((b / 1024 ** 3) * 10) / 10;
      return ok(`Drive holding ${display(dir)} is ${usedPct}% used (${gb(free)} GB free of ${gb(total)} GB).`,
        [{ folder: display(dir), total_gb: gb(total), free_gb: gb(free), used_percent: usedPct }], display(dir));
    });

  readOnlyTool(server, "files_check",
    "Check one file or folder inside an allowed folder: whether it exists, its size and modified time, and whether it can be read and written.",
    { path: z.string() },
    async ({ path }) => {
      const p = await confine(path, roots);
      const s = await stat(p).catch(() => null);
      if (!s) return ok(`${display(p)} does not exist.`, [{ path: display(p), exists: false }], display(p));
      const can = (mode: number) => access(p, mode).then(() => true, (e: NodeJS.ErrnoException) => e.code ?? false);
      const readable = await can(constants.R_OK);
      const writable = await can(constants.W_OK);
      let openError: string | null = null;
      if (s.isFile()) await open(p, "r").then((fh) => fh.close(), (e: NodeJS.ErrnoException) => (openError = e.code ?? "error"));
      const row = {
        path: display(p), exists: true, type: s.isDirectory() ? "folder" : "file", size_bytes: s.size,
        modified: s.mtime.toISOString(), readable: readable === true, writable: writable === true,
        problem: openError ?? (readable !== true ? String(readable) : writable !== true ? `not writable (${writable})` : null),
      };
      return ok(`${display(p)} exists (${row.type}, ${s.size.toLocaleString("en-US")} bytes)${row.problem ? `; problem: ${row.problem}` : ""}.`, [row], display(p));
    });

  return server;
}
