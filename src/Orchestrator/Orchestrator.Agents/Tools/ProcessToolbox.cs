using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;
using Orchestrator.Core.Abstractions;

namespace Orchestrator.Agents.Tools;

public sealed class ProcessToolbox(IOptions<WorkspaceOptions> options) : IToolbox
{
    private const int MaxCapturedCharacters = 200_000;

    private static readonly HashSet<string> AllowedTools = new(StringComparer.OrdinalIgnoreCase) { "dotnet", "git" };

    private static readonly string[] SensitiveMarkers = ["KEY", "TOKEN", "SECRET", "PASSWORD", "CONNECTIONSTRING", "CREDENTIAL"];

    public async Task<ProcessResult> RunAsync(
        string tool, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!AllowedTools.Contains(tool))
        {
            throw new UnauthorizedAccessException($"Tool '{tool}' is not on the agent allowlist.");
        }

        var root = Path.GetFullPath(options.Value.WorkspacesRoot);
        var directory = Path.GetFullPath(workingDirectory);
        if (!directory.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Agents may only execute tools inside their sandboxed workspace.");
        }

        return await ProcessRunner.RunAsync(tool, arguments, directory, timeout, Sanitize, cancellationToken);
    }

    internal static void Sanitize(ProcessStartInfo start)
    {
        foreach (var key in start.Environment.Keys.ToList())
        {
            if (SensitiveMarkers.Any(marker => key.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            {
                start.Environment.Remove(key);
            }
        }

        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
    }

    internal static string Cap(StringBuilder buffer) =>
        buffer.Length <= MaxCapturedCharacters ? buffer.ToString() : buffer.ToString(buffer.Length - MaxCapturedCharacters, MaxCapturedCharacters);
}

public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        Action<ProcessStartInfo>? configure,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var start = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        configure?.Invoke(start);

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var timer = Stopwatch.StartNew();
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (stdout) { stdout.AppendLine(e.Data); } } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (stderr) { stderr.AppendLine(e.Data); } } };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return new ProcessResult(-1, ProcessToolbox.Cap(stdout), $"Timed out after {timeout.TotalSeconds:F0}s{Environment.NewLine}{ProcessToolbox.Cap(stderr)}", timer.Elapsed);
        }

        return new ProcessResult(process.ExitCode, ProcessToolbox.Cap(stdout), ProcessToolbox.Cap(stderr), timer.Elapsed);
    }
}
