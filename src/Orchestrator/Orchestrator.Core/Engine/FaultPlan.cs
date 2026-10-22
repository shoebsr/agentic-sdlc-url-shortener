using Orchestrator.Core.Model;

namespace Orchestrator.Core.Engine;

public enum FaultKind
{
    Transient,
    Primary,
    Fatal,
}

public sealed record InjectedFault(string NodeId, FaultKind Kind, int Times);

public static class FaultPlan
{
    public static IReadOnlyList<InjectedFault> Parse(WorkflowRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (!run.Settings.TryGetValue(RunSettings.Faults, out var spec) || string.IsNullOrWhiteSpace(spec))
        {
            return [];
        }

        return spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(entry => entry.Split(':', StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length >= 2 && Enum.TryParse<FaultKind>(parts[1], ignoreCase: true, out _))
            .Select(parts => new InjectedFault(
                parts[0],
                Enum.Parse<FaultKind>(parts[1], ignoreCase: true),
                parts.Length > 2 && int.TryParse(parts[2], out var times) ? times : 1))
            .ToList();
    }

    public static InjectedFault? For(WorkflowRun run, string nodeId, int attempt, bool isFallback)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.StateOf(nodeId).Executions > 1)
        {
            return null;
        }

        var fault = Parse(run).FirstOrDefault(f => f.NodeId == nodeId);
        return fault?.Kind switch
        {
            FaultKind.Transient when !isFallback && attempt <= fault.Times => fault,
            FaultKind.Primary when !isFallback => fault,
            FaultKind.Fatal => fault,
            _ => null,
        };
    }
}
