using Orchestrator.Core;
using Orchestrator.Core.Abstractions;

namespace Orchestrator.Agents.Tools;

public static class ToolGateway
{
    public static bool IsSimulated(AgentContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return string.Equals(context.Setting(RunSettings.ExecutionMode), "simulated", StringComparison.OrdinalIgnoreCase);
    }

    public static Task<ProcessResult> DotnetAsync(AgentContext context, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(arguments);
        return IsSimulated(context)
            ? Task.FromResult(Simulate(arguments))
            : context.Tools.RunAsync("dotnet", arguments, context.Workspace.Root, timeout, cancellationToken);
    }

    private static ProcessResult Simulate(IReadOnlyList<string> arguments)
    {
        var verb = arguments.Count > 0 ? arguments[0] : string.Empty;
        var target = arguments.Count > 1 ? arguments[1] : string.Empty;
        var output = verb switch
        {
            "build" => "Build succeeded.\n    0 Warning(s)\n    0 Error(s)",
            "test" when target.Contains("Integration", StringComparison.OrdinalIgnoreCase) =>
                "Passed!  - Failed:     0, Passed:    12, Skipped:     0, Total:    12, Duration: 3 s",
            "test" => "Passed!  - Failed:     0, Passed:    61, Skipped:     0, Total:    61, Duration: 1 s",
            "list" => "The given project has no vulnerable packages given the current sources.",
            _ => string.Empty,
        };
        return new ProcessResult(0, output + "\n[simulated execution]", string.Empty, TimeSpan.FromMilliseconds(40));
    }
}
