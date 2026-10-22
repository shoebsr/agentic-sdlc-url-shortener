using Orchestrator.Core.Model;

namespace Orchestrator.Core.Abstractions;

public interface IAgent
{
    string Name { get; }

    Task<AgentOutcome> ExecuteAsync(AgentContext context, CancellationToken cancellationToken);

    Task CompensateAsync(AgentContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}

public interface IAgentRegistry
{
    IAgent Resolve(string name);
}

public sealed class AgentContext
{
    public required WorkflowRun Run { get; init; }

    public required WorkflowNode Node { get; init; }

    public required IReadOnlyDictionary<string, Artifact> Inputs { get; init; }

    public required IWorkspace Workspace { get; init; }

    public required IToolbox Tools { get; init; }

    public required ILlmClient Llm { get; init; }

    public required TimeProvider Clock { get; init; }

    public int Attempt { get; init; } = 1;

    public string Requirement => Run.Requirement;

    public Artifact? Input(string key) => Inputs.GetValueOrDefault(key);

    public Artifact RequireInput(string key) =>
        Inputs.GetValueOrDefault(key) ?? throw new AgentInputMissingException(Node.Id, key);

    public string? Setting(string key) => Run.Settings.GetValueOrDefault(key);
}

public sealed record AgentOutcome
{
    public bool Succeeded { get; init; }

    public bool Transient { get; init; }

    public string? Error { get; init; }

    public IReadOnlyList<Artifact> Artifacts { get; init; } = [];

    public IReadOnlyList<DecisionDraft> Decisions { get; init; } = [];

    public ChangeSet? Changes { get; init; }

    public IReadOnlyList<ClarificationQuestion> Questions { get; init; } = [];

    public IReadOnlyList<NodeInsertion> NodesToInsert { get; init; } = [];

    public IReadOnlyDictionary<string, string> Metrics { get; init; } = new Dictionary<string, string>();

    public static AgentOutcome Success(params Artifact[] artifacts) => new() { Succeeded = true, Artifacts = artifacts };

    public static AgentOutcome Failure(string error, bool transient = false) => new() { Succeeded = false, Error = error, Transient = transient };
}

public sealed record NodeInsertion(WorkflowNode Node, string Before, string Reason);

public sealed record DecisionDraft(string Summary, string Rationale, IReadOnlyList<string>? Alternatives = null);

public sealed record ChangeSet(IReadOnlyList<FileChange> Files)
{
    public static readonly ChangeSet Empty = new([]);

    public int AddedLines => Files.Sum(f => f.AddedLines);

    public int RemovedLines => Files.Sum(f => f.RemovedLines);
}

public enum FileChangeKind
{
    Added,
    Modified,
    Deleted,
}

public sealed record FileChange(string Path, FileChangeKind Kind, int AddedLines, int RemovedLines, string AddedContent);

public sealed class AgentInputMissingException(string nodeId, string key)
    : Exception($"Node '{nodeId}' requires artifact '{key}' which has not been produced.");
