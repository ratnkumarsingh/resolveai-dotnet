using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace ResolveAI.E2E;

[Collection("app")]
public sealed class SmokeTests(AppFixture app)
{
    private async Task<IPage> NewPage(int width = 1280)
    {
        var context = await app.Browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = app.BaseUrl, ViewportSize = new ViewportSize { Width = width, Height = 900 }, AcceptDownloads = true });
        return await context.NewPageAsync();
    }

    [Fact]
    public async Task Incident_to_investigation_to_evidence_to_similar_incident_to_RCA_draft()
    {
        // Start from a known dataset.
        Assert.True((await app.Http.PostAsync("/api/demo/reset", null)).IsSuccessStatusCode);

        var page = await NewPage();
        await page.GotoAsync("/");
        await Expect(page.GetByText("Data source: Demo dataset")).ToBeVisibleAsync();
        var row = page.GetByRole(AriaRole.Row, new() { NameRegex = new Regex("INC-1042") });
        await row.GetByRole(AriaRole.Link, new() { Name = "Open incident" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Payment API elevated 500 errors" })).ToBeVisibleAsync();

        // Records come from the database, not decorative UI.
        await Expect(page.GetByText("HTTP 500 rate")).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Tab, new() { NameRegex = new Regex("Deployments") }).ClickAsync();
        await Expect(page.GetByText("payment-api v2.8.1")).ToBeVisibleAsync();

        // Run the investigation and watch the loading state.
        await page.GetByTestId("run-investigation").ClickAsync();
        await Expect(page.GetByTestId("investigation-running")).ToBeVisibleAsync();
        await Expect(page.GetByTestId("investigation-results")).ToBeVisibleAsync(new() { Timeout = 20_000 });
        var hypotheses = page.GetByTestId("hypothesis");
        Assert.True(await hypotheses.CountAsync() >= 2);
        await Expect(hypotheses.First).ToContainTextAsync(new Regex("pool", RegexOptions.IgnoreCase));
        await Expect(page.GetByText(new Regex("not confirmed")).First).ToBeVisibleAsync();

        // The audit trail lists every pipeline stage.
        var trace = page.GetByTestId("trace-panel");
        await trace.Locator("summary").ClickAsync();
        await Expect(trace).ToContainTextAsync("Build evidence packet");
        await Expect(trace).ToContainTextAsync(new Regex("redactions: [1-9]"));
        await Expect(trace).ToContainTextAsync("Baseline checks (MCP)");

        // The systems were checked: citations carry their system, and every call is listed with its result.
        await Expect(page.GetByTestId("systems-strip")).ToContainTextAsync("Files & logs");
        await Expect(hypotheses.First.GetByTestId("system-citation").First).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Tab, new() { NameRegex = new Regex("System checks") }).ClickAsync();
        await Expect(page.GetByTestId("tool-result").First).ToContainTextAsync(new Regex("files_search|db_"));
        await Expect(page.GetByTestId("tool-results")).ToContainTextAsync("Follow-up");
        await page.GetByRole(AriaRole.Tab, new() { Name = "AI findings" }).ClickAsync();

        // Evidence links jump to the source record.
        await hypotheses.First.GetByTestId("evidence-link").Filter(new() { HasText = "pool.max=20" }).First.ClickAsync();
        await Expect(page.GetByRole(AriaRole.Tab, new() { NameRegex = new Regex("Logs") })).ToHaveAttributeAsync("aria-selected", "true");
        await Expect(page.GetByRole(AriaRole.Cell, new() { NameRegex = new Regex(@"pool\.max=20") })).ToBeVisibleAsync();

        // Similar incident with its historical RCA.
        await page.GetByRole(AriaRole.Tab, new() { Name = "Similar incidents" }).ClickAsync();
        var similar = page.GetByTestId("similar-incident").First;
        await Expect(similar).ToContainTextAsync("INC-0881");
        await similar.GetByRole(AriaRole.Button, new() { Name = "View historical RCA" }).ClickAsync();
        await Expect(similar).ToContainTextAsync("RCA: INC-0881");

        // Generate, edit, save and reload the RCA.
        await page.GetByRole(AriaRole.Tab, new() { NameRegex = new Regex("RCA") }).ClickAsync();
        await page.GetByTestId("generate-rca").ClickAsync();
        var editor = page.GetByTestId("rca-editor");
        await Expect(editor).ToContainTextAsync(new Regex("draft — review required"));
        var rootCause = page.GetByTestId("rca-root-cause");
        await rootCause.FillAsync("Verified: v2.8.1 set pool.max=20 in db-client.yaml (previously 100).");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save changes" }).ClickAsync();
        await Expect(editor).ToContainTextAsync("Saved.");

        await page.ReloadAsync();
        await page.GetByRole(AriaRole.Tab, new() { NameRegex = new Regex("RCA") }).ClickAsync();
        await Expect(page.GetByTestId("rca-root-cause")).ToHaveValueAsync(new Regex("previously 100"));

        // Export downloads Markdown containing the edit.
        var download = await page.RunAndWaitForDownloadAsync(() => page.GetByRole(AriaRole.Link, new() { Name = "Download .md" }).ClickAsync());
        Assert.Equal("INC-1042-rca-v1.md", download.SuggestedFilename);
        Assert.Contains("previously 100", await File.ReadAllTextAsync((await download.PathAsync())!));

        // The incident waits in the review queue until a person verifies it.
        await page.GotoAsync("/");
        await page.GetByTestId("needs-review").ClickAsync();
        await Expect(page.GetByRole(AriaRole.Row, new() { NameRegex = new Regex("INC-1042") })).ToContainTextAsync("Needs review");
        await Expect(page.GetByRole(AriaRole.Row, new() { NameRegex = new Regex("INC-1043") })).ToHaveCountAsync(0);

        // Each system has its own MCP server and status.
        await page.GetByRole(AriaRole.Link, new() { Name = "Systems" }).ClickAsync();
        await Expect(page.GetByTestId("system-files")).ToContainTextAsync("Connected");
        await Expect(page.GetByTestId("system-database")).ToContainTextAsync(new Regex("Turned off|Connected"));
    }

    [Fact]
    public async Task A_run_shows_its_progress_then_opens_on_the_leading_hypothesis_and_next_actions()
    {
        Assert.True((await app.Http.PostAsync("/api/demo/reset", null)).IsSuccessStatusCode);
        var page = await NewPage();
        await page.GotoAsync("/incidents/INC-1042");
        await Expect(page.GetByTestId("incident-description")).ToBeVisibleAsync();

        await page.GetByTestId("run-investigation").ClickAsync();
        await Expect(page.GetByTestId("investigation-steps")).ToContainTextAsync("Analyse the evidence");
        await Expect(page.GetByTestId("investigation-results")).ToBeVisibleAsync(new() { Timeout = 20_000 });

        // The headline, its proof and an action are all on the first screen, and it says it isn't confirmed.
        var hero = page.GetByTestId("leading-hypothesis");
        await Expect(hero).ToContainTextAsync(new Regex("pool", RegexOptions.IgnoreCase));
        await Expect(hero).ToContainTextAsync("not confirmed");
        await Expect(hero).ToContainTextAsync("nothing is executed");
        await Expect(hero.GetByRole(AriaRole.Button, new() { Name = "Draft RCA" })).ToBeInViewportAsync();
        await Expect(page.GetByTestId("leading-evidence").First).ToBeInViewportAsync();

        // The header stepped aside; Details brings back the description and the earlier runs.
        await Expect(page.GetByTestId("incident-description")).ToHaveCountAsync(0);
        await page.GetByTestId("incident-details-toggle").ClickAsync();
        await Expect(page.GetByTestId("incident-description")).ToBeVisibleAsync();
        await Expect(page.GetByTestId("prior-runs")).ToBeVisibleAsync();
        await page.GetByTestId("incident-details-toggle").ClickAsync();

        // Each action goes where it says.
        await hero.GetByRole(AriaRole.Button, new() { Name = "Record what you found" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Textbox, new() { Name = "Verified root cause" })).ToBeInViewportAsync();
        await page.EvaluateAsync("() => window.scrollTo(0, 0)");
        await page.GetByTestId("leading-evidence").First.ClickAsync();
        await Expect(page.GetByRole(AriaRole.Tab, new() { Name = "AI findings" })).ToHaveAttributeAsync("aria-selected", "false");
        await page.GetByRole(AriaRole.Tab, new() { Name = "AI findings" }).ClickAsync();
        await hero.GetByRole(AriaRole.Button, new() { Name = "Draft RCA" }).ClickAsync();
        await Expect(page.GetByTestId("generate-rca")).ToBeVisibleAsync();

        // Coming back opens on the findings.
        await page.ReloadAsync();
        await Expect(hero).ToBeVisibleAsync();

        // On a phone the headline is on the first screen and the finished page doesn't scroll sideways.
        var phone = await NewPage(390);
        await phone.GotoAsync("/incidents/INC-1042");
        await Expect(phone.GetByTestId("leading-hypothesis").Locator("h2")).ToBeInViewportAsync();
        Assert.True(await phone.EvaluateAsync<int>("() => document.documentElement.scrollWidth - window.innerWidth") <= 0);
    }

    [Fact]
    public async Task The_leading_hypothesis_never_overstates_weak_evidence()
    {
        Assert.True((await app.Http.PostAsync("/api/demo/reset", null)).IsSuccessStatusCode);
        var page = await NewPage();
        await page.GotoAsync("/incidents/INC-1042");
        await page.GetByTestId("run-investigation").ClickAsync();
        var hero = page.GetByTestId("leading-hypothesis");
        await Expect(hero).ToBeVisibleAsync(new() { Timeout = 20_000 });

        app.Deps.Db.Run("UPDATE hypotheses SET speculative = 1 WHERE rank = 1");
        await page.ReloadAsync();
        await Expect(page.GetByTestId("leading-speculative")).ToBeVisibleAsync();
        await Expect(hero).Not.ToContainTextAsync("high confidence");

        app.Deps.Db.Run("UPDATE investigations SET insufficient_evidence = 1");
        await page.ReloadAsync();
        await Expect(page.GetByTestId("insufficient-evidence")).ToBeVisibleAsync();
        await Expect(hero).ToHaveCountAsync(0);

        Assert.True((await app.Http.PostAsync("/api/demo/reset", null)).IsSuccessStatusCode);
    }

    [Fact]
    public async Task Filters_and_row_clicks_work_on_the_queue()
    {
        var page = await NewPage();
        await page.GotoAsync("/");
        await page.GetByLabel("Severity").SelectOptionAsync("SEV-2");
        await Expect(page.GetByRole(AriaRole.Row, new() { NameRegex = new Regex("INC-1045") })).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Row, new() { NameRegex = new Regex("INC-1046") })).ToHaveCountAsync(0);
        await page.GetByLabel("Severity").SelectOptionAsync("");
        await page.GetByLabel("Search incidents").FillAsync("pdf");
        await Expect(page.GetByText("1 active · 1 shown")).ToBeVisibleAsync();
        await page.GetByText("Invoice PDF uploads failing").ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Invoice PDF uploads failing" })).ToBeVisibleAsync();
        await page.GotoAsync("/not-a-page");
        await Expect(page.GetByText("Page not found.")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Log_text_containing_HTML_is_shown_as_text_and_never_runs()
    {
        var incident = app.Deps.Db.Get<string>("SELECT id FROM incidents WHERE key = 'INC-1045'");
        var id = Guid.NewGuid().ToString();
        const string attack = "<img src=x onerror=\"window.__xss=1\"><script>window.__xss=1</script>";
        app.Deps.Db.Run(
            "INSERT INTO log_events (id, incident_id, timestamp, service_name, level, message, error_signature, trace_id, source) VALUES (@id, @incident, '2026-10-27T11:22:00Z', 'document-service', 'ERROR', @attack, NULL, NULL, 'app')",
            new { id, incident, attack });
        try
        {
            var page = await NewPage();
            await page.GotoAsync("/incidents/INC-1045");
            await page.GetByRole(AriaRole.Tab, new() { NameRegex = new Regex("Logs") }).ClickAsync();
            await Expect(page.GetByText("<img src=x", new() { Exact = false })).ToBeVisibleAsync();
            Assert.Equal(0, await page.Locator("tbody img").CountAsync());
            await page.WaitForTimeoutAsync(300);
            Assert.False(await page.EvaluateAsync<bool>("() => window.__xss === 1"));
        }
        finally
        {
            app.Deps.Db.Run("DELETE FROM log_events WHERE id = @id", new { id });
        }
    }

    [Fact]
    public async Task The_access_code_is_kept_only_in_this_browser_and_not_readable_from_its_storage()
    {
        var page = await NewPage();
        await page.GotoAsync("/settings");
        await Expect(page.GetByText("No code saved.")).ToBeVisibleAsync();
        await page.GetByLabel("Access code").FillAsync(AppFixture.AccessCode);
        await page.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
        await Expect(page.GetByText("Saved in this browser.")).ToBeVisibleAsync();

        var stored = await page.EvaluateAsync<string>("() => JSON.stringify(localStorage)");
        Assert.Contains("resolveai.accessCode", stored);
        Assert.DoesNotContain(AppFixture.AccessCode, stored);

        await page.ReloadAsync();
        await Expect(page.GetByText("Saved in this browser.")).ToBeVisibleAsync();

        // Another browser profile starts with nothing.
        var other = await NewPage();
        await other.GotoAsync("/settings");
        await Expect(other.GetByText("No code saved.")).ToBeVisibleAsync();

        await page.GetByRole(AriaRole.Button, new() { Name = "Forget" }).ClickAsync();
        await Expect(page.GetByText("No code saved.")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Demo_reset_from_the_settings_page_asks_first_and_restores_the_data()
    {
        var page = await NewPage();
        await page.GotoAsync("/settings");
        await page.GetByRole(AriaRole.Button, new() { Name = "Reset demo data…" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Reset demo data…" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Yes, reset everything" }).ClickAsync();
        await Expect(page.GetByText("Demo data restored.")).ToBeVisibleAsync();
    }

    [Theory]
    [InlineData(1280)]
    [InlineData(390)]
    public async Task No_page_scrolls_sideways_and_the_Open_incident_button_stays_on_one_line(int width)
    {
        var page = await NewPage(width);
        foreach (var path in new[] { "/", "/systems", "/settings", "/incidents/INC-1042" })
        {
            await page.GotoAsync(path);
            await Expect(page.Locator("header").First).ToBeVisibleAsync();
            Assert.True(await page.EvaluateAsync<int>("() => document.documentElement.scrollWidth - window.innerWidth") <= 0, $"{path} overflows at {width}px");
        }
        await page.GotoAsync("/");
        var button = page.GetByRole(AriaRole.Link, new() { Name = "Open incident" }).First;
        await Expect(button).ToBeVisibleAsync();
        Assert.True((await button.BoundingBoxAsync())!.Height < 30);
    }

    [Fact]
    public async Task A_browser_page_on_another_site_cannot_open_a_circuit()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/_blazor/negotiate?negotiateVersion=1");
        request.Headers.Add("Origin", "https://evil.example");
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await app.Http.SendAsync(request)).StatusCode);

        using var same = new HttpRequestMessage(HttpMethod.Post, "/_blazor/negotiate?negotiateVersion=1");
        same.Headers.Add("Origin", app.BaseUrl);
        Assert.NotEqual(System.Net.HttpStatusCode.Forbidden, (await app.Http.SendAsync(same)).StatusCode);

        var page = await NewPage();
        await page.GotoAsync("/");
        await Expect(page.GetByText("Incident queue")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task The_API_still_answers_with_the_same_JSON_next_to_the_UI()
    {
        var incidents = JsonNode.Parse(await app.Http.GetStringAsync("/api/incidents"))!;
        Assert.Equal(6, incidents["total"]!.GetValue<int>());
    }
}
