using System.Collections.Concurrent;
using System.Text.Json;

namespace AgenticUrlShortener;

public class WorkflowEngine(JsonStore<WorkflowCollection> store, IEngineeringStages stages,
    AuditJournal journal, EngineeringOptions options, ILogger<WorkflowEngine> logger)
{
    public static Dictionary<string, string[]> Graph() => new()
    {
        ["requirements"] = [], ["planning"] = ["requirements"],
        ["design"] = ["planning"], ["security"] = ["planning"],
        ["implementation"] = ["design", "security"], ["tests"] = ["implementation"],
        ["documentation"] = ["implementation"], ["review"] = ["tests", "documentation"],
        ["approval"] = ["review"], ["release"] = ["approval"]
    };
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly SemaphoreSlim _capacity = new(2, 2);
    private static WorkflowState Snapshot(WorkflowState w)
    {
        lock (w) return JsonSerializer.Deserialize<WorkflowState>(JsonSerializer.Serialize(w))!;
    }
    private Task Save(WorkflowState w) => store.Update(data => { data.Items[w.Id] = w; return true; });
    public Task<WorkflowState?> Get(string id) => store.Read(data => data.Items.GetValueOrDefault(id));
    public Task<List<WorkflowState>> All() => store.Read(data => data.Items.Values.OrderByDescending(w => w.StartedUtc).ToList());
    private static void ValidateRequirement(string? requirement)
    {
        if (string.IsNullOrWhiteSpace(requirement) || requirement.Length > 4000)
            throw new ArgumentException("Requirement must be 1-4000 characters.");
    }

    public async Task<WorkflowState> Create(WorkflowRequest req, string actor)
    {
        ValidateRequirement(req.Requirement);
        var scenario = req.Scenario?.ToLowerInvariant();
        if (scenario is not ("greenfield" or "brownfield" or "ambiguous")) throw new ArgumentException("Unknown scenario.");
        if (req.FailureMode is not ("none" or "persistent-tests" or "runner-unavailable")) throw new ArgumentException("Unknown failure mode.");
        if ((req.SimulateTestFailure || req.FailureMode != "none") && !options.EnableFailureInjection)
            throw new ArgumentException("Failure injection is disabled. Enable it explicitly for demonstrations.");
        var w = new WorkflowState
        {
            SchemaVersion = 2, Requirement = req.Requirement, Scenario = scenario, CreatedBy = actor,
            SimulateTestFailure = req.SimulateTestFailure, FailureMode = req.FailureMode,
            Nodes = Graph().ToDictionary(x => x.Key, x => new NodeState { Id = x.Key, Dependencies = x.Value })
        };
        journal.Append(w, "created", "Requirement accepted; isolated artifact generation only; no deployment.", actor);
        await Save(w);
        return await Advance(w.Id);
    }

    public async Task<WorkflowState> Advance(string id)
    {
        await _capacity.WaitAsync();
        var gate = _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try { return await AdvanceLocked(await Get(id) ?? throw new KeyNotFoundException()); }
        finally { gate.Release(); _capacity.Release(); }
    }

    private async Task<WorkflowState> AdvanceLocked(WorkflowState w)
    {
        journal.Verify(w);
        if (w.Status is "Rejected" or "Completed" or "SafeStopped" or "WaitingClarification") return w;
        while (true)
        {
            var ready = w.Nodes.Values.Where(n => n.Status == NodeStatus.Pending &&
                n.Dependencies.All(d => w.Nodes[d].Status == NodeStatus.Completed)).ToList();
            if (ready.Count == 0) break;
            // Every task yields before its stage starts, so independent asynchronous tools can overlap.
            await Task.WhenAll(ready.Select(n => Execute(w, n)));
            if (w.Status == "SafeStopped") { await Save(w); return w; }
            if (w.Nodes["requirements"].Status == NodeStatus.Completed && w.Nodes["planning"].Status == NodeStatus.Pending)
            {
                var spec = JsonSerializer.Deserialize<RequirementSpec>(w.Artifacts["requirements"])!;
                if (spec.NeedsClarification)
                {
                    w.Status = "WaitingClarification";
                    journal.Append(w, "clarification_required", string.Join(" ", spec.Questions));
                    await Save(w);
                    return w;
                }
                if (w.Scenario == "brownfield" && spec.Expiration)
                {
                    w.Nodes["migration"] = new NodeState { Id = "migration", Dependencies = ["design"] };
                    w.Nodes["implementation"].Dependencies = ["design", "security", "migration"];
                    journal.Append(w, "graph_changed", "Expiration enhancement requires a data compatibility gate.");
                }
            }
            await Save(w);
        }
        if (w.Nodes["approval"].Status == NodeStatus.WaitingApproval) w.Status = "WaitingApproval";
        else if (w.Nodes.Values.All(n => n.Status == NodeStatus.Completed))
        {
            w.Status = "Completed";
            w.EndedUtc = DateTime.UtcNow;
            journal.Append(w, "complete", "Candidate and validation evidence approved; no deployment performed.");
        }
        await Save(w);
        return w;
    }

    private async Task Execute(WorkflowState w, NodeState n)
    {
        await Task.Yield();
        lock (w) { n.Status = NodeStatus.Running; n.StartedUtc = DateTime.UtcNow; }
        journal.Append(w, "start", n.Id);
        Exception? failure = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            n.Attempts++;
            try
            {
                if (n.Id == "tests")
                {
                    if (w.SimulateTestFailure && !w.FailureConsumed)
                    { w.FailureConsumed = true; throw new RetryableStageException("Injected transient test runner failure."); }
                    if (w.FailureMode == "persistent-tests") throw new RetryableStageException("Injected persistent test runner failure.");
                }
                // Stages consume a private snapshot and publish only their returned result.
                // Parallel siblings cannot enumerate or mutate one another's live dictionaries.
                var input = Snapshot(w);
                var result = await stages.Execute(input, input.Nodes[n.Id]);
                if (n.Id == "review") await stages.VerifyFiles(Snapshot(w));
                Complete(w, n, result);
                return;
            }
            catch (Exception ex)
            {
                failure = ex;
                n.Error = ex.Message;
                lock (w)
                {
                    if (!w.Incidents.Any(i => i.Node == n.Id && i.Revision == w.Revision))
                        w.Incidents.Add(new RecoveryIncident { Node = n.Id, Revision = w.Revision, FailedUtc = DateTime.UtcNow });
                }
                journal.Append(w, "error", $"{n.Id} attempt={attempt}: {ex.Message}");
                if (ex is not RetryableStageException || attempt == 3) break;
                lock (w) w.Retries++;
                journal.Append(w, "retry", $"{n.Id} bounded retry after attempt={attempt}");
                await Task.Delay(40 * attempt);
            }
        }
        try
        {
            var fallbackInput = Snapshot(w);
            var fallback = await stages.Fallback(fallbackInput, fallbackInput.Nodes[n.Id], failure!);
            if (fallback != null)
            {
                lock (w) w.Fallbacks++;
                journal.Append(w, "fallback", $"{n.Id}: package-free contract verifier; same assertions, no test-failure bypass.");
                Complete(w, n, fallback, "Fallback");
                return;
            }
        }
        catch (Exception ex) { failure = ex; n.Error = ex.Message; }
        lock (w) { n.Status = NodeStatus.Failed; n.EndedUtc = DateTime.UtcNow; }
        await SafeStop(w, $"{n.Id}: {failure!.Message}");
    }

    private void Complete(WorkflowState w, NodeState n, StageResult result, string outcome = "Retry")
    {
        lock (w)
        {
            n.Output = result.Output;
            n.Error = null;
            n.EndedUtc = DateTime.UtcNow;
            w.Artifacts[n.Id] = result.Output;
            w.Evidence[n.Id] = new StageEvidence(w.Revision, Digests.Of(result.Output),
                n.Dependencies.ToDictionary(d => d, d => w.Evidence[d].ContentHash), result.Files ?? new());
            n.Status = n.Id == "approval" ? NodeStatus.WaitingApproval : NodeStatus.Completed;
            var incident = w.Incidents.SingleOrDefault(i => i.Node == n.Id && i.Revision == w.Revision);
            if (incident != null) { incident.RecoveredUtc = DateTime.UtcNow; incident.Outcome = outcome; }
            if (n.Id == "review") w.ReviewDigest = Digests.Review(w);
        }
        journal.Append(w, n.Id == "approval" ? "approval_required" : "success", $"{n.Id} attempts={n.Attempts}");
        if (n.Id == "review") journal.Append(w, "review_sealed", w.ReviewDigest!);
    }

    private async Task SafeStop(WorkflowState w, string reason)
    {
        lock (w) { w.Status = "SafeStopped"; w.EndedUtc = DateTime.UtcNow; w.Rollbacks++; }
        await stages.Rollback(w, reason);
        journal.Append(w, "safe_stop", reason);
        logger.LogWarning("Workflow {Id} safely stopped: {Reason}", w.Id, reason);
    }

    public async Task<WorkflowState> Approve(string id, ApprovalRequest req, string actor)
    {
        var gate = _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            var w = await Get(id) ?? throw new KeyNotFoundException();
            journal.Verify(w);
            if (w.Status != "WaitingApproval") throw new InvalidOperationException("Workflow is not awaiting approval.");
            if (actor == w.CreatedBy) throw new InvalidOperationException("The creator cannot approve their own workflow.");
            if (req.Revision != w.Revision || req.EvidenceHash != w.ReviewDigest)
                throw new InvalidOperationException("Stale revision or evidence hash; review the current evidence.");
            if (req.Reviewer != null && req.Reviewer != actor) throw new ArgumentException("Reviewer must match authenticated identity.");
            if (string.IsNullOrWhiteSpace(req.Reason)) throw new ArgumentException("Decision reason is required.");
            if (req.Approved)
            {
                try
                {
                    if (w.ReviewDigest != Digests.Review(w)) throw new ValidationException("Evidence digest changed after review.");
                    if (w.Audit.LastOrDefault(e => e.Event == "review_sealed" && e.Revision == w.Revision)?.Detail != w.ReviewDigest)
                        throw new ValidationException("Evidence digest does not match the recorded review seal.");
                    await stages.VerifyFiles(w);
                }
                catch (ValidationException ex)
                {
                    await SafeStop(w, ex.Message);
                    await Save(w);
                    return w;
                }
                w.ApprovedBy = actor;
                w.Nodes["approval"].Status = NodeStatus.Completed;
                w.Status = "Running";
                journal.Append(w, "approved", $"revision={w.Revision} evidence={w.ReviewDigest}; {req.Reason}", actor);
                await Save(w);
                return await AdvanceLocked(w);
            }
            w.Nodes["approval"].Status = NodeStatus.Failed;
            w.Status = "Rejected";
            w.EndedUtc = DateTime.UtcNow;
            w.RejectionReason = req.Reason;
            w.Rollbacks++;
            await stages.Rollback(w, req.Reason);
            journal.Append(w, "rollback", "Release candidate quarantined: " + req.Reason, actor);
            await Save(w);
            return w;
        }
        finally { gate.Release(); }
    }

    public async Task<WorkflowState> Replan(string id, ReplanRequest req, string actor)
    {
        ValidateRequirement(req.Requirement);
        var gate = _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            var w = await Get(id) ?? throw new KeyNotFoundException();
            journal.Verify(w);
            if (w.Status == "Completed") throw new InvalidOperationException("Completed workflow is immutable; create a new one.");
            if (req.Revision != w.Revision) throw new InvalidOperationException("Stale revision.");
            await stages.Rollback(w, "Superseded by requirement change.");
            w.History.Add(new(w.Revision, w.Requirement, w.ReviewDigest, w.Artifacts, w.Evidence));
            w.Revision++;
            w.Requirement = req.Requirement;
            w.Status = "Running";
            w.ApprovedBy = null;
            w.ReviewDigest = null;
            w.RejectionReason = null;
            w.FailureMode = "none";
            w.SimulateTestFailure = false;
            w.EndedUtc = null;
            w.Artifacts = new(); w.Evidence = new();
            w.Nodes = Graph().ToDictionary(x => x.Key, x => new NodeState { Id = x.Key, Dependencies = x.Value });
            w.Rollbacks++;
            journal.Append(w, "replan", "Upstream requirement changed; old revision quarantined, lineage retained, demonstration faults cleared.", actor);
            await Save(w);
            return await AdvanceLocked(w);
        }
        finally { gate.Release(); }
    }

    public async Task RecoverInterrupted()
    {
        foreach (var w in (await All()).Where(w => w.SchemaVersion == 2 && w.Status == "Running"))
        {
            // A crash can leave durable journal events ahead of the last state snapshot.
            w.Audit = journal.ReadVerified(w);
            await SafeStop(w, "Process restarted during execution; explicit replan required.");
            await Save(w);
        }
    }

    public async Task<object> Metrics()
    {
        var all = await All();
        var current = all.Where(w => w.SchemaVersion == 2).ToList();
        var done = current.Where(w => w.EndedUtc != null).ToList();
        var recovered = current.SelectMany(w => w.Incidents).Where(i => i.RecoveredUtc != null).ToList();
        return new
        {
            Total = current.Count, LegacyWorkflows = all.Count - current.Count,
            Completed = current.Count(w => w.Status == "Completed"),
            SuccessRate = done.Count == 0 ? 0 : (double)done.Count(w => w.Status == "Completed") / done.Count,
            RetryFrequency = current.Sum(w => w.Retries), RollbackFrequency = current.Sum(w => w.Rollbacks),
            FallbackFrequency = current.Sum(w => w.Fallbacks), RecoveredIncidents = recovered.Count,
            UnrecoveredIncidents = current.SelectMany(w => w.Incidents).Count(i => i.RecoveredUtc == null),
            MeanTimeToRecoveryMs = recovered.Select(i => (i.RecoveredUtc!.Value - i.FailedUtc).TotalMilliseconds).DefaultIfEmpty(0).Average(),
            MeanEndToEndLatencyMs = done.Select(w => (w.EndedUtc!.Value - w.StartedUtc).TotalMilliseconds).DefaultIfEmpty(0).Average(),
            Note = "MTTR measures first failed attempt to successful retry/fallback per node/revision; unresolved incidents are excluded. Latency includes human wait."
        };
    }
}
