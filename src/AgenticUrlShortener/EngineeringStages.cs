using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace AgenticUrlShortener;

public static class RequirementParser
{
    public static RequirementSpec Parse(string requirement, string scenario)
    {
        var text = requirement.ToLowerInvariant();
        var analytics = text.Contains("analytics") || text.Contains("click");
        var expiration = text.Contains("expir") || text.Contains("expiry");
        var https = text.Contains("https-only") || text.Contains("https only");
        var unclear = (text.Contains("secure") || scenario == "ambiguous") &&
            !https && !text.Contains("http(s)") && !text.Contains("reject credentials");
        return new(requirement, analytics, expiration, https, unclear,
            unclear ? ["Define security acceptance criteria: for example HTTPS-only targets and rejection of URL credentials."] : [],
            ["Create unique short links and redirect without losing clicks", "Reject unsupported URL schemes and credentials",
                analytics ? "Expose daily click analytics" : "Analytics endpoint is disabled",
                expiration ? "Reject expired links; expiry range is 1-365 days" : "Reject expiry parameters until expiration is requested",
                https ? "Reject plain HTTP targets" : "Accept HTTP(S) targets", "Require separate human approval of tested evidence"]);
    }
}

public class EngineeringStages(EngineeringOptions options, IToolRunner tools) : IEngineeringStages
{
    private static readonly string[] SourceFiles = ["UrlService.cs", "UrlModels.cs", "JsonStore.cs"];
    private readonly SemaphoreSlim _toolGate = new(1, 1);
    private string Root(WorkflowState w) => Path.Combine(options.DataDirectory, "workspaces", w.Id, "r" + w.Revision);
    private static string Json(object value) => JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
    private static T Read<T>(WorkflowState w, string stage) => JsonSerializer.Deserialize<T>(w.Artifacts[stage])!;
    private static RequirementSpec Spec(WorkflowState w) => Read<RequirementSpec>(w, "requirements");
    private string Source(string file) => Path.Combine(options.RepositoryRoot, "src", "AgenticUrlShortener", file);

    public async Task<StageResult> Execute(WorkflowState w, NodeState n) => n.Id switch
    {
        "requirements" => new(Json(RequirementParser.Parse(w.Requirement, w.Scenario))),
        "planning" => new(Json(new { Requirement = Spec(w), Tasks = w.Nodes.Values.Select(x => new
            { x.Id, x.Dependencies, ExitGate = x.Id == "tests" ? "Executed contract tests must pass" : "Hashed artifact and recorded inputs" }) })),
        "design" => await Inspect(w),
        "security" => await Security(w),
        "migration" => await CheckCompatibility(w),
        "implementation" => await Generate(w),
        "tests" => await Test(w),
        "documentation" => await Document(w),
        "review" => await Review(w),
        "approval" => new(Json(new { Revision = w.Revision, EvidenceHash = w.ReviewDigest,
            RequiredRole = "reviewer", CreatorCannotApprove = true, ReleaseIsDeployment = false })),
        "release" => await Release(w),
        _ => throw new ValidationException("Unknown stage.")
    };

    private async Task<StageResult> Inspect(WorkflowState w)
    {
        // Read the maintained implementation, not a hardcoded claim about its contents.
        var inspected = await Task.WhenAll(SourceFiles.Select(async file =>
        {
            var content = await File.ReadAllTextAsync(Source(file));
            return new InspectedFile(file, Digests.Of(content), content.Split('\n').Length);
        }));
        return new(Json(new DesignReport(inspected, Spec(w), w.Scenario == "brownfield"
            ? "Extend existing UrlService and preserve stored link compatibility; optional feature policy is generated separately."
            : "Compose the inspected service with generated feature policy and HTTP endpoints in a separate candidate.")));
    }

    private async Task<StageResult> Security(WorkflowState w)
    {
        var source = await File.ReadAllTextAsync(Source("UrlService.cs"));
        var store = await File.ReadAllTextAsync(Source("JsonStore.cs"));
        // These are deliberately narrow source assertions, backed by runtime contract tests below.
        var checks = new Dictionary<string, bool>
        {
            ["cryptographic_codes"] = source.Contains("RandomNumberGenerator.GetBytes"),
            ["url_credentials_rejected"] = source.Contains("uri.UserInfo.Length == 0"),
            ["bounded_expiry"] = source.Contains("< 1 or > 365"),
            ["atomic_replace"] = store.Contains("File.Move"),
            ["no_shell_commands_from_requirements"] = !Spec(w).NeedsClarification
        };
        if (checks.Values.Any(ok => !ok)) throw new ValidationException("A required source-policy check failed.");
        return new(Json(new { Checks = checks, SourceHash = Digests.Of(source),
            Guardrails = new[] { "Trusted templates only", "Isolated revision directory", "Fixed dotnet argument lists", "Authenticated separate reviewer" },
            RemainingRisks = new[] { "Redirect abuse and private-network destinations", "Single-process JSON persistence", "API keys require TLS beyond loopback" } }));
    }

