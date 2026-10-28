using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgenticUrlShortener;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgenticUrlShortener.Tests;

public sealed class ApiFixture : IAsyncDisposable
{
    public const string OperatorKey = "integration-operator-only";
    public const string ReviewerKey = "integration-reviewer-only";
    public string Data { get; } = Path.Combine(Path.GetTempPath(), "agentic-http-tests", Guid.NewGuid().ToString("N"));
    public WebApplication App { get; }
    public HttpClient Operator { get; }
    public HttpClient Reviewer { get; }
    public HttpClient Anonymous { get; }
    public string Dotnet { get; } = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
    public ApiFixture(bool realStages = false)
    {
        App = ApplicationHost.Build([], builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DATA_DIR"] = Data, ["REPOSITORY_ROOT"] = EngineFixture.RepositoryPath(),
                ["OPERATOR_API_KEY"] = OperatorKey, ["REVIEWER_API_KEY"] = ReviewerKey,
                ["ENABLE_FAILURE_INJECTION"] = "true", ["DOTNET_EXECUTABLE"] = Dotnet
            });
            if (!realStages) builder.Services.AddSingleton<IEngineeringStages, TestStages>();
        });
        App.StartAsync().GetAwaiter().GetResult();
        Operator = App.GetTestClient(); Operator.DefaultRequestHeaders.Add("X-Api-Key", OperatorKey);
        Reviewer = App.GetTestClient(); Reviewer.DefaultRequestHeaders.Add("X-Api-Key", ReviewerKey);
        Anonymous = App.GetTestClient();
        Operator.Timeout = TimeSpan.FromMinutes(3);
    }
    public async Task<WorkflowState> Create(WorkflowRequest request)
    {
        var response = await Operator.PostAsJsonAsync("/api/workflows", request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, text);
        return JsonSerializer.Deserialize<WorkflowState>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
    public async ValueTask DisposeAsync()
    {
        Operator.Dispose(); Reviewer.Dispose(); Anonymous.Dispose();
        await App.DisposeAsync();
        if (Directory.Exists(Data)) Directory.Delete(Data, true);
    }
}

