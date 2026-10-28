using AgenticUrlShortener;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
namespace AgenticUrlShortener.Tests;
public class EngineTests {
 private static string Temp()=>Path.Combine(Path.GetTempPath(),"agentic-tests",Guid.NewGuid().ToString("N"),"store.json");
 [Theory][InlineData("https://example.com",true)][InlineData("javascript:alert(1)",false)][InlineData("ftp://example.com",false)][InlineData("not-a-url",false)][InlineData("https://user:pass@example.com",false)]
 public void UrlValidation(string url,bool expected)=>Assert.Equal(expected,UrlService.IsSafeUrl(url));
 [Fact] public async Task UrlLifecycle(){
  var store=new JsonStore<UrlData>(Temp());
  var svc=new UrlService(store);
  var link=await svc.Create(new CreateUrlRequest("https://example.com"));
  Assert.Equal(10,link.Code.Length);
  var resolved=await svc.Resolve(link.Code);
  Assert.NotNull(resolved);
  Assert.Equal(1,resolved.Clicks);
  Assert.NotNull(await svc.Analytics(link.Code));
  Assert.Equal(1,await store.Read(data=>data.Links.Single(x=>x.Code==link.Code).Clicks));
  Assert.Equal(1,await store.Read(data=>data.Clicks.Count(x=>x.Code==link.Code)));
  Assert.True(await svc.Disable(link.Code));
  Assert.Null(await svc.Resolve(link.Code));
 }
 [Fact] public async Task WorkflowApprovalAndParallelJoin(){var e=new WorkflowEngine(new JsonStore<WorkflowCollection>(Temp()),NullLogger<WorkflowEngine>.Instance);var w=await e.Create(new WorkflowRequest("Create a URL shortener"));Assert.Equal("WaitingApproval",w.Status);Assert.Equal(NodeStatus.Completed,w.Nodes["design"].Status);Assert.Equal(NodeStatus.Completed,w.Nodes["security"].Status);Assert.Equal(NodeStatus.WaitingApproval,w.Nodes["approval"].Status);Assert.Equal(NodeStatus.Pending,w.Nodes["release"].Status);w=await e.Approve(w.Id,new ApprovalRequest(true,"reviewer"));Assert.Equal("Completed",w.Status);Assert.Equal(NodeStatus.Completed,w.Nodes["release"].Status);}
 [Fact] public async Task RetryAndReplan(){var e=new WorkflowEngine(new JsonStore<WorkflowCollection>(Temp()),NullLogger<WorkflowEngine>.Instance);var w=await e.Create(new WorkflowRequest("Build",SimulateTestFailure:true));Assert.True(w.Retries>=1);Assert.Equal("WaitingApproval",w.Status);w=await e.Replan(w.Id,"Add expiration");Assert.Equal(2,w.Revision);Assert.Equal(1,w.Rollbacks);Assert.Equal("WaitingApproval",w.Status);Assert.Contains(w.Audit,a=>a.Event=="replan");}
 [Fact] public async Task RejectionStopsRelease(){var e=new WorkflowEngine(new JsonStore<WorkflowCollection>(Temp()),NullLogger<WorkflowEngine>.Instance);var w=await e.Create(new WorkflowRequest("Build"));w=await e.Approve(w.Id,new ApprovalRequest(false,"reviewer","unsafe"));Assert.Equal("Rejected",w.Status);Assert.Equal(NodeStatus.Pending,w.Nodes["release"].Status);Assert.Equal(1,w.Rollbacks);}
}
