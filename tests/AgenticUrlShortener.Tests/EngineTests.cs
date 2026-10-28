using System.Text.Json;
using AgenticUrlShortener;
using Xunit;

namespace AgenticUrlShortener.Tests;

public class EngineTests
{
    [Fact]
    public async Task StageInputMutationsCannotBypassGovernanceOrLeakToSiblings()
    {
        using var f = new EngineFixture();
        f.Stages.MutateInput = true;
        var w = await f.Engine.Create(new("Build analytics"), "operator");
        Assert.Equal("WaitingApproval", w.Status);
        Assert.Equal(NodeStatus.Pending, w.Nodes["release"].Status);
        Assert.False(w.Artifacts.ContainsKey("uncommitted"));
    }

    [Fact]
    public async Task IndependentBranchesOverlapAndJoinBeforeImplementation()
    {
        using var f = new EngineFixture();
        f.Stages.ProveParallel = true;
        var w = await f.Engine.Create(new("URL shortener with analytics"), "operator");
        Assert.Equal("WaitingApproval", w.Status);
        Assert.True(w.Nodes["design"].StartedUtc <= w.Nodes["security"].EndedUtc);
        Assert.True(w.Nodes["security"].StartedUtc <= w.Nodes["design"].EndedUtc);
        Assert.True(w.Nodes["implementation"].StartedUtc >= w.Nodes["design"].EndedUtc);
        Assert.True(w.Nodes["implementation"].StartedUtc >= w.Nodes["security"].EndedUtc);
        Assert.Equal(NodeStatus.Pending, w.Nodes["release"].Status);
        Assert.Equal(w.Evidence["design"].ContentHash, w.Evidence["implementation"].Inputs["design"]);
        w = await f.Engine.Approve(w.Id, EngineFixture.Decision(w), "reviewer");
        Assert.Equal("Completed", w.Status);
        Assert.All(w.Nodes.Values, n => Assert.Equal(NodeStatus.Completed, n.Status));
        f.Journal.Verify(w);
    }

    [Fact]
    public async Task TransientFailureRecordsAnActualRecoveryInterval()
    {
        using var f = new EngineFixture();
        var w = await f.Engine.Create(new("Build analytics", SimulateTestFailure: true), "operator");
        Assert.Equal(1, w.Retries);
        Assert.Equal(2, w.Nodes["tests"].Attempts);
        Assert.Null(w.Nodes["tests"].Error);
        var incident = Assert.Single(w.Incidents);
        Assert.True(incident.RecoveredUtc > incident.FailedUtc);
        Assert.Equal("Retry", incident.Outcome);
        using var metrics = JsonDocument.Parse(JsonSerializer.Serialize(await f.Engine.Metrics()));
        Assert.True(metrics.RootElement.GetProperty("MeanTimeToRecoveryMs").GetDouble() > 0);
    }

    [Fact]
    public async Task PersistentFailureExhaustsBudgetAndQuarantinesCandidate()
    {
        using var f = new EngineFixture();
        var w = await f.Engine.Create(new("Build", FailureMode: "persistent-tests"), "operator");
        Assert.Equal("SafeStopped", w.Status);
        Assert.Equal(3, w.Nodes["tests"].Attempts);
        Assert.Equal(2, w.Retries);
        Assert.Equal(0, w.Fallbacks);
        Assert.Equal(1, f.Stages.RollbackCount);
        Assert.Equal(NodeStatus.Pending, w.Nodes["release"].Status);
        Assert.Null(Assert.Single(w.Incidents).RecoveredUtc);
        Assert.Contains(w.Audit, e => e.Event == "safe_stop");
        var replanned = await f.Engine.Replan(w.Id, new("Add analytics", 1), "operator");
        Assert.Equal("WaitingApproval", replanned.Status);
        Assert.Equal("none", replanned.FailureMode);
    }

    [Fact]
    public async Task RunnerOutageUsesFallbackButTestFailureCannot()
    {
        using var f = new EngineFixture();
        var w = await f.Engine.Create(new("Build", FailureMode: "runner-unavailable"), "operator");
        Assert.Equal("WaitingApproval", w.Status);
        Assert.Equal(1, w.Fallbacks);
        Assert.Equal("Fallback", Assert.Single(w.Incidents).Outcome);
        f.Stages.ValidationFails = true;
        var failed = await f.Engine.Create(new("Build"), "operator");
        Assert.Equal("SafeStopped", failed.Status);
        Assert.Equal(1, failed.Nodes["tests"].Attempts);
        Assert.Equal(0, failed.Fallbacks);
    }

