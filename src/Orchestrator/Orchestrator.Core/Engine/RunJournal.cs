using Orchestrator.Core.Abstractions;
using Orchestrator.Core.Model;

namespace Orchestrator.Core.Engine;

public sealed class RunJournal(IAuditLog audit, IRunEventPublisher events, TimeProvider clock)
{
    public async Task RecordAsync(
        WorkflowRun run,
        string type,
        string message,
        string? nodeId = null,
        string actor = "orchestrator",
        IReadOnlyDictionary<string, string>? data = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        var appended = await audit.AppendAsync(new AuditEvent
        {
            RunId = run.Id,
            Type = type,
            NodeId = nodeId,
            Actor = actor,
            Message = message,
            Data = data ?? new Dictionary<string, string>(),
            At = clock.GetUtcNow(),
        }, cancellationToken);
        events.Publish(appended);
    }

    public Decision Decide(WorkflowRun run, string nodeId, string actor, DecisionDraft draft, IReadOnlyDictionary<string, string> inputs)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(draft);
        var decision = new Decision
        {
            Id = $"D{run.Decisions.Count + 1:000}",
            NodeId = nodeId,
            Actor = actor,
            Summary = draft.Summary,
            Rationale = draft.Rationale,
            AlternativesConsidered = draft.Alternatives ?? [],
            InputHashes = inputs,
            PlanVersion = run.PlanVersion,
            At = clock.GetUtcNow(),
        };
        run.Decisions.Add(decision);
        return decision;
    }
}
