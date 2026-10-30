using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using ResolveAI.App.Llm;
using ResolveAI.App.Pipeline;
using ResolveAI.Contracts;

namespace ResolveAI.Tests;

/// <summary>Compares the .NET app against what the Node app recorded, route by route.</summary>
public class ApiGoldenTests
{
    public static IEnumerable<object[]> RouteCases() =>
        Enumerable.Range(0, Golden.Load("routes").AsArray().Count).Select(i => new object[] { i });

    [Theory]
    [MemberData(nameof(RouteCases))]
    public async Task Routes_match_the_Node_app_on_a_fresh_seed(int index)
    {
        var expected = Golden.Load("routes").AsArray()[index]!;
        var url = expected["url"]!.GetValue<string>();
        await using var app = new TestApp(systems: await Hubs.Demo());
        var res = await app.Http.GetAsync(url);
        Assert.Equal(expected["status"]!.GetValue<int>(), (int)res.StatusCode);
        Golden.AssertSame(expected["body"], Golden.Normalise(JsonNode.Parse(await res.Content.ReadAsStringAsync())), url);
    }

    public static IEnumerable<object[]> RunCases() =>
        Golden.Load("investigations").AsArray().Select(r => new object[] { r!["name"]!.GetValue<string>() });

    [Theory]
    [MemberData(nameof(RunCases))]
    public async Task Investigations_packets_and_RCAs_match_the_Node_app(string name)
    {
        var expected = Golden.Load("investigations").AsArray().First(r => r!["name"]!.GetValue<string>() == name)!;
        var key = name.Replace("-sql-on", "");
        var systems = await Hubs.Demo(name.EndsWith("sql-on") ? McpFixtures.FakeSql : null);
        await using var app = new TestApp(systems: systems);

        var inv = await app.Run(key);
        var id = inv["id"]!.GetValue<string>();
        Golden.AssertSame(expected["investigation"], Golden.Normalise(inv), $"{name}.investigation");

        var packet = app.Db.Get<string>("SELECT evidence_packet FROM investigations WHERE id = @id", new { id })!;
        Golden.AssertSame(expected["packet"], Golden.Normalise(JsonNode.Parse(packet)), $"{name}.packet");

        var created = await app.Post($"/api/incidents/{key}/rca-drafts", new { investigation_id = id });
        Assert.Equal(expected["rca"]!["status"]!.GetValue<int>(), (int)created.StatusCode);
        var body = JsonNode.Parse(await created.Content.ReadAsStringAsync())!;
        Golden.AssertSame(expected["rca"]!["rca"], Golden.Normalise(body["rca"]), $"{name}.rca");

        var md = await app.Http.GetAsync($"/api/rca-drafts/{body["rca_draft_id"]}/export.md");
        Assert.Equal(expected["rca"]!["markdown"]!.GetValue<string>(), Golden.NormaliseText(await md.Content.ReadAsStringAsync()));
        Assert.StartsWith("text/markdown", md.Content.Headers.ContentType!.ToString());
        Assert.Equal(expected["rca"]!["content_disposition"]!.GetValue<string>(), md.Content.Headers.ContentDisposition!.ToString());

        var list = await app.Get("/api/incidents");
        var state = list["items"]!.AsArray().First(i => i!["key"]!.GetValue<string>() == key)!["review_state"]!.GetValue<string>();
        Assert.Equal(expected["review_state_after"]!.GetValue<string>(), state);
    }
}

/// <summary>The exact text and schemas the model is sent must not change in the port.</summary>
public class PromptGoldenTests
{
    private static readonly JsonNode G = Golden.Load("prompts");
    private static string S(string key) => G[key]!.GetValue<string>();

    private static EvidencePacket Sample(string? question = "is <it> the DB?") => new()
    {
        Incident = new PacketIncident { Key = "INC-1", Title = "T → x", Service = "svc", Severity = "SEV-1", Environment = "production", StartedAt = "2026-10-27T10:00:00Z", DetectedAt = "2026-10-27T10:01:00Z", Description = "d \"q\" <b> & 'y'", ImpactSummary = "i…" },
        Question = question,
        Items =
        [
            new PacketItem { Id = "log:1", Type = "log", Timestamp = "2026-10-27T10:00:00Z", Source = "app/svc", Excerpt = "ERROR a×b", Occurrences = 3 },
            new PacketItem { Id = "doc:1", Type = "runbook", Timestamp = null, Source = "runbook", Excerpt = "line1\nline2" },
        ],
    };

