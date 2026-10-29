import { execFileSync } from "node:child_process";
import { mkdirSync, mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { beforeAll, describe, expect, it } from "vitest";
import { buildDeploysServer } from "../src/deploys/tools.js";
import { buildMetricsServer } from "../src/metrics/tools.js";
import { config, connect } from "./helpers.js";

let dir: string;
let commit: string;

beforeAll(() => {
  dir = mkdtempSync(join(tmpdir(), "resolveai-deploys-"));
  const repo = join(dir, "repo");
  mkdirSync(repo);
  const git = (args: string[], date?: string) => execFileSync("git", args, {
    cwd: repo, encoding: "utf8",
    env: { ...process.env, GIT_AUTHOR_NAME: "dev", GIT_AUTHOR_EMAIL: "dev@example.test", GIT_COMMITTER_NAME: "dev", GIT_COMMITTER_EMAIL: "dev@example.test",
      ...(date ? { GIT_AUTHOR_DATE: date, GIT_COMMITTER_DATE: date } : {}) },
  });
  git(["init", "-q"]);
  writeFileSync(join(repo, "pool.ts"), "export const max = 100;\n");
  git(["add", "."]);
  git(["commit", "-qm", "Initial pool config"], "2026-10-26T09:00:00Z");
  writeFileSync(join(repo, "pool.ts"), "export const max = 20;\n");
  git(["commit", "-qam", "Lower pool max"], "2026-10-27T09:55:00Z");
  commit = git(["rev-parse", "--short", "HEAD"]).trim();
  git(["tag", "v2.8.1"]);

  writeFileSync(join(dir, "feed.json"), JSON.stringify([
    { service: "payment-api", version: "v2.8.1", deployed_at: "2026-10-27T10:00:00Z", change_summary: "Pool settings moved", commit: "7d03b1f",
      files_changed: [{ path: "db-client.yaml", added: 4, removed: 0 }], patch: "+pool:\n+  max: 20\n" },
    { service: "order-api", version: "v4.2.0", deployed_at: "2026-10-27T13:30:00Z", change_summary: "Hourly reconciliation", commit: "c90e4a7" },
  ]));
  mkdirSync(join(dir, "metrics"));
  writeFileSync(join(dir, "metrics", "payment-api.json"), JSON.stringify({
    "HTTP 500 rate": { unit: "%", points: [["2026-10-27T10:00:00Z", 0.2], ["2026-10-27T10:05:00Z", 28], ["2026-10-27T10:10:00Z", 27]] },
  }));
});

describe("deploys server", () => {
  const server = () => buildDeploysServer(config({
    services: { "payment-api": { repo: join(dir, "repo") }, "order-api": {} },
    deploys: { feed: join(dir, "feed.json") },
  }));

  it("merges the deploy feed with git commits in the window, newest first", async () => {
    const { call } = await connect(server());
    const r = await call("deploys_recent", { service: "payment-api", from: "2026-10-27T08:00:00Z", to: "2026-10-27T11:00:00Z" });
    expect(r.rows.map((x) => [x.kind, x.time])).toEqual([["deployment", "2026-10-27T10:00:00Z"], ["commit", "2026-10-27T09:55:00.000Z"]]);
    expect(r.rows[1]).toMatchObject({ summary: "Lower pool max", version: "v2.8.1" });
    const everyone = await call("deploys_recent", { from: "2026-10-27T08:00:00Z", to: "2026-10-27T20:00:00Z" });
    expect(everyone.rows.map((x) => x.service)).toContain("order-api");
  });

  it("summarises a change from the feed or from git, and rejects bad input", async () => {
    const { call } = await connect(server());
    const fromFeed = await call("deploys_change_summary", { service: "payment-api", commit: "7d03b1f" });
    expect(fromFeed.rows[0]).toMatchObject({ path: "db-client.yaml" });
    const fromGit = await call("deploys_change_summary", { service: "payment-api", commit });
    expect(fromGit.rows[0]).toMatchObject({ path: "pool.ts", added: 1, removed: 1 });
    expect(String(fromGit.rows.at(-1)!.patch)).toContain("+export const max = 20;");
    expect((await call("deploys_change_summary", { service: "payment-api", commit: "--output=/tmp/x" })).isError).toBe(true);
    expect((await call("deploys_recent", { service: "nope", from: "2026-10-27T08:00:00Z", to: "2026-10-27T09:00:00Z" })).summary).toMatch(/Unknown service/);
    expect((await call("deploys_recent", { service: "payment-api", from: "2026-10-20T00:00:00Z", to: "2026-10-27T00:00:00Z" })).summary).toMatch(/24 hours/);
  });
});

describe("metrics server", () => {
  it("returns a window of points with baseline and peak", async () => {
    const { call } = await connect(buildMetricsServer(config({ metrics: { series_dir: join(dir, "metrics") } })));
    const r = await call("metrics_series", { service: "payment-api", name: "http 500 rate", from: "2026-10-27T09:59:00Z", to: "2026-10-27T10:20:00Z" });
    expect(r.summary).toBe("payment-api HTTP 500 rate: baseline 0.2 %, peak 28 % at 2026-10-27T10:05:00Z, average 18.4 % over 3 point(s).");
    expect((await call("metrics_series", { service: "../etc", name: "x", from: "2026-10-27T09:59:00Z" })).isError).toBe(true);
    expect((await call("metrics_list", { service: "payment-api" })).rows).toHaveLength(1);
    const host = await call("metrics_host");
    expect(Number(host.rows[0].cpu_cores)).toBeGreaterThan(0);
  });
});
