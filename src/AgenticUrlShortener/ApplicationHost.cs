using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgenticUrlShortener;

public static class ApplicationHost
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);
        builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = 65_536);
        var configuration = builder.Configuration;
        if (configuration["urls"] == null) builder.WebHost.UseUrls("http://localhost:5080");
        var data = Path.GetFullPath(configuration["DATA_DIR"] ?? Path.Combine(builder.Environment.ContentRootPath, "data"));
        var root = configuration["REPOSITORY_ROOT"] ?? FindRepository(builder.Environment.ContentRootPath);
        var options = new EngineeringOptions(Path.GetFullPath(root), data,
            configuration["DOTNET_EXECUTABLE"] ?? "dotnet", configuration["ENABLE_FAILURE_INJECTION"] == "true");
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<AccessKeys>();
        builder.Services.AddAuthentication("ApiKey").AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>("ApiKey", _ => { });
        builder.Services.AddAuthorization(auth =>
        {
            auth.AddPolicy("operator", policy => policy.RequireRole("operator"));
            auth.AddPolicy("reviewer", policy => policy.RequireRole("reviewer"));
        });
        builder.Services.AddSingleton(new JsonStore<UrlData>(Path.Combine(data, "urls.json")));
        builder.Services.AddSingleton(new JsonStore<WorkflowCollection>(Path.Combine(data, "workflows.json")));
        builder.Services.AddSingleton<UrlService>();
        builder.Services.AddSingleton<AuditJournal>();
        builder.Services.TryAddSingleton<IToolRunner, ToolRunner>();
        builder.Services.TryAddSingleton<IEngineeringStages, EngineeringStages>();
        builder.Services.AddSingleton<WorkflowEngine>();
        var app = builder.Build();
        _ = app.Services.GetRequiredService<AccessKeys>(); // Validate credentials before accepting requests.
        app.Use(async (context, next) =>
        {
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            try { await next(); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ValidationException or KeyNotFoundException)
            {
                context.Response.StatusCode = ex switch
                {
                    ArgumentException => 400, KeyNotFoundException => 404, _ => 409
                };
                await context.Response.WriteAsJsonAsync(new { error = ex.Message });
            }
        });
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/", () => Results.Ok(new { Service = "Agentic URL Shortener", Version = "2.0", Docs = "/docs" }));
        app.MapGet("/docs", () => Results.Content("""
            <!doctype html><html><head><meta charset="utf-8"><title>Agentic URL Shortener API</title></head>
            <body><h1>Agentic URL Shortener API</h1><p>Version 2. <a href="/openapi.json">OpenAPI 3.0 specification</a>.</p>
            <p>Management APIs require an X-Api-Key header. Use the operator key to create and replan workflows;
            use the separate reviewer key to approve or reject a specific revision and evidence hash.</p>
            <p>See README.md for setup, candidate artifacts, clarification, retries, fallback and quarantine examples.</p>
            <ul><li>POST /api/urls</li><li>GET /{code} (public redirect)</li><li>GET /api/urls/{code}/analytics</li>
            <li>DELETE /api/urls/{code}</li><li>POST /api/workflows</li><li>GET /api/workflows</li>
            <li>GET /api/workflows/{id}</li><li>GET /api/workflows/{id}/audit</li>
            <li>GET /api/workflows/{id}/artifacts/{path}</li><li>POST /api/workflows/{id}/approval</li>
            <li>POST /api/workflows/{id}/replan</li><li>GET /api/workflows/metrics</li></ul></body></html>
            """, "text/html"));
        app.MapGet("/openapi.json", () => Results.File(Path.Combine(root, "src", "AgenticUrlShortener", "openapi.json"), "application/json"));
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapPost("/urls", async (CreateUrlRequest req, UrlService service, HttpContext ctx) =>
        {
            var link = await service.Create(req);
            return Results.Created($"/api/urls/{link.Code}/analytics", new { link.Code,
                ShortUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}/{link.Code}", link.Url, link.ExpiresUtc });
        }).RequireAuthorization("operator");
        api.MapGet("/urls/{code}/analytics", async (string code, UrlService service) =>
            await service.Analytics(code) is { } value ? Results.Ok(value) : Results.NotFound());
        api.MapDelete("/urls/{code}", async (string code, UrlService service) =>
            await service.Disable(code) ? Results.NoContent() : Results.NotFound()).RequireAuthorization("operator");
        api.MapPost("/workflows", async (WorkflowRequest req, WorkflowEngine engine, HttpContext ctx) =>
        {
            var workflow = await engine.Create(req, ctx.User.Identity!.Name!);
            return Results.Created($"/api/workflows/{workflow.Id}", workflow);
        }).RequireAuthorization("operator");
        api.MapGet("/workflows", async (WorkflowEngine engine) => Results.Ok(await engine.All()));
        api.MapGet("/workflows/metrics", async (WorkflowEngine engine) => Results.Ok(await engine.Metrics()));
        api.MapGet("/workflows/{id}", async (string id, WorkflowEngine engine) =>
            await engine.Get(id) is { } workflow ? Results.Ok(workflow) : Results.NotFound());
        api.MapGet("/workflows/{id}/audit", async (string id, WorkflowEngine engine, AuditJournal journal) =>
        {
            var workflow = await engine.Get(id) ?? throw new KeyNotFoundException();
            var events = journal.ReadVerified(workflow);
            if (workflow.Status != "Running") journal.Verify(workflow);
            return Results.Ok(new { Integrity = "verified", Events = events });
        });
        api.MapPost("/workflows/{id}/approval", async (string id, ApprovalRequest req, WorkflowEngine engine, HttpContext ctx) =>
            Results.Ok(await engine.Approve(id, req, ctx.User.Identity!.Name!))).RequireAuthorization("reviewer");
        api.MapPost("/workflows/{id}/replan", async (string id, ReplanRequest req, WorkflowEngine engine, HttpContext ctx) =>
            Results.Ok(await engine.Replan(id, req, ctx.User.Identity!.Name!))).RequireAuthorization("operator");
        api.MapGet("/workflows/{id}/artifacts/{**path}", async (string id, string path, WorkflowEngine engine) =>
        {
            var w = await engine.Get(id) ?? throw new KeyNotFoundException();
            if (!w.Evidence.Values.Any(e => e.Files.ContainsKey(path))) return Results.NotFound();
            var file = EngineeringStages.Contained(Path.Combine(data, "workspaces", w.Id, "r" + w.Revision), path);
            return Results.File(file, "text/plain", Path.GetFileName(path));
        });
        app.MapGet("/{code}", async (string code, UrlService service) =>
            await service.Resolve(code) is { } link ? Results.Redirect(link.Url) : Results.NotFound());
        return app;
    }

    private static string FindRepository(string start)
    {
        for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "templates", "candidate"))) return directory.FullName;
        throw new InvalidOperationException("Cannot locate trusted templates; set REPOSITORY_ROOT.");
    }
}