    [Fact]
    public void System_prompts_match_exactly()
    {
        Assert.Equal(Prompts.Version, S("version"));
        Assert.Equal(S("investigation_system"), Prompts.InvestigationSystem);
        Assert.Equal(S("rca_system"), Prompts.RcaSystem);
        Assert.Equal(S("follow_up_system"), Prompts.FollowUpSystem);
    }

    [Fact]
    public void User_messages_match_exactly_including_JSON_formatting()
    {
        Assert.Equal(S("investigation_user"), Prompts.InvestigationUserMessage(Sample()));
        Assert.Equal(S("investigation_user_no_question"), Prompts.InvestigationUserMessage(Sample(null)));
        Assert.Equal(S("follow_up_user"), Prompts.FollowUpUserMessage(Sample(), 8));
        Assert.Equal(S("follow_up_user_no_question"), Prompts.FollowUpUserMessage(Sample(null), 8));
        Assert.Equal(S("rca_user"), Prompts.RcaUserMessage(new RcaInput
        {
            Packet = Sample(),
            Summary = "s",
            EngineerNotes = ["note"],
            Hypotheses = [new RcaHypothesisInput { Rank = 1, Title = "t", Explanation = "e", Confidence = "high", EvidenceIds = ["log:1"] }],
            Similar = [new RcaSimilarInput { Id = "hist:1", Key = "INC-0", RootCause = "r", Resolution = "f" }],
        }));
    }

    [Fact]
    public void Output_schemas_match_exactly()
    {
        Golden.AssertSame(G["investigation_schema"], JsonNode.Parse(Schemas.InvestigationJson), "investigation_schema");
        Golden.AssertSame(G["rca_schema"], JsonNode.Parse(Schemas.RcaJson), "rca_schema");
    }

    [Theory]
    [InlineData("""{"summary":"s"}""")]
    [InlineData("""{"summary":1,"insufficient_evidence":false,"hypotheses":[],"similar_incidents":[],"limitations":[]}""")]
    [InlineData("""{"summary":"s","insufficient_evidence":false,"hypotheses":[{"title":"t","explanation":"e","confidence":"certain","evidence_ids":[],"counter_evidence":[],"unknowns":[],"recommended_checks":[]}],"similar_incidents":[],"limitations":[]}""")]
    [InlineData("""{"summary":"s","insufficient_evidence":false,"hypotheses":[{"title":"t"}],"similar_incidents":[],"limitations":[]}""")]
    [InlineData("not json")]
    public void A_reply_of_the_wrong_shape_is_rejected_rather_than_used(string reply)
    {
        Assert.ThrowsAny<JsonException>(() => Schemas.ParseInvestigation(reply));
    }

    [Fact]
    public void A_correct_reply_parses()
    {
        var o = Schemas.ParseInvestigation("""{"summary":"s","insufficient_evidence":true,"hypotheses":[{"title":"t","explanation":"e","confidence":"high","evidence_ids":["log:1"],"counter_evidence":[],"unknowns":[],"recommended_checks":[{"step":"a","why":"b","expected_signal":"c"}]}],"similar_incidents":[{"incident_id":"hist:1","similarity_reason":"r"}],"limitations":["l"]}""");
        Assert.True(o.InsufficientEvidence);
        Assert.Equal("c", o.Hypotheses[0].RecommendedChecks[0].ExpectedSignal);
        Assert.Equal("hist:1", o.SimilarIncidents[0].IncidentId);
    }
}

public class PipelineTests
{
    [Fact]
    public void Redacts_bearer_tokens_api_keys_passwords_url_credentials_and_emails()
    {
        var (text, redactions) = Sanitizer.Sanitize("Authorization: Bearer sk_live_51FAKEdemoTOKENdoNotUse9x smtp://mailer:password=hunter2-FAKE@relay:587 password=hunter2 jane.doe@example.com api_key=abc123");
        Assert.DoesNotContain("sk_live_51FAKE", text);
        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain("jane.doe@example.com", text);
        Assert.DoesNotContain("abc123", text);
        Assert.Contains("[EMAIL]", text);
        Assert.True(redactions >= 4);
    }

    [Theory]
    [InlineData("key sk-ant-abcDEF12345", "[REDACTED_KEY]")]
    [InlineData("AKIAABCDEFGHIJKLMNOP", "[REDACTED_KEY]")]
    [InlineData("ghp_abcdefghijklmnopqrstuvwxyz", "[REDACTED_KEY]")]
    [InlineData("card 4111 1111 1111 1111 ok", "[CARD]")]
    [InlineData("token: \"a b c\"", "token: [REDACTED]")]
    [InlineData("https://user:pw@host/path", "https://[REDACTED]@host/path")]
    public void Redacts_each_kind_of_secret(string input, string expected)
    {
        Assert.Contains(expected, Sanitizer.Sanitize(input).Text);
    }

