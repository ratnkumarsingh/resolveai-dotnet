using ResolveAI.App;
using ResolveAI.App.Api;
using ResolveAI.App.Components;
using ResolveAI.App.Services;
using ResolveAI.Mcp;

AppSettings.LoadDotEnv(Path.Combine(McpConfig.RepoRoot(), ".env"));

// The container's health check: the runtime image has no curl, so the app checks itself.
if (args is ["--healthcheck"])
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    try
    {
        var res = await http.GetAsync($"http://127.0.0.1:{AppSettings.Load().Port}/api/config");
        return res.IsSuccessStatusCode ? 0 : 1;
    }
    catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
    {
        return 1;
    }
}

// A published app serves its files from its own folder, wherever it was started from. Development uses the project folder.
var isDevelopment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == Environments.Development;
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = isDevelopment ? null : AppContext.BaseDirectory,
});

var settings = AppSettings.Load();
builder.WebHost.UseUrls($"http://{settings.Host}:{settings.Port}");

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// Tests register their own AppDeps first; the real one (which starts child processes) is only built when nothing else was registered.
builder.Services.AddSingleton<AppDeps>(_ => AppDeps.Create(settings).GetAwaiter().GetResult());
builder.Services.AddSingleton(sp =>
{
    var deps = sp.GetRequiredService<AppDeps>();
    return new AppService(deps.Db, deps.Providers, deps.Systems, deps.Settings);
});
builder.Services.AddScoped<AccessCodeStore>();

var app = builder.Build();

// Build everything before accepting requests, so the first request doesn't pay for starting the MCP servers.
var deps = app.Services.GetRequiredService<AppDeps>();

// An unknown page shows the app's not-found page. Only for page requests: other methods and the API keep their own status codes.
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.Use((http, next) =>
{
    var isPage = HttpMethods.IsGet(http.Request.Method) && !http.Request.Path.StartsWithSegments("/api") && !http.Request.Path.StartsWithSegments("/_blazor");
    if (!isPage && http.Features.Get<Microsoft.AspNetCore.Diagnostics.IStatusCodePagesFeature>() is { } pages) pages.Enabled = false;
    return next();
});
app.UseApiErrors();
app.UseCircuitOriginCheck();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapApi();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Lifetime.ApplicationStopping.Register(() =>
{
    if (deps.Systems is not null) deps.Systems.Hub.DisposeAsync().AsTask().GetAwaiter().GetResult();
});

app.Lifetime.ApplicationStarted.Register(() =>
{
    var live = deps.Providers.Live is null
        ? "off (no API key)"
        : deps.Settings.LlmMode == "live"
            ? $"default ({deps.Settings.Model})"
            : !string.IsNullOrEmpty(deps.Settings.LiveAccessCode)
                ? $"with access code ({deps.Settings.Model}, {deps.Settings.LiveDailyLimit}/day)"
                : "off (set LIVE_ACCESS_CODE to allow it)";
    Console.WriteLine($"ResolveAI on http://{deps.Settings.Host}:{deps.Settings.Port} — recorded mode; live Claude: {live}");
});

await app.RunAsync();
return 0;

public partial class Program;
