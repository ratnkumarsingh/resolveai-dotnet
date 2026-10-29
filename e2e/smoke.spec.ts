import { expect, test } from "@playwright/test";

test("incident → investigation → evidence → similar incident → RCA draft", async ({ page, request }) => {
  // Start from a known dataset.
  expect((await request.post("/api/demo/reset")).ok()).toBe(true);

  await page.goto("/");
  await expect(page.getByText("Data source: Demo dataset")).toBeVisible();
  const row = page.getByRole("row", { name: /INC-1042/ });
  await row.getByRole("link", { name: "Open incident" }).click();
  await expect(page.getByRole("heading", { name: "Payment API elevated 500 errors" })).toBeVisible();

  // Records come from the database, not decorative UI.
  await expect(page.getByText("HTTP 500 rate")).toBeVisible();
  await page.getByRole("tab", { name: /Deployments/ }).click();
  await expect(page.getByText("payment-api v2.8.1")).toBeVisible();

  // Run the investigation and watch the loading state.
  await page.getByTestId("run-investigation").click();
  await expect(page.getByTestId("investigation-running")).toBeVisible();
  await expect(page.getByTestId("investigation-results")).toBeVisible({ timeout: 20_000 });
  const hypotheses = page.getByTestId("hypothesis");
  expect(await hypotheses.count()).toBeGreaterThanOrEqual(2);
  await expect(hypotheses.first()).toContainText(/pool/i);
  await expect(page.getByText(/not confirmed/).first()).toBeVisible();

  // The audit trail lists every pipeline stage.
  const trace = page.getByTestId("trace-panel");
  await trace.locator("summary").click();
  await expect(trace).toContainText("Build evidence packet");
  await expect(trace).toContainText(/redactions: [1-9]/);
  await expect(trace).toContainText("Baseline checks (MCP)");

  // The systems were checked: citations carry their system, and every call is listed with its result.
  await expect(page.getByTestId("systems-strip")).toContainText("Files & logs");
  await expect(hypotheses.first().getByTestId("system-citation").first()).toBeVisible();
  await page.getByRole("tab", { name: /System checks/ }).click();
  await expect(page.getByTestId("tool-result").first()).toContainText("files_search");
  await expect(page.getByTestId("tool-results")).toContainText("Follow-up");
  await page.getByRole("tab", { name: "AI findings" }).click();

  // Evidence links jump to the source record.
  await hypotheses.first().getByTestId("evidence-link").filter({ hasText: "pool.max=20" }).click();
  await expect(page.getByRole("tab", { name: /Logs/ })).toHaveAttribute("aria-selected", "true");
  await expect(page.getByRole("cell", { name: /pool\.max=20/ })).toBeVisible();

  // Similar incident with its historical RCA.
  await page.getByRole("tab", { name: "Similar incidents" }).click();
  const similar = page.getByTestId("similar-incident").first();
  await expect(similar).toContainText("INC-0881");
  await similar.getByRole("button", { name: "View historical RCA" }).click();
  await expect(similar).toContainText("RCA: INC-0881");

  // Generate, edit, save and reload the RCA.
  await page.getByRole("tab", { name: "RCA" }).click();
  await page.getByTestId("generate-rca").click();
  const editor = page.getByTestId("rca-editor");
  await expect(editor).toContainText(/draft — review required/);
  const rootCause = page.getByTestId("rca-root-cause");
  await rootCause.fill("Verified: v2.8.1 set pool.max=20 in db-client.yaml (previously 100).");
  await page.getByRole("button", { name: "Save changes" }).click();
  await expect(editor).toContainText("Saved.");

  await page.reload();
  await page.getByRole("tab", { name: /RCA/ }).click();
  await expect(page.getByTestId("rca-root-cause")).toHaveValue(/previously 100/);

  // Export downloads Markdown containing the edit.
  const [download] = await Promise.all([page.waitForEvent("download"), page.getByRole("link", { name: "Download .md" }).click()]);
  expect(download.suggestedFilename()).toBe("INC-1042-rca-v1.md");
  const body = await (await request.get(`/api/rca-drafts/${(await (await request.get("/api/incidents/INC-1042")).json()).rca_drafts[0].id}/export.md`)).text();
  expect(body).toContain("previously 100");

  // The incident waits in the review queue until a person verifies it.
  await page.goto("/");
  await page.getByTestId("needs-review").click();
  await expect(page.getByRole("row", { name: /INC-1042/ })).toContainText("Needs review");
  await expect(page.getByRole("row", { name: /INC-1043/ })).toHaveCount(0);

  // Each system has its own MCP server and status.
  await page.getByRole("link", { name: "Systems" }).click();
  await expect(page.getByTestId("system-files")).toContainText("Connected");
  await expect(page.getByTestId("system-database")).toContainText(/Turned off|Connected/);
});