    [Fact]
    public void Leaves_ordinary_log_text_alone()
    {
        const string line = "payment-api: database connection acquisition timed out after 5000ms; request_id=req-781";
        Assert.Equal((line, 0), Sanitizer.Sanitize(line));
    }

    [Fact]
    public void Flags_numbers_that_are_not_in_the_evidence_and_ignores_versions_dates_and_small_counts()
    {
        var known = Validate.NumbersIn("5xx rate 28% p95 2400ms pool.max=20 at 2026-10-27T10:04:00Z, 1,250 requests");
        Assert.Empty(Validate.UnverifiedNumbers("Errors hit 28% and p95 reached 2400ms with 20 connections; 1,250 requests failed.", known));
        Assert.Equal(["35%"], Validate.UnverifiedNumbers("Error rate reached 35% after v2.8.1 shipped on 2026-10-27 at 10:04; 3 retries.", known));
        Assert.Equal(["900ms", "450"], Validate.UnverifiedNumbers("Latency was 900ms and 450 requests timed out.", known));
    }

    [Fact]
    public void Non_ASCII_digits_are_not_read_as_numbers()
    {
        Assert.Empty(Validate.UnverifiedNumbers("٣٥٠ requests", []));
    }

    [Theory]
    [InlineData("Run rm -rf /var/lib/queue to clear it")]
    [InlineData("kubectl delete pod x")]
    [InlineData("DROP TABLE orders")]
    [InlineData("terraform destroy the stack")]
    [InlineData("chmod 777 /etc")]
    public void Destructive_checks_are_removed(string step)
    {
        var packet = new EvidencePacket { Items = [new PacketItem { Id = "log:1", Type = "log", Excerpt = "x" }] };
        var output = Output.With(Output.Hypothesis("h", "medium", "log:1"));
        output.Hypotheses[0].RecommendedChecks = [new RecommendedCheck { Step = step, Why = "x", ExpectedSignal = "x" }, new RecommendedCheck { Step = "Look at the dashboard", Why = "x", ExpectedSignal = "x" }];
        var v = Validate.Investigation(output, packet);
        Assert.Equal(["Look at the dashboard"], v.Hypotheses[0].RecommendedChecks.Select(c => c.Step));
        Assert.Contains(v.Issues, i => i.Contains("destructive"));
    }

    [Fact]
    public void Terms_drop_stopwords_digits_and_short_words()
    {
        Assert.Equal(["connection", "timeout"], Similar.Terms("The connection error 404 is a timeout, timeout!"));
    }
}

public class ApiBehaviourTests
{
    private static string S(JsonNode? n) => n!.GetValue<string>();