    [Fact]
    public async Task ReplanRetainsHistoryAndChangesGraphForExpiration()
    {
        using var f = new EngineFixture();
        var w = await f.Engine.Create(new("Add analytics", "brownfield"), "operator");
        var oldHash = w.ReviewDigest;
        Assert.False(w.Nodes.ContainsKey("migration"));
        w = await f.Engine.Replan(w.Id, new("Add expiration and analytics", 1), "operator");
        Assert.Equal(2, w.Revision);
        Assert.Equal("WaitingApproval", w.Status);
        Assert.Contains("migration", w.Nodes["implementation"].Dependencies);
        Assert.Equal(NodeStatus.Completed, w.Nodes["migration"].Status);
        Assert.Equal(oldHash, Assert.Single(w.History).ReviewDigest);
        Assert.NotEqual(oldHash, w.ReviewDigest);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Engine.Approve(w.Id,
            new(true, 1, oldHash!, Reason: "Stale decision"), "reviewer"));
    }

    [Fact]
    public async Task AmbiguousSecurityRequirementStopsUntilClarified()
    {
        using var f = new EngineFixture();
        var w = await f.Engine.Create(new("Make links secure", "ambiguous"), "operator");
        Assert.Equal("WaitingClarification", w.Status);
        Assert.Equal(NodeStatus.Pending, w.Nodes["implementation"].Status);
        Assert.Null(w.ReviewDigest);
        w = await f.Engine.Replan(w.Id, new("Require HTTPS-only links and analytics", 1), "operator");
        Assert.Equal("WaitingApproval", w.Status);
    }

    [Fact]
    public async Task RejectionIsTerminalUntilExplicitReplan()
    {
        using var f = new EngineFixture();
        var w = await f.Engine.Create(new("Build"), "operator");
        w = await f.Engine.Approve(w.Id, EngineFixture.Decision(w, false), "reviewer");
        Assert.Equal("Rejected", w.Status);
        Assert.Equal(1, w.Rollbacks);
        Assert.Equal(NodeStatus.Pending, w.Nodes["release"].Status);
        Assert.Equal("Rejected", (await f.Engine.Advance(w.Id)).Status);
        Assert.False(w.Artifacts.ContainsKey("release"));
    }

    [Fact]
    public async Task TamperedCandidateCannotBeApproved()
    {
        using var f = new EngineFixture();
        var w = await f.Engine.Create(new("Build"), "operator");
        f.Stages.Tampered = true;
        w = await f.Engine.Approve(w.Id, EngineFixture.Decision(w), "reviewer");
        Assert.Equal("SafeStopped", w.Status);
        Assert.Null(w.ApprovedBy);
        Assert.Equal(NodeStatus.Pending, w.Nodes["release"].Status);
    }

    [Fact]
    public async Task TruncatedJournalBlocksApproval()
    {
        using var f = new EngineFixture();
        var w = await f.Engine.Create(new("Build"), "operator");
        var path = Path.Combine(f.DirectoryPath, "audit", w.Id + ".jsonl");
        var lines = await File.ReadAllLinesAsync(path);
        await File.WriteAllLinesAsync(path, lines.SkipLast(1));
        await Assert.ThrowsAsync<ValidationException>(() => f.Engine.Approve(w.Id, EngineFixture.Decision(w), "reviewer"));
        Assert.Equal("WaitingApproval", (await f.Engine.Get(w.Id))!.Status);
    }

    [Fact]
    public async Task StateArtifactTamperingInvalidatesReviewDigest()
    {
        using var f = new EngineFixture();
        var w = await f.Engine.Create(new("Build"), "operator");
        await f.Store.Update(data =>
        {
            data.Items[w.Id].Evidence["design"] = data.Items[w.Id].Evidence["design"] with { ContentHash = "modified" };
            return true;
        });
        w = await f.Engine.Approve(w.Id, EngineFixture.Decision(w), "reviewer");
        Assert.Equal("SafeStopped", w.Status);
    }

    [Fact]
    public async Task OnlyOneConcurrentApprovalCanSucceed()
    {
        using var f = new EngineFixture();
        var w = await f.Engine.Create(new("Build"), "operator");
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            Record.ExceptionAsync(() => f.Engine.Approve(w.Id, EngineFixture.Decision(w), "reviewer"))));
        Assert.Single(results, x => x is null);
        Assert.Single(results, x => x is InvalidOperationException);
        Assert.Equal("Completed", (await f.Engine.Get(w.Id))!.Status);
    }

    [Fact]
    public async Task CreatorCannotApproveAndCompletedRunsCannotBeReplanned()
    {
        using var f = new EngineFixture();
        var w = await f.Engine.Create(new("Build"), "operator");
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Engine.Approve(w.Id, EngineFixture.Decision(w), "operator"));
        w = await f.Engine.Approve(w.Id, EngineFixture.Decision(w), "reviewer");
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Engine.Replan(w.Id, new("Change", 1), "operator"));
    }

    [Fact]
    public async Task FailureInjectionMustBeExplicitlyEnabled()
    {
        using var f = new EngineFixture(false);
        await Assert.ThrowsAsync<ArgumentException>(() => f.Engine.Create(new("Build", SimulateTestFailure: true), "operator"));
    }

    [Fact]
    public async Task InterruptedExecutionIsSafelyStoppedOnRestart()
    {
        using var f = new EngineFixture();
        var w = await f.Engine.Create(new("Build"), "operator");
        await f.Store.Update(data => { data.Items[w.Id].Status = "Running"; return true; });
        await f.Engine.RecoverInterrupted();
        Assert.Equal("SafeStopped", (await f.Engine.Get(w.Id))!.Status);
    }

    [Fact]
    public async Task RestartRecoversDurableJournalTailBeforeSafeStop()
    {
        using var f = new EngineFixture();
        var w = await f.Engine.Create(new("Build"), "operator");
        await f.Store.Update(data => { data.Items[w.Id].Status = "Running"; return true; });
        // Simulate a crash after a journal write but before the next state snapshot.
        f.Journal.Append(w, "start", "release");
        await f.Engine.RecoverInterrupted();
        var recovered = (await f.Engine.Get(w.Id))!;
        Assert.Equal("SafeStopped", recovered.Status);
        Assert.Contains(recovered.Audit, e => e.Event == "start" && e.Detail == "release");
        f.Journal.Verify(recovered);
    }

    [Fact]
    public async Task InterruptedWorkflowWithCorruptJournalCannotResume()
    {
        using var f = new EngineFixture();
        var w = await f.Engine.Create(new("Build"), "operator");
        await f.Store.Update(data => { data.Items[w.Id].Status = "Running"; return true; });
        await File.AppendAllTextAsync(Path.Combine(f.DirectoryPath, "audit", w.Id + ".jsonl"), "not-json\n");
        await Assert.ThrowsAsync<ValidationException>(() => f.Engine.RecoverInterrupted());
    }
}
