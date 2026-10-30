import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { defineConfig } from "@playwright/test";

// Runs in recorded LLM mode. By default it starts its own dev servers on a temporary database, because the test
// calls /api/demo/reset; it never reuses running servers, which could be holding real data. Set BASE_URL
// (e.g. http://localhost:8787 for the Docker container) to test a server you know is safe to reset.
const baseURL = process.env.BASE_URL || "http://localhost:5173";

export default defineConfig({
  testDir: "e2e",
  timeout: 60_000,
  use: { baseURL, trace: "retain-on-failure" },
  webServer: process.env.BASE_URL
    ? undefined
    : {
        command: "npm run dev",
        url: baseURL,
        reuseExistingServer: false,
        timeout: 60_000,
        env: { DB_PATH: join(mkdtempSync(join(tmpdir(), "resolveai-e2e-")), "e2e.db") },
      },
});
