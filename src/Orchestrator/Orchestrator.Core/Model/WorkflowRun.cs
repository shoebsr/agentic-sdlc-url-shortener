namespace Orchestrator.Core.Model;

public sealed class NodeState
{
    public NodeStatus Status { get; set; } = NodeStatus.Pending;

    public int Attempts { get; set; }

    public string? ExecutedBy { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public DateTimeOffset? FirstFailureAt { get; set; }

    public string? LastError { get; set; }

    public List<string> Outputs { get; set; } = [];

    public List<GateOutcome> GateOutcomes { get; set; } = [];

    public int Executions { get; set; }
}

public sealed record GateOutcome(string Gate, string Phase, bool Passed, string Detail, DateTimeOffset At);

public sealed class WorkflowRun
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required ScenarioKind Kind { get; init; }

    public required string Requirement { get; set; }

    public string InitiatedBy { get; init; } = "system";

    public string? ScenarioId { get; init; }

    public RunStatus Status { get; set; } = RunStatus.Pending;

    public int PlanVersion { get; set; } = 1;

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public string? HaltReason { get; set; }

    public required List<WorkflowNode> Nodes { get; set; }

    public Dictionary<string, NodeState> NodeStates { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, Artifact> Artifacts { get; set; } = new(StringComparer.Ordinal);

    public List<Decision> Decisions { get; set; } = [];

    public List<ApprovalRequest> Approvals { get; set; } = [];

    public List<ClarificationQuestion> OpenQuestions { get; set; } = [];

    public Dictionary<string, string> Settings { get; set; } = new(StringComparer.Ordinal);

    public WorkflowGraph Graph() => new(Nodes);

    public NodeState StateOf(string nodeId)
    {
        if (!NodeStates.TryGetValue(nodeId, out var state))
        {
            state = new NodeState();
            NodeStates[nodeId] = state;
        }

        return state;
    }

    public Artifact? FindArtifact(string key) => Artifacts.GetValueOrDefault(key);

    public bool IsTerminal => Status is RunStatus.Succeeded or RunStatus.Failed or RunStatus.RolledBack;
}