    private async Task<StageResult> Generate(WorkflowState w)
    {
        var design = Read<DesignReport>(w, "design");
        var spec = Spec(w);
        var files = new Dictionary<string, string>();
        foreach (var inspected in design.Files)
        {
            var content = await File.ReadAllTextAsync(Source(inspected.Path));
            if (Digests.Of(content) != inspected.Hash) throw new ValidationException("Repository changed after design inspection; replan required.");
            await Write(w, "app/" + inspected.Path, content, files);
        }
        var templates = new Dictionary<string, string>
        {
            ["app/Candidate.csproj"] = "Candidate.csproj.txt", ["app/Program.cs"] = "Program.cs.txt",
            ["tests/Candidate.Tests.csproj"] = "Candidate.Tests.csproj.txt", ["tests/ContractTests.cs"] = "ContractTests.cs.txt",
            ["verifier/Verifier.csproj"] = "Verifier.csproj.txt", ["verifier/Program.cs"] = "Verifier.cs.txt",
            ["checks/ContractChecks.cs"] = "ContractChecks.cs.txt"
        };
        foreach (var (target, template) in templates)
            await Write(w, target, await File.ReadAllTextAsync(Path.Combine(options.RepositoryRoot, "templates", "candidate", template)), files);
        var policy = $$"""
            namespace AgenticUrlShortener;
            // Generated from normalized requirements; no requirement text is executed.
            public static class FeaturePolicy
            {
                public static readonly bool Analytics = {{spec.Analytics.ToString().ToLowerInvariant()}};
                public static readonly bool Expiration = {{spec.Expiration.ToString().ToLowerInvariant()}};
                public static readonly bool HttpsOnly = {{spec.HttpsOnly.ToString().ToLowerInvariant()}};
                public static void Validate(CreateUrlRequest request)
                {
                    if (!UrlService.IsSafeUrl(request.Url)) throw new ArgumentException("Only HTTP(S) URLs without credentials are allowed.");
                    if (HttpsOnly && !request.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("This candidate requires HTTPS targets.");
                    if (!Expiration && request.ExpiresInDays != null) throw new ArgumentException("Expiration was not requested.");
                }
            }
            """;
        await Write(w, "app/FeaturePolicy.cs", policy, files);
        await Write(w, "requirements.json", Json(spec), files);
        var schema = await CandidateSchema(spec);
        await Write(w, "api.json", schema, files);
        await Write(w, "app/openapi.json", schema, files);
        return new(Json(new { Candidate = "app/Candidate.csproj", GeneratedFiles = files,
            BasedOn = w.Evidence["design"].ContentHash, FeaturePolicy = spec,
            Scope = "Trusted-template generation; maintained repository is unchanged. Candidate must be tested and reviewed." }), files);
    }

