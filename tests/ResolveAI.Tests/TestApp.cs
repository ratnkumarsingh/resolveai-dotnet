using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ResolveAI.App;
using ResolveAI.App.Data;
using ResolveAI.App.Llm;
using ResolveAI.App.Mcp;
using ResolveAI.App.Services;
using ResolveAI.Contracts;
using ResolveAI.Mcp;

namespace ResolveAI.Tests;

/// <summary>A provider whose Investigate is scripted by the test; RCA delegates to the recorded provider.</summary>
public sealed class ScriptedProvider(Func<EvidencePacket, int, InvestigationOutput> script, FollowUp? followUp = null) : ILlmProvider
{
    private readonly RecordedProvider _recorded = new(0);

    public string Mode => "live";
    public string? Model => "scripted";
    public List<EvidencePacket> Packets { get; } = [];
    public FollowUp? FollowUp => followUp;

    public Task<LlmResult<InvestigationOutput>> Investigate(EvidencePacket packet, CancellationToken ct)
    {
        Packets.Add(packet);
        return Task.FromResult(new LlmResult<InvestigationOutput>(script(packet, Packets.Count), "scripted"));
    }

    public Task<LlmResult<RcaOutput>> DraftRca(RcaInput input, CancellationToken ct) => _recorded.DraftRca(input, ct);
}

public static class Hubs
{
    public static HubEntry InProcess(SystemDef def, McpServerOptions options, bool enabled = true) => new(def, enabled, null, () =>
    {
        var toServer = new Pipe();
        var toClient = new Pipe();
        var server = McpServer.Create(new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream()), options);
        _ = server.RunAsync();
        return new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream());
    });

    /// <summary>The four demo systems from mcp.config.example.json, in-process. SQL Server answers from a fake when given, and is otherwise off, as it is by default.</summary>
    public static async Task<Systems> Demo(SqlQuery? sql = null, int maxFollowUps = 8)
    {
        var cfg = McpFixtures.Example();
        var entries = ServerDefs.All.Select(d =>
        {
            var def = new SystemDef(d.System, d.Server, d.Label);
            return d.Server switch
            {
                "sqlserver" => InProcess(def, SqlServerServer.Build(cfg, sql ?? McpFixtures.FakeSql).ToOptions(), enabled: sql is not null),
                _ => InProcess(def, d.Build(cfg).ToOptions()),
            };
        });
        return new Systems(await McpHub.Connect(entries), cfg, maxFollowUps);
    }
}

/// <summary>Builds an app on a fresh seeded in-memory database, reachable through an in-process HTTP client.</summary>
public sealed class TestApp : IAsyncDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    public Db Db { get; }
    public HttpClient Http { get; }

    public TestApp(ILlmProvider? llm = null, ILlmProvider? live = null, Systems? systems = null, string? accessCode = null, int dailyLimit = 1000, bool demoMode = true)
    {
        llm ??= new RecordedProvider(0);
        Db = new Db(":memory:");
        Seed.Demo(Db);
        var providers = llm.Mode == "live" ? new Providers(new RecordedProvider(0), llm) : new Providers(llm, live);
        var settings = new AppSettings { LlmMode = llm.Mode, Model = "test-model", LiveAccessCode = accessCode, LiveDailyLimit = dailyLimit, DemoMode = demoMode, McpEnabled = false };
        var deps = new AppDeps(Db, providers, systems, settings);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.ConfigureTestServices(s =>
            {
                s.AddSingleton(deps);
                s.AddSingleton<IStartupFilter>(new RemoteAddressFilter());
            });
        });
        Http = _factory.CreateClient();
    }

    /// <summary>TestServer has no real connection: the caller's address comes from a test-only header, loopback by default.</summary>
    private sealed class RemoteAddressFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((http, nextStep) =>
            {
                http.Connection.RemoteIpAddress = http.Request.Headers.TryGetValue("x-test-remote", out var remote) ? IPAddress.Parse(remote.ToString()) : IPAddress.Loopback;
                return nextStep();
            });
            next(app);
        };
    }

    public async Task<JsonNode> Get(string url)
    {
        var res = await Http.GetAsync(url);
        return JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
    }

    public Task<HttpResponseMessage> Post(string url, object? body = null, string? code = null, string? remote = null, HttpMethod? method = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Post, url);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (code is not null) request.Headers.Add("x-access-code", code);
        if (remote is not null) request.Headers.Add("x-test-remote", remote);
        return Http.SendAsync(request);
    }

    public async Task<JsonNode> Run(string incident, object? body = null, string? code = null)
    {
        var start = await Post($"/api/incidents/{incident}/investigations", body ?? new { }, code);
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
        var id = JsonNode.Parse(await start.Content.ReadAsStringAsync())!["investigation_id"]!.GetValue<string>();
        return await Finish(id);
    }

    public async Task<JsonNode> Finish(string id)
    {
        for (var i = 0; i < 1000; i++)
        {
            var inv = await Get($"/api/investigations/{id}");
            if (inv["status"]!.GetValue<string>() is "completed" or "failed") return inv;
            await Task.Delay(10);
        }
        throw new TimeoutException("investigation did not finish");
    }

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await _factory.DisposeAsync();
        Db.Dispose();
    }
}

public static class Output
{
    public static LlmHypothesis Hypothesis(string title, string confidence, params string[] evidence) =>
        new() { Title = title, Explanation = "x", Confidence = confidence, EvidenceIds = [.. evidence] };

    public static InvestigationOutput With(params LlmHypothesis[] hypotheses) =>
        new() { Summary = "test", Hypotheses = [.. hypotheses] };
}
