namespace AgenticUrlShortener;

public record WorkflowRequest(string Requirement, string Scenario = "greenfield", bool SimulateTestFailure = false,
    string FailureMode = "none");
public record ReplanRequest(string Requirement, int Revision);
public record ApprovalRequest(bool Approved, int Revision, string EvidenceHash, string? Reviewer = null, string? Reason = null);
public enum NodeStatus { Pending, Running, WaitingApproval, Completed, Failed, Skipped }

public class NodeState
{
    public string Id { get; set; } = "";
    public string[] Dependencies { get; set; } = [];
    public NodeStatus Status { get; set; }
    public int Attempts { get; set; }
    public string? Output { get; set; }
    public string? Error { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? EndedUtc { get; set; }
}

public record AuditEvent(long Sequence, DateTime AtUtc, int Revision, string Actor, string Event, string Detail,
    string PreviousHash, string Hash);
public record StageEvidence(int Revision, string ContentHash, Dictionary<string, string> Inputs,
    Dictionary<string, string> Files);
public record RevisionSnapshot(int Revision, string Requirement, string? ReviewDigest,
    Dictionary<string, string> Artifacts, Dictionary<string, StageEvidence> Evidence);
public class RecoveryIncident
{
    public string Node { get; set; } = "";
    public int Revision { get; set; }
    public DateTime FailedUtc { get; set; }
    public DateTime? RecoveredUtc { get; set; }
    public string Outcome { get; set; } = "Unrecovered";
}

public class WorkflowState
{
    public int SchemaVersion { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Requirement { get; set; } = "";
    public string Scenario { get; set; } = "greenfield";
    public string CreatedBy { get; set; } = "";
    public string Status { get; set; } = "Running";
    public int Revision { get; set; } = 1;
    public bool SimulateTestFailure { get; set; }
    public string FailureMode { get; set; } = "none";
    public bool FailureConsumed { get; set; }
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? EndedUtc { get; set; }
    public Dictionary<string, NodeState> Nodes { get; set; } = new();
    public List<AuditEvent> Audit { get; set; } = [];
    public Dictionary<string, string> Artifacts { get; set; } = new();
    public Dictionary<string, StageEvidence> Evidence { get; set; } = new();
    public List<RevisionSnapshot> History { get; set; } = [];
    public List<RecoveryIncident> Incidents { get; set; } = [];
    public string? ReviewDigest { get; set; }
    public string? ApprovedBy { get; set; }
    public string? RejectionReason { get; set; }
    public int Retries { get; set; }
    public int Rollbacks { get; set; }
    public int Fallbacks { get; set; }
}

public record RequirementSpec(string Intent, bool Analytics, bool Expiration, bool HttpsOnly,
    bool NeedsClarification, string[] Questions, string[] AcceptanceCriteria);
public record StageResult(string Output, Dictionary<string, string>? Files = null);
public record EngineeringOptions(string RepositoryRoot, string DataDirectory, string DotnetExecutable = "dotnet",
    bool EnableFailureInjection = false, int ToolTimeoutSeconds = 120);
public class RetryableStageException(string message) : Exception(message);
public class ToolUnavailableException(string message) : RetryableStageException(message);
public class ValidationException(string message) : Exception(message);

public interface IEngineeringStages
{
    Task<StageResult> Execute(WorkflowState workflow, NodeState node);
    Task<StageResult?> Fallback(WorkflowState workflow, NodeState node, Exception failure);
    Task VerifyFiles(WorkflowState workflow);
    Task Rollback(WorkflowState workflow, string reason);
}

public class WorkflowCollection { public Dictionary<string, WorkflowState> Items { get; set; } = new(); }
