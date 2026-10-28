using System.Text.Json.Serialization;
namespace AgenticUrlShortener;
public record CreateUrlRequest(string Url, int? ExpiresInDays = null);
public class ShortLink { public string Code {get;set;}=""; public string Url {get;set;}=""; public DateTime CreatedUtc {get;set;}=DateTime.UtcNow; public DateTime? ExpiresUtc {get;set;} public bool Disabled {get;set;} public int Clicks {get;set;} }
public record ClickEvent(string Code,DateTime AtUtc);
public class UrlData {public List<ShortLink> Links {get;set;}=new();public List<ClickEvent> Clicks {get;set;}=new();}
public record WorkflowRequest(string Requirement,string Scenario="greenfield",bool SimulateTestFailure=false);
public record ApprovalRequest(bool Approved,string Reviewer,string? Reason=null);
public enum NodeStatus {Pending,Running,WaitingApproval,Completed,Failed,Skipped}
public class NodeState {public string Id {get;set;}="";public string[] Dependencies {get;set;}=[];public NodeStatus Status {get;set;}=NodeStatus.Pending;public int Attempts {get;set;}=0;public string? Output {get;set;} public string? Error {get;set;} }
public record AuditEvent(DateTime AtUtc,string Event,string Detail);
public class WorkflowState {public string Id {get;set;}=Guid.NewGuid().ToString("N");public string Requirement {get;set;}="";public string Scenario {get;set;}="greenfield";public string Status {get;set;}="Running";public int Revision {get;set;}=1;public bool SimulateTestFailure {get;set;} public bool FailureConsumed {get;set;} public DateTime StartedUtc {get;set;}=DateTime.UtcNow;public DateTime? EndedUtc {get;set;}public Dictionary<string,NodeState> Nodes {get;set;}=new();public List<AuditEvent> Audit {get;set;}=new();public Dictionary<string,string> Artifacts {get;set;}=new();public string? ApprovedBy {get;set;}public string? RejectionReason {get;set;}public int Retries {get;set;}public int Rollbacks {get;set;} }
