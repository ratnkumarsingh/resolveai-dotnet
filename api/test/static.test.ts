import { mkdirSync, mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { makeApp } from "./helpers.js";
import { RecordedProvider } from "../src/llm/recorded.js";

describe("serving the built web app", () => {
  it("serves files, falls back to index.html for pages, and keeps API and asset misses as 404", async () => {
    const dist = mkdtempSync(join(tmpdir(), "resolveai-dist-"));
    mkdirSync(join(dist, "assets"));
    writeFileSync(join(dist, "index.html"), "<!doctype html><title>ResolveAI</title>");
    writeFileSync(join(dist, "assets", "app.js"), "console.log(1)");
    const { app } = makeApp(new RecordedProvider(0), { webDist: dist });

    const page = await app.inject("/incidents/INC-1042");
    expect(page.statusCode).toBe(200);
    expect(page.body).toContain("<title>ResolveAI</title>");
    expect((await app.inject("/assets/app.js")).body).toBe("console.log(1)");
    expect((await app.inject("/assets/missing.js")).statusCode).toBe(404);
    expect((await app.inject("/api/nope")).json()).toEqual({ error: "Not found" });
    expect((await app.inject("/api/incidents")).json().total).toBe(6);
  });
});
