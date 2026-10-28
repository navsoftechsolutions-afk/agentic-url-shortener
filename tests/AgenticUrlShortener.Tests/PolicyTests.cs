using AgenticUrlShortener;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgenticUrlShortener.Tests;

public class PolicyTests
{
    [Fact]
    public void LocalCredentialsAreDistinctAndPrivateOnUnix()
    {
        using var f = new EngineFixture();
        var config = new ConfigurationBuilder().Build();
        _ = new AccessKeys(f.Options, config);
        var path = Path.Combine(f.DirectoryPath, "access-keys.json");
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        var keys = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!;
        var registry = new AccessKeys(f.Options, config);
        Assert.NotEqual(keys["operator"], keys["reviewer"]);
        Assert.Equal("operator", registry.Authenticate(keys["operator"]));
        Assert.Equal("reviewer", registry.Authenticate(keys["reviewer"]));
        Assert.Null(registry.Authenticate("incorrect"));
    }

    [Fact]
    public void SharedRoleCredentialsAreRejected()
    {
        using var f = new EngineFixture();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["OPERATOR_API_KEY"] = "same", ["REVIEWER_API_KEY"] = "same" }).Build();
        Assert.Throws<InvalidOperationException>(() => new AccessKeys(f.Options, config));
    }

    [Theory]
    [InlineData("../secrets.json")]
    [InlineData("/etc/passwd")]
    public void ArtifactPathsCannotEscapeWorkspace(string path) =>
        Assert.Throws<ValidationException>(() => EngineeringStages.Contained(Path.Combine(Path.GetTempPath(), "workspace"), path));

    [Fact]
    public async Task RealToolFailureCannotBecomeSuccessfulFallbackEvidence()
    {
        using var f = new EngineFixture();
        var stages = new EngineeringStages(f.Options, new FailedTool());
        var engine = new WorkflowEngine(f.Store, stages, f.Journal, f.Options,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkflowEngine>.Instance);
        var w = await engine.Create(new("Build analytics"), "operator");
        Assert.Equal("SafeStopped", w.Status);
        Assert.Equal(0, w.Fallbacks);
        Assert.Equal(1, w.Nodes["tests"].Attempts);
        Assert.Contains("Candidate tests failed", w.Nodes["tests"].Error);
        Assert.True(File.Exists(Path.Combine(f.DirectoryPath, "workspaces", w.Id, "r1", "QUARANTINED.json")));
    }

    private class FailedTool : IToolRunner
    {
        public Task<ToolResult> Run(string directory, string[] arguments) => Task.FromResult(new ToolResult(1, "A contract assertion failed.", 1));
    }
}
