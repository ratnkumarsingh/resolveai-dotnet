using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using ResolveAI.App.Llm;
using ResolveAI.App.Services;
using ResolveAI.Contracts;

namespace ResolveAI.App.Api;

/// <summary>The HTTP API: the same routes and JSON the app has always had. Rules live in AppService; this layer only translates.</summary>
public static class ApiEndpoints
{
    private static IResult JsonResult<T>(T value, int status = 200) => Results.Json(value, Json.Options, statusCode: status);

    private static Caller CallerOf(HttpContext http)
    {
        var code = http.Request.Headers["x-access-code"].ToString();
        return new Caller(code.Length > 0 ? code : null, http.Connection.RemoteIpAddress is { } ip && (System.Net.IPAddress.IsLoopback(ip) || (ip.IsIPv4MappedToIPv6 && System.Net.IPAddress.IsLoopback(ip.MapToIPv4()))));
    }

    /// <summary>Reads a JSON body. A missing body counts as an empty object; explicit nulls and wrong types are refused, as they always were.</summary>
    private static async Task<T> Body<T>(HttpRequest request, params (string Name, JsonValueKind Kind)[] fields) where T : new()
    {
        using var reader = new StreamReader(request.Body);
        var text = await reader.ReadToEndAsync(request.HttpContext.RequestAborted);
        if (string.IsNullOrWhiteSpace(text)) text = "{}";
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            throw new AppError(400, "Invalid request: body is not valid JSON.");
        }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new AppError(400, "Invalid request: body must be an object.");
            foreach (var (name, kind) in fields)
            {
                if (!doc.RootElement.TryGetProperty(name, out var value)) continue;
                var ok = kind == JsonValueKind.True ? value.ValueKind is JsonValueKind.True or JsonValueKind.False : value.ValueKind == kind;
                if (!ok) throw new AppError(400, $"Invalid request: {name} has the wrong type.");
            }
            try
            {
                return JsonSerializer.Deserialize<T>(doc.RootElement, Json.Options) ?? new T();
            }
            catch (JsonException)
            {
                throw new AppError(400, "Invalid request: a field has the wrong type.");
            }
        }
    }

    private static int IntQuery(HttpRequest request, string name, int fallback) =>
        int.TryParse(request.Query[name], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n != 0 ? n : fallback;

    public static void MapApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/config", ([FromServices] AppService s) => JsonResult(s.Config()));
        api.MapGet("/systems", ([FromServices] AppService s) => JsonResult(s.Systems()));

        api.MapGet("/incidents", ([FromServices] AppService s, HttpRequest r) => JsonResult(s.ListIncidents(
            r.Query["status"], r.Query["severity"], r.Query["service"], r.Query["q"], r.Query["review"], IntQuery(r, "page", 1), IntQuery(r, "page_size", 25))));

        api.MapGet("/incidents/{id}", ([FromServices] AppService s, string id) => JsonResult(s.IncidentDetail(id)));
        api.MapGet("/incidents/{id}/evidence", ([FromServices] AppService s, string id) => JsonResult(s.Evidence(id)));

        api.MapPost("/incidents/{id}/investigations", async ([FromServices] AppService s, string id, HttpContext http) =>
        {
            // The incident is checked first, so an unknown incident is a 404 even with a bad body.
            s.IncidentDetail(id);
            var body = await Body<StartInvestigationBody>(http.Request, ("question", JsonValueKind.String), ("mode", JsonValueKind.String));
            var (investigationId, status) = s.StartInvestigation(id, body, CallerOf(http));
            return JsonResult(new { investigation_id = investigationId, status }, 202);
        });

        api.MapGet("/investigations/{id}", ([FromServices] AppService s, string id) => JsonResult(s.Investigation(id)));

        api.MapPost("/investigations/{id}/feedback", async ([FromServices] AppService s, string id, HttpContext http) =>
        {
            var body = await Body<FeedbackBody>(http.Request, ("hypothesis_id", JsonValueKind.String), ("helpful", JsonValueKind.True), ("comment", JsonValueKind.String), ("verified_root_cause", JsonValueKind.String));
            return JsonResult(new { id = s.AddFeedback(id, body) }, 201);
        });

        api.MapPost("/incidents/{id}/rca-drafts", async ([FromServices] AppService s, string id, HttpContext http) =>
        {
            s.IncidentDetail(id);
            var body = await Body<CreateRcaBody>(http.Request, ("investigation_id", JsonValueKind.String));
            if (string.IsNullOrEmpty(body.InvestigationId)) throw new AppError(400, "Invalid request: investigation_id is required.");
            var rca = await s.CreateRca(id, body.InvestigationId, CallerOf(http));
            return JsonResult(new { rca_draft_id = rca.Id, rca }, 201);
        });

        api.MapGet("/rca-drafts/{id}", ([FromServices] AppService s, string id) => JsonResult(s.GetRca(id)));

        api.MapPatch("/rca-drafts/{id}", async ([FromServices] AppService s, string id, HttpContext http) =>
        {
            var body = await Body<PatchRcaBody>(http.Request,
                ("impact", JsonValueKind.String), ("root_cause", JsonValueKind.String), ("status", JsonValueKind.String), ("timeline", JsonValueKind.Array),
                ("contributing_factors", JsonValueKind.Array), ("resolution_steps", JsonValueKind.Array), ("prevention_actions", JsonValueKind.Array));
            return JsonResult(s.PatchRca(id, body));
        });

        api.MapGet("/rca-drafts/{id}/export.md", ([FromServices] AppService s, string id, HttpContext http) =>
        {
            var (fileName, markdown) = s.ExportRca(id);
            http.Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";
            return Results.Text(markdown, "text/markdown; charset=utf-8");
        });

        api.MapGet("/knowledge/{id}", ([FromServices] AppService s, string id) => JsonResult(s.Knowledge(id)));

        api.MapPost("/demo/reset", ([FromServices] AppService s, HttpContext http) =>
        {
            s.DemoReset(CallerOf(http));
            return JsonResult(new { ok = true });
        });

        api.MapFallback((HttpContext _) => JsonResult(new ErrorResponse { Error = "Not found" }, 404));
    }

    /// <summary>Turns failures from /api into the { error } responses the API has always returned.</summary>
    public static IApplicationBuilder UseApiErrors(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        try
        {
            await next();
        }
        catch (Exception err) when (http.Request.Path.StartsWithSegments("/api") && !http.Response.HasStarted && err is not OperationCanceledException)
        {
            var (status, message) = err switch
            {
                AppError e => (e.Status, e.Message),
                LlmError e => (502, e.Message),
                _ => (500, "Internal server error"),
            };
            if (status == 500) Console.Error.WriteLine(err);
            http.Response.StatusCode = status;
            await http.Response.WriteAsJsonAsync(new ErrorResponse { Error = message }, Json.Options);
        }
    });
}
