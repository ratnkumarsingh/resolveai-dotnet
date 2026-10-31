using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using ResolveAI.App;

namespace ResolveAI.E2E;

/// <summary>
/// The real app on a real port, with real MCP child processes and a browser, on a temporary database: these tests reset
/// the demo data, so they must never touch data/resolveai.db.
/// </summary>
public sealed class AppFixture : IAsyncLifetime
{
    public const string AccessCode = "e2e-access-code";

    private readonly string _dir = Directory.CreateTempSubdirectory("resolveai-e2e-").FullName;
    private WebApplicationFactory<Program>? _factory;
    private AppDeps? _deps;
    private IPlaywright? _playwright;

    public IBrowser Browser { get; private set; } = null!;
    public string BaseUrl { get; private set; } = "";
    public HttpClient Http { get; private set; } = new();
    public AppDeps Deps => _deps!;
    /// <summary>Starts the server with live Claude as its default. The key is fake, so a run started by mistake would fail, not spend money.</summary>
    public bool LiveByDefault { get; init; }

    public async Task InitializeAsync()
    {
        // A free port, so these tests can never bind to, or be confused with, a server already running on the default one.
        Environment.SetEnvironmentVariable("API_PORT", "0");
        _deps = await AppDeps.Create(new AppSettings
        {
            DbPath = Path.Combine(_dir, "e2e.db"),
            McpEnabled = true,
            DemoMode = true,
            LiveAccessCode = AccessCode,
            MaxFollowUps = 8,
            ApiKey = LiveByDefault ? "fake-key-for-tests" : null,
            LlmMode = LiveByDefault ? "live" : "recorded",
        });
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.ConfigureTestServices(s => s.AddSingleton(_deps));
        });
        _factory.UseKestrel();
        _factory.StartServer();
        BaseUrl = _factory.ClientOptions.BaseAddress.ToString().TrimEnd('/');
        Http = _factory.CreateClient();

        _playwright = await Playwright.CreateAsync();
        // E2E_BROWSER_PATH runs an already-installed Chromium; otherwise Playwright's own (see README).
        var path = Environment.GetEnvironmentVariable("E2E_BROWSER_PATH");
        Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { ExecutablePath = string.IsNullOrEmpty(path) ? null : path });
    }

    public async Task DisposeAsync()
    {
        if (_playwright is not null) await Browser.DisposeAsync();
        _playwright?.Dispose();
        Http.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
        if (_deps?.Systems is not null) await _deps.Systems.Hub.DisposeAsync();
        _deps?.Db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }
}

[CollectionDefinition("app")]
public sealed class AppCollection : ICollectionFixture<AppFixture>;
