using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace AgenticUrlShortener;

public record ToolResult(int ExitCode, string Output, double DurationMs);
public interface IToolRunner { Task<ToolResult> Run(string directory, string[] arguments); }

public class ToolRunner(EngineeringOptions options) : IToolRunner
{
    public async Task<ToolResult> Run(string directory, string[] arguments)
    {
        var start = new ProcessStartInfo(options.DotnetExecutable)
        {
            WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        var inherited = new[] { "PATH", "HOME", "TMPDIR", "TEMP", "TMP", "SystemRoot", "DOTNET_ROOT", "NUGET_PACKAGES" }
            .ToDictionary(key => key, Environment.GetEnvironmentVariable);
        start.Environment.Clear();
        foreach (var (key, value) in inherited) if (value != null) start.Environment[key] = value;
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        using var process = new Process { StartInfo = start };
        var timer = Stopwatch.StartNew();
        try { process.Start(); }
        catch (Win32Exception) { throw new ToolUnavailableException("The configured .NET SDK executable is unavailable."); }
        var stdout = Capture(process.StandardOutput);
        var stderr = Capture(process.StandardError);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.ToolTimeoutSeconds));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
            throw new ValidationException("Tool execution timed out; process tree terminated.");
        }
        return new(process.ExitCode, (await stdout) + "\n" + (await stderr), timer.Elapsed.TotalMilliseconds);
    }
    private static async Task<string> Capture(StreamReader reader)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer)) > 0)
            if (result.Length < 200_000) result.Append(buffer, 0, Math.Min(count, 200_000 - result.Length));
        return result.ToString();
    }
}