    private async Task<string> CandidateSchema(RequirementSpec spec)
    {
        var document = JsonNode.Parse(await File.ReadAllTextAsync(Source("openapi.json")))!.AsObject();
        document["info"] = JsonSerializer.SerializeToNode(new { title = "Generated URL shortener candidate", version = "1.0.0",
            description = "Unauthenticated local review artifact; not deployed." });
        document["servers"] = JsonSerializer.SerializeToNode(new[] { new { url = "http://localhost:5081" } });
        var paths = document["paths"]!.AsObject();
        var allowed = new[] { "/", "/api/urls", "/api/urls/{code}", "/api/urls/{code}/analytics", "/{code}", "/openapi.json" };
        foreach (var path in paths.Select(p => p.Key).ToList())
            if (!allowed.Contains(path) || (!spec.Analytics && path.EndsWith("/analytics"))) paths.Remove(path);
        foreach (var path in paths)
            foreach (var operation in path.Value!.AsObject())
            {
                operation.Value!["security"] = new JsonArray();
                operation.Value.AsObject().Remove("description");
                operation.Value["responses"]!.AsObject().Remove("401");
                operation.Value["responses"]!.AsObject().Remove("403");
            }
        document["components"]!.AsObject().Remove("securitySchemes");
        var schemas = document["components"]!["schemas"]!.AsObject();
        foreach (var name in schemas.Select(item => item.Key).ToList())
            if (name is not ("CreateUrlRequest" or "ShortLinkResponse" or "Analytics" or "Error")) schemas.Remove(name);
        paths["/"]!["get"]!["responses"]!["200"]!["content"]!["application/json"]!["schema"] =
            JsonSerializer.SerializeToNode(new { type = "object", properties = new
            {
                service = new { type = "string" }, analytics = new { type = "boolean" },
                expiration = new { type = "boolean" }, httpsOnly = new { type = "boolean" }
            } });
        var properties = document["components"]!["schemas"]!["CreateUrlRequest"]!["properties"]!;
        if (spec.HttpsOnly) properties["url"]!["pattern"] = "^https://";
        if (!spec.Expiration) properties["expiresInDays"]!["enum"] = new JsonArray((JsonNode?)null);
        document["x-features"] = JsonSerializer.SerializeToNode(spec);
        return document.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private async Task<StageResult> CheckCompatibility(WorkflowState w)
    {
        var models = await File.ReadAllTextAsync(Source("UrlModels.cs"));
        if (!models.Contains("DateTime? ExpiresUtc")) throw new ValidationException("Expiration schema requires a nullable field.");
        var existing = new JsonStore<UrlData>(Path.Combine(options.DataDirectory, "urls.json"));
        var count = await existing.Read(data => data.Links.Count);
        return new(Json(new { Compatible = true, ExistingLinksRead = count,
            Change = "Optional ExpiresUtc; existing links remain non-expiring.",
            Inputs = w.Evidence["design"].ContentHash, SchemaHash = Digests.Of(models), DestructiveMigration = false }));
    }

    private async Task<StageResult> Test(WorkflowState w)
    {
        await VerifyFiles(w);
        if (w.FailureMode == "runner-unavailable") throw new ToolUnavailableException("Injected primary test-runner outage.");
        await _toolGate.WaitAsync();
        try
        {
            var result = await tools.Run(Root(w), ["test", "tests/Candidate.Tests.csproj", "--logger", "trx;LogFileName=tests.trx", "--results-directory", "results"]);
            var files = new Dictionary<string, string>();
            await Write(w, "results/primary.log", result.Output, files);
            if (result.ExitCode != 0) throw new ValidationException("Candidate tests failed. Review results/primary.log; log hash=" +
                files["results/primary.log"] + "; failing tests cannot use fallback.");
            var reportPath = Path.Combine(Root(w), "results", "tests.trx");
            var report = XDocument.Load(reportPath);
            var counters = report.Descendants().First(e => e.Name.LocalName == "Counters");
            var total = int.Parse(counters.Attribute("total")!.Value);
            var passed = int.Parse(counters.Attribute("passed")!.Value);
            if (total == 0 || passed != total) throw new ValidationException("Test evidence is incomplete or contains failures.");
            files["results/tests.trx"] = Digests.OfBytes(await File.ReadAllBytesAsync(reportPath));
            await RecordBinaries(w, files);
            return new(Json(new { Runner = "dotnet test", Total = total, Passed = passed,
                Failed = 0, result.DurationMs, Implementation = w.Evidence["implementation"].ContentHash }), files);
        }
        finally { _toolGate.Release(); }
    }

    public async Task<StageResult?> Fallback(WorkflowState w, NodeState node, Exception failure)
    {
        if (node.Id != "tests" || failure is not ToolUnavailableException) return null;
        await VerifyFiles(w);
        await _toolGate.WaitAsync();
        try
        {
            var result = await tools.Run(Root(w), ["run", "--project", "verifier/Verifier.csproj"]);
            var files = new Dictionary<string, string>();
            await Write(w, "results/fallback.log", result.Output, files);
            if (result.ExitCode != 0 || !result.Output.Contains("CONTRACT_CHECKS_PASSED=3"))
                throw new ValidationException("Package-free contract verifier failed; release blocked.");
            await RecordBinaries(w, files);
            return new(Json(new { Runner = "package-free shared contract verifier", Total = 3, Passed = 3,
                Failed = 0, result.DurationMs, PrimaryUnavailable = true,
                Implementation = w.Evidence["implementation"].ContentHash }), files);
        }
        finally { _toolGate.Release(); }
    }

    private async Task<StageResult> Document(WorkflowState w)
    {
        var files = new Dictionary<string, string>();
        await Write(w, "REVIEW.md", $"# Candidate revision {w.Revision}\n\n" +
            $"Requirement: {Spec(w).Intent}\n\n" +
            "Inspect app/FeaturePolicy.cs, app/UrlService.cs, requirements.json, api.json and results/.\n\n" +
            "Run: dotnet test tests/Candidate.Tests.csproj\n\nRun service: dotnet run --project app/Candidate.csproj\n\n" +
            "Generated application is a review artifact. No public deployment is performed.\n\n" +
            "Trade-offs: local JSON store, no distributed transaction, redirect abuse remains a risk.\n", files);
        return new(Json(new { Document = "REVIEW.md", Implementation = w.Evidence["implementation"].ContentHash,
            AcceptanceCriteria = Spec(w).AcceptanceCriteria }), files);
    }

    private async Task<StageResult> Review(WorkflowState w)
    {
        await VerifyFiles(w);
        using var test = JsonDocument.Parse(w.Artifacts["tests"]);
        if (test.RootElement.GetProperty("Failed").GetInt32() != 0 || test.RootElement.GetProperty("Passed").GetInt32() < 3)
            throw new ValidationException("Review requires passing executed tests.");
        foreach (var (id, evidence) in w.Evidence)
        {
            if (evidence.Revision != w.Revision || evidence.ContentHash != Digests.Of(w.Artifacts[id]))
                throw new ValidationException("Artifact content or revision changed.");
            foreach (var input in evidence.Inputs)
                if (w.Evidence[input.Key].ContentHash != input.Value) throw new ValidationException("Dependency lineage changed.");
        }
        return new(Json(new { Gates = new[] { "source policy checked", "candidate files verified", "contract tests executed", "input lineage verified" },
            TestEvidence = w.Evidence["tests"].ContentHash, Documentation = w.Evidence["documentation"].ContentHash,
            HumanDecisionRequired = true }));
    }

    private async Task<StageResult> Release(WorkflowState w)
    {
        if (w.ApprovedBy == null || w.ReviewDigest != Digests.Review(w)) throw new ValidationException("Release approval evidence is invalid.");
        await VerifyFiles(w);
        var files = new Dictionary<string, string>();
        await Write(w, "RELEASE.json", Json(new { w.Id, w.Revision, w.ApprovedBy, w.ReviewDigest,
            ApprovedUtc = DateTime.UtcNow, DeploymentPerformed = false }), files);
        return new(Json(new { Manifest = "RELEASE.json", w.ApprovedBy, w.ReviewDigest, DeploymentPerformed = false }), files);
    }

    public async Task VerifyFiles(WorkflowState w)
    {
        if (w.Evidence.TryGetValue("implementation", out var implementation))
        {
            foreach (var directory in new[] { "app", "tests", "checks", "verifier" })
            {
                var sourceRoot = Path.Combine(Root(w), directory);
                if (!Directory.Exists(sourceRoot)) throw new ValidationException("Candidate source directory disappeared.");
                foreach (var path in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(Root(w), path).Replace(Path.DirectorySeparatorChar, '/');
                    if (relative.StartsWith(directory + "/bin/", StringComparison.Ordinal) ||
                        relative.StartsWith(directory + "/obj/", StringComparison.Ordinal)) continue;
                    if (relative is "app/data/urls.json" or "app/data/urls.json.tmp") continue;
                    if (!implementation.Files.ContainsKey(relative)) throw new ValidationException("Unreviewed candidate file: " + relative);
                }
            }
            foreach (var file in Directory.EnumerateFiles(Root(w)))
                if (Path.GetExtension(file) is ".props" or ".targets" or ".cs" or ".csproj")
                    throw new ValidationException("Unreviewed build input in candidate root.");
        }
        foreach (var file in w.Evidence.Values.SelectMany(e => e.Files))
        {
            var path = Contained(Root(w), file.Key);
            if (!File.Exists(path) || Digests.OfBytes(await File.ReadAllBytesAsync(path)) != file.Value)
                throw new ValidationException("Candidate file changed or disappeared: " + file.Key);
        }
    }

    private async Task RecordBinaries(WorkflowState w, Dictionary<string, string> files)
    {
        foreach (var name in new[] { "Candidate.dll", "Candidate.deps.json", "Candidate.runtimeconfig.json" })
        {
            var relative = "app/bin/Debug/net8.0/" + name;
            files[relative] = Digests.OfBytes(await File.ReadAllBytesAsync(Path.Combine(Root(w), relative)));
        }
    }

    public async Task Rollback(WorkflowState w, string reason)
    {
        var root = Root(w);
        if (!Directory.Exists(root)) return;
        var release = Path.Combine(root, "RELEASE.json");
        if (File.Exists(release)) File.Move(release, Path.Combine(root, "REVOKED-RELEASE.json"), true);
        await File.WriteAllTextAsync(Path.Combine(root, "QUARANTINED.json"), Json(new { w.Revision, Reason = reason, AtUtc = DateTime.UtcNow }));
    }

    private async Task Write(WorkflowState w, string relative, string content, Dictionary<string, string> files)
    {
        var path = Contained(Root(w), relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
        files[relative] = Digests.Of(content);
    }
    public static string Contained(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ValidationException("Artifact path escapes its workspace.");
        return path;
    }
    public record InspectedFile(string Path, string Hash, int Lines);
    public record DesignReport(InspectedFile[] Files, RequirementSpec Requirement, string Decision);
}