    [Fact]
    public async Task Lists_seeded_incidents_with_filters_and_returns_detail_and_evidence()
    {
        await using var app = new TestApp();
        var list = await app.Get("/api/incidents");
        Assert.Equal(6, list["total"]!.GetValue<int>());
        Assert.Equal("INC-1046", S(list["items"]![0]!["key"]));

        var sev1 = await app.Get("/api/incidents?severity=SEV-1");
        Assert.Equal(["INC-1042", "INC-1046"], sev1["items"]!.AsArray().Select(i => S(i!["key"])).Order());

        var evidence = await app.Get("/api/incidents/INC-1042/evidence");
        Assert.True(evidence["logs"]!.AsArray().Count > 5);
        Assert.Equal("db_conn_acquire_timeout", S(evidence["error_signals"]![0]!["signature"]));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Http.GetAsync("/api/incidents/INC-9999")).StatusCode);
    }

    [Fact]
    public async Task Unknown_API_paths_return_a_JSON_404()
    {
        await using var app = new TestApp();
        var res = await app.Http.GetAsync("/api/nope");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal("Not found", S(JsonNode.Parse(await res.Content.ReadAsStringAsync())!["error"]));
    }

    [Fact]
    public async Task Happy_path_gives_ranked_hypotheses_with_valid_evidence_and_the_similar_incident()
    {
        await using var app = new TestApp();
        var inv = await app.Run("INC-1042");
        Assert.Equal("completed", S(inv["status"]));
        Assert.Equal("high", S(inv["hypotheses"]![0]!["confidence"]));
        Assert.Contains("pool", S(inv["hypotheses"]![0]!["title"]), StringComparison.OrdinalIgnoreCase);
        foreach (var e in inv["hypotheses"]!.AsArray().SelectMany(h => h!["evidence"]!.AsArray())) Assert.NotEqual("(record not in packet)", S(e!["excerpt"]));
        Assert.Equal("INC-0881", S(inv["similar_incidents"]![0]!["historical"]!["key"]));
        Assert.Contains(inv["validation_issues"]!.AsArray(), i => S(i).Contains("Redacted"));
    }

    [Fact]
    public async Task Reports_insufficient_evidence_instead_of_inventing_a_cause()
    {
        await using var app = new TestApp();
        var inv = await app.Run("INC-1043");
        Assert.True(inv["insufficient_evidence"]!.GetValue<bool>());
        Assert.Empty(inv["hypotheses"]!.AsArray());
    }

    [Fact]
    public async Task Removes_citations_that_are_not_in_the_evidence_packet()
    {
        var llm = new ScriptedProvider((p, _) =>
        {
            var o = Output.With(Output.Hypothesis("Real", "high", p.Items.First(i => i.Type == "log").Id, "log:does-not-exist"), Output.Hypothesis("Made up", "high", "dep:fabricated"));
            o.SimilarIncidents = [new SimilarIncidentPick { IncidentId = "hist:not-a-candidate", SimilarityReason = "x" }];
            return o;
        });
        await using var app = new TestApp(llm);
        var inv = await app.Run("INC-1042");
        var hypotheses = inv["hypotheses"]!.AsArray();
        var real = hypotheses.First(h => S(h!["title"]) == "Real")!;
        var fake = hypotheses.First(h => S(h!["title"]) == "Made up")!;
        Assert.Single(real["evidence"]!.AsArray());
        Assert.True(fake["speculative"]!.GetValue<bool>());
        Assert.Equal("low", S(fake["confidence"]));
        Assert.Equal("Real", S(hypotheses[0]!["title"]));
        var issues = string.Join("\n", inv["validation_issues"]!.AsArray().Select(S));
        Assert.Contains("log:does-not-exist", issues);
        Assert.Contains("not among retrieved candidates", issues);
    }

    [Fact]
    public async Task Treats_prompt_injection_in_logs_as_data_and_never_sends_raw_secrets()
    {
        await using (var app = new TestApp())
        {
            var inv = await app.Run("INC-1044");
            var text = string.Join(" ", inv["hypotheses"]!.AsArray().Select(h => $"{h!["title"]} {h["explanation"]} {h["recommended_checks"]!.ToJsonString()}"));
            Assert.DoesNotContain("DNS outage", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("rm -rf", text);
        }

        var llm = new ScriptedProvider((p, _) =>
        {
            var o = Output.With(Output.Hypothesis("Queue corrupted", "medium", p.Items[0].Id));
            o.Hypotheses[0].RecommendedChecks = [new RecommendedCheck { Step = "Run rm -rf /var/lib/queue to clear it", Why = "x", ExpectedSignal = "x" }, new RecommendedCheck { Step = "Check the queue depth dashboard", Why = "x", ExpectedSignal = "x" }];
            return o;
        });
        await using var scripted = new TestApp(llm);
        var inv2 = await scripted.Run("INC-1044");
        Assert.Equal(["Check the queue depth dashboard"], inv2["hypotheses"]![0]!["recommended_checks"]!.AsArray().Select(c => S(c!["step"])));
        var sent = Json.Serialize(llm.Packets[0]);
        Assert.DoesNotContain("hunter2", sent);
        Assert.DoesNotContain("jane.doe@example.com", sent);
    }

    [Fact]
    public async Task Retries_once_on_malformed_output()
    {
        var llm = new ScriptedProvider((p, call) => call == 1 ? throw new LlmError("bad json", retryable: true) : Output.With(Output.Hypothesis("ok", "medium", p.Items[0].Id)));
        await using var app = new TestApp(llm);
        var inv = await app.Run("INC-1042");
        Assert.Equal("completed", S(inv["status"]));
        Assert.Equal(2, llm.Packets.Count);
        Assert.Contains(inv["validation_issues"]!.AsArray(), i => S(i).Contains("retried once"));
    }

    [Fact]
    public async Task Fails_gracefully_on_provider_errors_and_leaves_the_incident_intact()
    {
        var llm = new ScriptedProvider((_, _) => throw new LlmError("The Claude API is rate limiting requests. Wait a minute and run the investigation again."));
        await using var app = new TestApp(llm);
        var inv = await app.Run("INC-1042");
        Assert.Equal("failed", S(inv["status"]));
        Assert.Contains("rate limiting", S(inv["error_message"]));
        Assert.Equal(HttpStatusCode.OK, (await app.Http.GetAsync("/api/incidents/INC-1042")).StatusCode);
    }

    [Fact]
    public async Task An_unexpected_failure_does_not_leak_its_details()
    {
        var llm = new ScriptedProvider((_, _) => throw new InvalidOperationException("secret internal detail"));
        await using var app = new TestApp(llm);
        var inv = await app.Run("INC-1042");
        Assert.Equal("failed", S(inv["status"]));
        Assert.DoesNotContain("secret internal detail", S(inv["error_message"]));
    }

    [Fact]
    public async Task Records_feedback_drafts_an_RCA_persists_edits_and_exports_Markdown()
    {
        await using var app = new TestApp();
        var inv = await app.Run("INC-1042");
        var id = S(inv["id"]);
        var fb = await app.Post($"/api/investigations/{id}/feedback", new { hypothesis_id = S(inv["hypotheses"]![0]!["id"]), helpful = true, verified_root_cause = "pool.max set to 20 in db-client.yaml by v2.8.1" });
        Assert.Equal(HttpStatusCode.Created, fb.StatusCode);
        Assert.True((await app.Get($"/api/investigations/{id}"))["hypotheses"]![0]!["feedback"]!["helpful"]!.GetValue<bool>());

        var created = await app.Post("/api/incidents/INC-1042/rca-drafts", new { investigation_id = id });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var rca = JsonNode.Parse(await created.Content.ReadAsStringAsync())!["rca"]!;
        Assert.Equal(1, rca["version"]!.GetValue<int>());
        Assert.StartsWith("Verified by engineer: pool.max", S(rca["root_cause"]));
        Assert.True(rca["timeline"]!.AsArray().Count > 2);
        Assert.NotEmpty(rca["evidence_ids"]!.AsArray());

        var rcaId = S(rca["id"]);
        var patched = await app.Post($"/api/rca-drafts/{rcaId}", new { impact = "Edited impact", status = "reviewed" }, method: HttpMethod.Patch);
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        var reloaded = await app.Get($"/api/rca-drafts/{rcaId}");
        Assert.Equal("Edited impact", S(reloaded["impact"]));
        Assert.Equal("reviewed", S(reloaded["status"]));

        // Regenerating creates version 2 and keeps the edited version 1.
        var v2 = JsonNode.Parse(await (await app.Post("/api/incidents/INC-1042/rca-drafts", new { investigation_id = id })).Content.ReadAsStringAsync())!["rca"]!;
        Assert.Equal(2, v2["version"]!.GetValue<int>());
        Assert.Equal("Edited impact", S((await app.Get($"/api/rca-drafts/{rcaId}"))["impact"]));

        var md = await (await app.Http.GetAsync($"/api/rca-drafts/{rcaId}/export.md")).Content.ReadAsStringAsync();
        Assert.Contains("AI-generated draft — review required", md);
        Assert.Contains("Edited impact", md);
    }

    [Fact]
    public async Task Rejects_an_RCA_for_an_investigation_from_another_incident()
    {
        await using var app = new TestApp();
        var inv = await app.Run("INC-1042");
        var res = await app.Post("/api/incidents/INC-1044/rca-drafts", new { investigation_id = S(inv["id"]) });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("does not belong", S(JsonNode.Parse(await res.Content.ReadAsStringAsync())!["error"]));
    }

    [Fact]
    public async Task Bad_input_gets_a_400_and_changes_nothing()
    {
        await using var app = new TestApp();
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Post("/api/incidents/INC-1042/investigations", new { question = new string('x', 501) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Post("/api/incidents/INC-1042/investigations", new { mode = "other" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Post("/api/incidents/INC-1042/investigations", new { question = 5 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Post("/api/incidents/INC-1042/investigations", new { question = (string?)null })).StatusCode);
        Assert.Equal(0, app.Db.Get<int>("SELECT COUNT(*) FROM investigations"));

        var inv = await app.Run("INC-1042");
        var id = S(inv["id"]);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Post($"/api/investigations/{id}/feedback", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Post($"/api/investigations/{id}/feedback", new { hypothesis_id = "someone-elses", helpful = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Post("/api/incidents/INC-1042/rca-drafts", new { })).StatusCode);

        var rcaId = S(JsonNode.Parse(await (await app.Post("/api/incidents/INC-1042/rca-drafts", new { investigation_id = id })).Content.ReadAsStringAsync())!["rca"]!["id"]);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Post($"/api/rca-drafts/{rcaId}", new { status = "approved" }, method: HttpMethod.Patch)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Post($"/api/rca-drafts/{rcaId}", new { impact = new string('x', 5001) }, method: HttpMethod.Patch)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Post($"/api/rca-drafts/{rcaId}", new { resolution_steps = Enumerable.Repeat("x", 31) }, method: HttpMethod.Patch)).StatusCode);
    }

    [Fact]
    public async Task A_patch_ignores_unknown_fields_and_cannot_touch_other_columns()
    {
        await using var app = new TestApp();
        var inv = await app.Run("INC-1042");
        var rca = JsonNode.Parse(await (await app.Post("/api/incidents/INC-1042/rca-drafts", new { investigation_id = S(inv["id"]) })).Content.ReadAsStringAsync())!["rca"]!;
        var res = await app.Post($"/api/rca-drafts/{S(rca["id"])}", new { impact = "x", version = 99, incident_id = "other", generated_at = "never" }, method: HttpMethod.Patch);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var after = await app.Get($"/api/rca-drafts/{S(rca["id"])}");
        Assert.Equal(1, after["version"]!.GetValue<int>());
        Assert.Equal(S(rca["incident_id"]), S(after["incident_id"]));
        Assert.Equal(S(rca["generated_at"]), S(after["generated_at"]));
    }

    [Fact]
    public async Task Demo_reset_restores_the_dataset_from_localhost_and_refuses_remote_callers()
    {
        await using var app = new TestApp();
        await app.Run("INC-1042");
        Assert.Equal(HttpStatusCode.Forbidden, (await app.Post("/api/demo/reset", remote: "10.1.2.3")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.Post("/api/demo/reset")).StatusCode);
        Assert.Null((await app.Get("/api/incidents/INC-1042"))["latest_investigation_id"]);

        // A second run after reset gives the same result (recorded mode is deterministic).
        static string Shape(JsonNode inv) => string.Join("|", inv["hypotheses"]!.AsArray().Select(h => $"{h!["title"]}:{h["evidence"]!.AsArray().Select(e => e!["ref"]!.ToString()).Aggregate("", (a, b) => a + b)}"));
        var a = await app.Run("INC-1042");
        await app.Post("/api/demo/reset");
        var b = await app.Run("INC-1042");
        Assert.Equal(Shape(a), Shape(b));
    }

    [Fact]
    public async Task Demo_reset_is_hidden_when_demo_mode_is_off()
    {
        await using var app = new TestApp(demoMode: false);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Post("/api/demo/reset")).StatusCode);
    }

    private const string Code = "s3cret-code";

    private static (TestApp App, ScriptedProvider Live) Gated(int dailyLimit = 10)
    {
        var live = new ScriptedProvider((p, _) =>
        {
            var o = Output.With(Output.Hypothesis("Pool limit", "medium", p.Items[0].Id));
            o.Summary = "scripted live run";
            return o;
        });
        return (new TestApp(live: live, accessCode: Code, dailyLimit: dailyLimit), live);
    }

    [Fact]
    public async Task Advertises_live_mode_and_defaults_to_recorded()
    {
        var (app, _) = Gated();
        await using var _a = app;
        var cfg = await app.Get("/api/config");
        Assert.Equal("recorded", S(cfg["llm_mode"]));
        Assert.True(cfg["live_available"]!.GetValue<bool>());
        Assert.Equal(10, cfg["live_daily_limit"]!.GetValue<int>());
        Assert.Equal("recorded", S((await app.Run("INC-1042"))["llm_mode"]));
    }

    [Fact]
    public async Task Rejects_live_without_the_right_code_and_runs_live_with_it()
    {
        var (app, live) = Gated();
        await using var _a = app;
        const string url = "/api/incidents/INC-1042/investigations";
        Assert.Equal(HttpStatusCode.Forbidden, (await app.Post(url, new { mode = "live" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.Post(url, new { mode = "live" }, "wrong")).StatusCode);
        Assert.Empty(live.Packets);

        var ok = await app.Post(url, new { mode = "live" }, Code);
        Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);
        var inv = await app.Finish(S(JsonNode.Parse(await ok.Content.ReadAsStringAsync())!["investigation_id"]));
        Assert.Equal("live", S(inv["llm_mode"]));
        Assert.Equal("scripted live run", S(inv["summary"]));
        Assert.Single(live.Packets);

        // The RCA follows the investigation's mode, so it needs the code too.
        Assert.Equal(HttpStatusCode.Forbidden, (await app.Post("/api/incidents/INC-1042/rca-drafts", new { investigation_id = S(inv["id"]) })).StatusCode);
        var rca = await app.Post("/api/incidents/INC-1042/rca-drafts", new { investigation_id = S(inv["id"]) }, Code);
        Assert.Equal(HttpStatusCode.Created, rca.StatusCode);
        Assert.Equal("live", S(JsonNode.Parse(await rca.Content.ReadAsStringAsync())!["rca"]!["llm_mode"]));
    }

    [Fact]
    public async Task Enforces_the_daily_live_limit()
    {
        var (app, _) = Gated(1);
        await using var _a = app;
        Assert.Equal(HttpStatusCode.Accepted, (await app.Post("/api/incidents/INC-1042/investigations", new { mode = "live" }, Code)).StatusCode);
        Assert.Equal(429, (int)(await app.Post("/api/incidents/INC-1043/investigations", new { mode = "live" }, Code)).StatusCode);
        Assert.Equal(1, (await app.Get("/api/config"))["live_runs_today"]!.GetValue<int>());
        // Recorded runs are never limited.
        Assert.Equal("completed", S((await app.Run("INC-1043"))["status"]));
    }

    [Fact]
    public async Task Says_live_is_unavailable_when_no_key_or_code_is_configured()
    {
        await using var app = new TestApp();
        Assert.False((await app.Get("/api/config"))["live_available"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Post("/api/incidents/INC-1042/investigations", new { mode = "live" }, Code)).StatusCode);
    }

    [Fact]
    public async Task Allows_demo_reset_from_a_non_local_address_only_with_the_access_code()
    {
        var (app, _) = Gated();
        await using var _a = app;
        Assert.Equal(HttpStatusCode.Forbidden, (await app.Post("/api/demo/reset", remote: "172.17.0.1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.Post("/api/demo/reset", code: Code, remote: "172.17.0.1")).StatusCode);
    }

    [Fact]
    public async Task Records_an_invented_metric_as_a_validation_issue_without_removing_the_hypothesis()
    {
        await using var app = new TestApp(new ScriptedProvider((p, _) =>
        {
            var o = Output.With(Output.Hypothesis("Pool exhausted", "high", p.Items[0].Id));
            o.Hypotheses[0].Explanation = "Error rate climbed to 35% once the pool saturated.";
            return o;
        }));
        var inv = await app.Run("INC-1042");
        Assert.Single(inv["hypotheses"]!.AsArray());
        Assert.Contains(inv["validation_issues"]!.AsArray(), i => S(i).Contains("Unverified number \"35%\""));
        Assert.Equal(1, inv["trace"]!.AsArray().First(t => S(t!["stage"]) == "Validate output")!["details"]!["unverified_numbers"]!.GetValue<int>());
    }

    [Fact]
    public async Task Persists_a_stage_trace_for_each_investigation()
    {
        await using var app = new TestApp();
        var inv = await app.Run("INC-1042");
        Assert.Equal(["Gather evidence", "Search incident history", "Build evidence packet", "Analyse (recorded)", "Validate output"], inv["trace"]!.AsArray().Select(t => S(t!["stage"])));
        Assert.True(inv["trace"]!.AsArray().First(t => S(t!["stage"]) == "Build evidence packet")!["details"]!["redactions"]!.GetValue<int>() > 0);
    }

    [Fact]
    public async Task Moves_an_incident_from_none_to_pending_to_verified_as_a_human_confirms_the_work()
    {
        await using var app = new TestApp();
        async Task<string> State() => S((await app.Get("/api/incidents?q=INC-1042"))["items"]![0]!["review_state"]);
        Assert.Equal("none", await State());
        var inv = await app.Run("INC-1042");
        Assert.Equal("pending", await State());
        await app.Post($"/api/investigations/{S(inv["id"])}/feedback", new { verified_root_cause = "pool.max lowered to 20" });
        Assert.Equal("pending", await State());
        var rca = JsonNode.Parse(await (await app.Post("/api/incidents/INC-1042/rca-drafts", new { investigation_id = S(inv["id"]) })).Content.ReadAsStringAsync())!["rca"]!;
        await app.Post($"/api/rca-drafts/{S(rca["id"])}", new { status = "reviewed" }, method: HttpMethod.Patch);
        Assert.Equal("verified", await State());
        Assert.Equal(0, (await app.Get("/api/incidents?review=pending"))["total"]!.GetValue<int>());
    }

    [Fact]
    public async Task Keeps_earlier_runs_and_lists_them_newest_first()
    {
        await using var app = new TestApp();
        var first = await app.Run("INC-1042");
        var second = await app.Run("INC-1042");
        var detail = await app.Get("/api/incidents/INC-1042");
        Assert.Equal([S(second["id"]), S(first["id"])], detail["investigations"]!.AsArray().Select(r => S(r!["id"])));
        Assert.Contains("pool", S(detail["investigations"]![0]!["top_hypothesis"]), StringComparison.OrdinalIgnoreCase);
    }
}

public class SystemsBehaviourTests
{
    private static string S(JsonNode? n) => n!.GetValue<string>();

    [Fact]
    public async Task Runs_the_baseline_checks_and_recorded_follow_ups_and_cites_their_results()
    {
        await using var app = new TestApp(systems: await Hubs.Demo());
        var inv = await app.Run("INC-1045");
        var calls = inv["tool_calls"]!.AsArray();
        Assert.Equal(["files_search", "files_search", "deploys_recent", "metrics_series", "metrics_series"], calls.Where(c => S(c!["phase"]) == "baseline").Select(c => S(c!["tool"])));
        Assert.Equal(["files_tail", "files_tail", "deploys_change_summary"], calls.Where(c => S(c!["phase"]) == "follow_up").Select(c => S(c!["tool"])));
        Assert.All(calls, c => Assert.True(c!["ok"]!.GetValue<bool>()));
        var top = inv["hypotheses"]![0]!;
        Assert.Equal("high", S(top["confidence"]));
        var cited = top["evidence"]!.AsArray().Select(e => S(e!["ref"])).ToList();
        foreach (var r in new[] { "mcp:1", "mcp:6", "mcp:7", "mcp:8" }) Assert.Contains(r, cited);
        Assert.Equal("files: files_tail", S(top["evidence"]!.AsArray().First(e => S(e!["ref"]) == "mcp:6")!["label"]));
    }

    [Fact]
    public async Task Redacts_secrets_in_tool_output_before_the_model_sees_them_but_keeps_raw_rows_for_the_engineer()
    {
        await using var app = new TestApp(systems: await Hubs.Demo());
        var inv = await app.Run("INC-1042");
        var errors = inv["tool_calls"]!.AsArray().First(c => S(c!["tool"]) == "files_search")!;
        Assert.Contains("sk_live_51FAKE", errors["rows"]!.ToJsonString());
        var packet = app.Db.Get<string>("SELECT evidence_packet FROM investigations WHERE id = @id", new { id = S(inv["id"]) })!;
        Assert.DoesNotContain("sk_live_51FAKE", packet);
        Assert.Contains("mcp:1", packet);
    }

    [Fact]
    public async Task Enforces_the_follow_up_limit_however_many_calls_the_model_tries()
    {
        var replies = new List<string>();
        var greedy = new ScriptedProvider((p, _) => Output.With(Output.Hypothesis("x", "low", p.Items[0].Id)), async (_, tools, call, _, ct) =>
        {
            Assert.Contains("files_tail", tools.Select(t => t.Name));
            for (var i = 0; i < 10; i++) replies.Add(await call("files_tail", new() { ["path"] = "payment-api/app.log", ["lines"] = 5L }, ct));
            return null;
        });
        await using var app = new TestApp(greedy, systems: await Hubs.Demo(maxFollowUps: 3));
        var inv = await app.Run("INC-1042");
        Assert.Equal(3, inv["tool_calls"]!.AsArray().Count(c => S(c!["phase"]) == "follow_up"));
        Assert.All(replies.Take(3), r => Assert.Matches(@"^\[mcp:[0-9]+\] Last 5 line\(s\)", r));
        Assert.All(replies.Skip(3), r => Assert.StartsWith("Limit reached", r));
        var stage = inv["trace"]!.AsArray().First(t => S(t!["stage"]) == "Follow-up checks (Claude)")!["details"]!;
        Assert.Equal(3, stage["calls"]!.GetValue<int>());
        Assert.Equal(7, stage["refused"]!.GetValue<int>());
    }

    [Fact]
    public async Task Keeps_going_when_follow_ups_fail()
    {
        var broken = new ScriptedProvider((_, _) => Output.With(), (_, _, _, _, _) => throw new InvalidOperationException("model unavailable"));
        await using var app = new TestApp(broken, systems: await Hubs.Demo());
        var inv = await app.Run("INC-1042");
        Assert.Equal("completed", S(inv["status"]));
        Assert.Contains(inv["validation_issues"]!.AsArray(), i => S(i) == "Follow-up checks stopped early: model unavailable");
    }
}