public class ApiTests
{
    [Fact]
    public async Task AuthenticationAndRoleBoundariesAreEnforced()
    {
        await using var f = new ApiFixture();
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Anonymous.GetAsync("/api/workflows")).StatusCode);
        f.Anonymous.DefaultRequestHeaders.Add("X-Api-Key", "invalid");
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Anonymous.GetAsync("/api/workflows")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Reviewer.PostAsJsonAsync("/api/workflows", new WorkflowRequest("Build"))).StatusCode);
        var w = await f.Create(new("Build"));
        Assert.Equal("operator", w.CreatedBy);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Operator.PostAsJsonAsync($"/api/workflows/{w.Id}/approval", EngineFixture.Decision(w))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Reviewer.PostAsJsonAsync($"/api/workflows/{w.Id}/replan", new ReplanRequest("Change", 1))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Reviewer.PostAsJsonAsync($"/api/workflows/{w.Id}/approval",
            EngineFixture.Decision(w) with { Reviewer = "someone-else" })).StatusCode);
        var result = await f.Reviewer.PostAsJsonAsync($"/api/workflows/{w.Id}/approval", EngineFixture.Decision(w));
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        var approved = await result.Content.ReadFromJsonAsync<WorkflowState>();
        Assert.Equal("reviewer", approved!.ApprovedBy);
    }

    [Fact]
    public async Task UrlHttpLifecycleAndInputFailuresAreCovered()
    {
        await using var f = new ApiFixture();
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Operator.PostAsJsonAsync("/api/urls", new CreateUrlRequest("javascript:alert(1)"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Operator.PostAsJsonAsync("/api/urls", new CreateUrlRequest("https://example.com", 366))).StatusCode);
        var create = await f.Operator.PostAsJsonAsync("/api/urls", new CreateUrlRequest("https://example.com", 30));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        using var body = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var code = body.RootElement.GetProperty("code").GetString();
        var redirect = await f.Anonymous.GetAsync("/" + code);
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.Equal(new Uri("https://example.com"), redirect.Headers.Location);
        using var analytics = JsonDocument.Parse(await f.Operator.GetStringAsync($"/api/urls/{code}/analytics"));
        Assert.Equal(1, analytics.RootElement.GetProperty("clicks").GetInt32());
        Assert.Equal(HttpStatusCode.NoContent, (await f.Operator.DeleteAsync("/api/urls/" + code)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.Anonymous.GetAsync("/" + code)).StatusCode);
    }

    [Fact]
    public async Task HttpReplanRequiresCurrentRevisionAndInvalidatesApproval()
    {
        await using var f = new ApiFixture();
        var w = await f.Create(new("Add analytics", "brownfield"));
        Assert.Equal(HttpStatusCode.Conflict, (await f.Operator.PostAsJsonAsync($"/api/workflows/{w.Id}/replan", new ReplanRequest("Add expiry", 99))).StatusCode);
        var response = await f.Operator.PostAsJsonAsync($"/api/workflows/{w.Id}/replan", new ReplanRequest("Add expiry", 1));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var changed = await response.Content.ReadFromJsonAsync<WorkflowState>();
        Assert.Equal(2, changed!.Revision);
        Assert.Contains("migration", changed.Nodes.Keys);
        Assert.Equal(HttpStatusCode.Conflict, (await f.Reviewer.PostAsJsonAsync($"/api/workflows/{w.Id}/approval", EngineFixture.Decision(w))).StatusCode);
        using var audit = JsonDocument.Parse(await f.Operator.GetStringAsync($"/api/workflows/{w.Id}/audit"));
        Assert.Equal("verified", audit.RootElement.GetProperty("integrity").GetString());
    }

    [Fact]
    public async Task OpenApiDocumentsTheApprovalContractAndSecurity()
    {
        await using var f = new ApiFixture();
        using var spec = JsonDocument.Parse(await f.Anonymous.GetStringAsync("/openapi.json"));
        Assert.Equal("3.0.3", spec.RootElement.GetProperty("openapi").GetString());
        var components = spec.RootElement.GetProperty("components");
        Assert.Equal("X-Api-Key", components.GetProperty("securitySchemes").GetProperty("ApiKey").GetProperty("name").GetString());
        var required = components.GetProperty("schemas").GetProperty("ApprovalRequest").GetProperty("required").EnumerateArray().Select(x => x.GetString());
        Assert.Contains("evidenceHash", required);
        Assert.Contains("revision", required);
        Assert.Equal(HttpStatusCode.OK, (await f.Anonymous.GetAsync("/docs")).StatusCode);
    }

    [Fact]
    public async Task RealWorkflowGeneratesCompilesTestsAndRunsTheCandidateHttpService()
    {
        await using var f = new ApiFixture(realStages: true);
        var w = await f.Create(new("Build HTTPS-only URL shortener with expiration and analytics", SimulateTestFailure: true));
        Assert.True(w.Status == "WaitingApproval", JsonSerializer.Serialize(w));
        using var tests = JsonDocument.Parse(w.Artifacts["tests"]);
        Assert.Equal("dotnet test", tests.RootElement.GetProperty("Runner").GetString());
        Assert.Equal(3, tests.RootElement.GetProperty("Passed").GetInt32());
        Assert.Equal(1, w.Retries);
        Assert.Contains("HttpsOnly = true", await f.Reviewer.GetStringAsync($"/api/workflows/{w.Id}/artifacts/app/FeaturePolicy.cs"));
        Assert.Equal(HttpStatusCode.NotFound, (await f.Reviewer.GetAsync($"/api/workflows/{w.Id}/artifacts/access-keys.json")).StatusCode);
        var workspace = Path.Combine(f.Data, "workspaces", w.Id, "r1");
        await ExerciseCandidateHttp(f.Dotnet, workspace);
        var decision = await f.Reviewer.PostAsJsonAsync($"/api/workflows/{w.Id}/approval", EngineFixture.Decision(w));
        var approved = await decision.Content.ReadFromJsonAsync<WorkflowState>();
        Assert.Equal("Completed", approved!.Status);
        Assert.True(File.Exists(Path.Combine(workspace, "RELEASE.json")));
    }

    [Fact]
    public async Task RealFallbackRunsSharedContractAssertionsWithoutATestFramework()
    {
        await using var f = new ApiFixture(realStages: true);
        var w = await f.Create(new("Build analytics", FailureMode: "runner-unavailable"));
        Assert.True(w.Status == "WaitingApproval", JsonSerializer.Serialize(w));
        Assert.Equal(1, w.Fallbacks);
        Assert.Equal(2, w.Retries);
        using var evidence = JsonDocument.Parse(w.Artifacts["tests"]);
        Assert.Equal("package-free shared contract verifier", evidence.RootElement.GetProperty("Runner").GetString());
        var log = await f.Reviewer.GetStringAsync($"/api/workflows/{w.Id}/artifacts/results/fallback.log");
        Assert.Contains("CONTRACT_CHECKS_PASSED=3", log);
    }

    [Fact]
    public async Task TamperedBinaryAndUnreviewedDataSourceAreRejected()
    {
        await using var f = new ApiFixture(realStages: true);
        var w = await f.Create(new("Build analytics"));
        Assert.True(w.Status == "WaitingApproval", JsonSerializer.Serialize(w));
        var root = Path.Combine(f.Data, "workspaces", w.Id, "r1");
        var binary = Path.Combine(root, "app", "bin", "Debug", "net8.0", "Candidate.dll");
        var original = await File.ReadAllBytesAsync(binary);
        await File.WriteAllBytesAsync(binary, [.. original, 1]);
        await Assert.ThrowsAsync<ValidationException>(() => f.App.Services.GetRequiredService<IEngineeringStages>().VerifyFiles(w));
        await File.WriteAllBytesAsync(binary, original);
        Directory.CreateDirectory(Path.Combine(root, "app", "data"));
        await File.WriteAllTextAsync(Path.Combine(root, "app", "data", "Unreviewed.cs"), "// added after tests");
        var response = await f.Reviewer.PostAsJsonAsync($"/api/workflows/{w.Id}/approval", EngineFixture.Decision(w));
        var stopped = await response.Content.ReadFromJsonAsync<WorkflowState>();
        Assert.Equal("SafeStopped", stopped!.Status);
        Assert.False(File.Exists(Path.Combine(root, "RELEASE.json")));
        Assert.True(File.Exists(Path.Combine(root, "QUARANTINED.json")));
    }

    private static async Task ExerciseCandidateHttp(string dotnet, string workspace)
    {
        var start = new ProcessStartInfo(dotnet) { WorkingDirectory = workspace, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("app/bin/Debug/net8.0/Candidate.dll");
        start.ArgumentList.Add("--urls"); start.ArgumentList.Add("http://127.0.0.1:0");
        start.Environment["CANDIDATE_DATA_DIR"] = Path.Combine(workspace, "app", "data");
        using var process = Process.Start(start)!;
        var listening = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = process.StandardError.ReadToEndAsync();
        var output = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync()) != null)
            {
                const string marker = "Now listening on: ";
                var index = line.IndexOf(marker, StringComparison.Ordinal);
                if (index >= 0) listening.TrySetResult(line[(index + marker.Length)..]);
            }
        });
        try
        {
            var url = await listening.Task.WaitAsync(TimeSpan.FromSeconds(30));
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(url) };
            using var api = JsonDocument.Parse(await client.GetStringAsync("/openapi.json"));
            Assert.False(api.RootElement.GetProperty("paths").TryGetProperty("/api/workflows", out _));
            Assert.False(api.RootElement.GetProperty("components").GetProperty("schemas").TryGetProperty("Workflow", out _));
            Assert.True(api.RootElement.GetProperty("paths").GetProperty("/").GetProperty("get")
                .GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json")
                .GetProperty("schema").GetProperty("properties").TryGetProperty("httpsOnly", out _));
            Assert.Equal("^https://", api.RootElement.GetProperty("components").GetProperty("schemas")
                .GetProperty("CreateUrlRequest").GetProperty("properties").GetProperty("url").GetProperty("pattern").GetString());
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/urls", new CreateUrlRequest("http://example.com"))).StatusCode);
            var created = await client.PostAsJsonAsync("/api/urls", new CreateUrlRequest("https://example.com", 1));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using var link = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            var code = link.RootElement.GetProperty("code").GetString();
            Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/" + code)).StatusCode);
            using var analytics = JsonDocument.Parse(await client.GetStringAsync($"/api/urls/{code}/analytics"));
            Assert.Equal(1, analytics.RootElement.GetProperty("clicks").GetInt32());
            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/urls/" + code)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/" + code)).StatusCode);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await output; await errors;
        }
    }
}
