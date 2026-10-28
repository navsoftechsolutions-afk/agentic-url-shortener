using AgenticUrlShortener;

var app = ApplicationHost.Build(args);
await app.Services.GetRequiredService<WorkflowEngine>().RecoverInterrupted();
app.Run();
public partial class Program { }
