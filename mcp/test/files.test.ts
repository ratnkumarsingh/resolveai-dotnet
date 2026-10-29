import { mkdirSync, mkdtempSync, symlinkSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { beforeAll, describe, expect, it } from "vitest";
import { buildFilesServer } from "../src/files/tools.js";
import { config, connect } from "./helpers.js";

let base: string;
let allowed: string;
let call: Awaited<ReturnType<typeof connect>>["call"];

beforeAll(async () => {
  base = mkdtempSync(join(tmpdir(), "resolveai-files-"));
  allowed = join(base, "logs");
  mkdirSync(join(allowed, "svc"), { recursive: true });
  mkdirSync(join(base, "secret"));
  writeFileSync(join(base, "secret", "passwords.txt"), "ERROR root password hunter2\n");
  writeFileSync(join(allowed, "svc", "app.log"), [
    "2026-10-27T10:00:00Z INFO started",
    "2026-10-27T10:04:00Z ERROR EACCES: permission denied, open '/mnt/a.pdf'",
    "2026-10-27T12:00:00Z ERROR disk quota exceeded",
    "no timestamp ERROR here",
  ].join("\n"));
  writeFileSync(join(allowed, "svc", "blob.bin"), Buffer.from([0, 1, 2, 69, 82, 82, 79, 82]));
  // A directory junction that points outside the allowed root.
  symlinkSync(join(base, "secret"), join(allowed, "escape"), "junction");
  ({ call } = await connect(buildFilesServer(config({ files: { allowed_roots: [allowed] } }))));
});

describe("files server", () => {
  it("finds matching lines and filters them by the leading timestamp", async () => {
    const all = await call("files_search", { root: allowed, phrase: "error" });
    expect(all.isError).toBe(false);
    expect(all.rows.map((r) => r.line)).toEqual([2, 3, 4]);
    expect(all.rows[0]).toMatchObject({ file: "logs/svc/app.log", time: "2026-10-27T10:04:00Z" });

    const windowed = await call("files_search", { root: "svc", phrase: "ERROR", from: "2026-10-27T09:30:00Z", to: "2026-10-27T11:00:00Z" });
    expect(windowed.rows).toHaveLength(1);
    expect(windowed.rows[0].text).toContain("permission denied");
  });

  it("refuses paths outside the allowed folders, including through a junction", async () => {
    for (const root of [join(base, "secret"), "../secret", join(allowed, "escape")]) {
      const r = await call("files_search", { root, phrase: "password" });
      expect(r.isError, root).toBe(true);
      expect(r.summary).toMatch(/outside the allowed folders/);
    }
    expect((await call("files_tail", { path: join(allowed, "escape", "passwords.txt") })).isError).toBe(true);
  });

  it("skips binary files and caps matches", async () => {
    const r = await call("files_search", { root: allowed, phrase: "error", max_matches: 1 });
    expect(r.rows).toHaveLength(1);
    expect(r.truncated).toBe(true);
    expect((await call("files_tail", { path: "svc/blob.bin" })).summary).toMatch(/Not a text file/);
  });

  it("tails, checks and reports space", async () => {
    const tail = await call("files_tail", { path: "svc/app.log", lines: 2 });
    expect(tail.rows.map((r) => r.line)).toEqual([3, 4]);
    const check = await call("files_check", { path: "svc/app.log" });
    expect(check.rows[0]).toMatchObject({ exists: true, type: "file", readable: true });
    expect((await call("files_check", { path: "svc/missing.log" })).rows[0]).toMatchObject({ exists: false });
    const space = await call("files_disk_space", { root: allowed });
    expect(Number(space.rows[0].total_gb)).toBeGreaterThan(0);
    const recent = await call("files_recent_changes", { root: allowed, since: "2000-01-01T00:00:00Z" });
    expect(recent.rows.map((r) => r.file)).toContain("logs/svc/app.log");
  });

  it("marks every tool read-only", async () => {
    const { client } = await connect(buildFilesServer(config({ files: { allowed_roots: [allowed] } })));
    const { tools } = await client.listTools();
    expect(tools.map((t) => t.name).sort()).toEqual(["files_check", "files_disk_space", "files_recent_changes", "files_search", "files_tail"]);
    expect(tools.every((t) => t.annotations?.readOnlyHint === true)).toBe(true);
  });
});
