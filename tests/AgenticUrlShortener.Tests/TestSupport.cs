using System.Text.Json;
using AgenticUrlShortener;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgenticUrlShortener.Tests;

public class EngineFixture : IDisposable
{
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "agentic-tests", Guid.NewGuid().ToString("N"));
    public JsonStore<WorkflowCollection> Store { get; }
    public TestStages Stages { get; } = new();
    public WorkflowEngine Engine { get; }
    public EngineeringOptions Options { get; }
    public AuditJournal Journal { get; }
    public EngineFixture(bool failureInjection = true)
    {
        Options = new EngineeringOptions(RepositoryPath(), DirectoryPath, EnableFailureInjection: failureInjection);
        Store = new JsonStore<WorkflowCollection>(Path.Combine(DirectoryPath, "workflows.json"));
        Journal = new AuditJournal(Options);
        Engine = new(Store, Stages, Journal, Options, NullLogger<WorkflowEngine>.Instance);
    }
    public static string RepositoryPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (System.IO.Directory.Exists(Path.Combine(dir.FullName, "templates", "candidate"))) return dir.FullName;
        throw new InvalidOperationException("Repository not found.");
    }
    public static ApprovalRequest Decision(WorkflowState w, bool approved = true) =>
        new(approved, w.Revision, w.ReviewDigest!, Reason: "Reviewed candidate and executed test evidence.");
    public void Dispose() { if (System.IO.Directory.Exists(DirectoryPath)) System.IO.Directory.Delete(DirectoryPath, true); }
}

public class TestStages : IEngineeringStages
{
    public bool Tampered { get; set; }
    public bool ValidationFails { get; set; }
    public bool ProveParallel { get; set; }
    public bool MutateInput { get; set; }
    public int RollbackCount { get; private set; }
    private int _parallelStarted;
    private readonly TaskCompletionSource _bothStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task<StageResult> Execute(WorkflowState w, NodeState n)
    {
        if (MutateInput && n.Id == "documentation")
        {
            w.Artifacts["uncommitted"] = "Sibling-private data";
            w.Nodes["release"].Status = NodeStatus.Completed;
            w.Status = "Completed";
        }
        if (ProveParallel && n.Id is "design" or "security")
        {
            if (Interlocked.Increment(ref _parallelStarted) == 2) _bothStarted.TrySetResult();
            await _bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        if (n.Id == "requirements") return new(JsonSerializer.Serialize(RequirementParser.Parse(w.Requirement, w.Scenario)));
        if (n.Id == "tests")
        {
            if (ValidationFails) throw new ValidationException("Actual candidate test failed.");
            if (w.FailureMode == "runner-unavailable") throw new ToolUnavailableException("Primary unavailable.");
            return new("{\"Passed\":3,\"Failed\":0}");
        }
        return new(JsonSerializer.Serialize(new { Node = n.Id, Requirement = w.Requirement }));
    }
    public Task<StageResult?> Fallback(WorkflowState w, NodeState n, Exception ex) =>
        Task.FromResult<StageResult?>(n.Id == "tests" && ex is ToolUnavailableException ? new("{\"Passed\":3,\"Failed\":0}") : null);
    public Task VerifyFiles(WorkflowState w)
    {
        if (Tampered) throw new ValidationException("Candidate changed after review.");
        return Task.CompletedTask;
    }
    public Task Rollback(WorkflowState w, string reason) { RollbackCount++; return Task.CompletedTask; }
}
