using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orchestrator.Core;
using Orchestrator.Core.Abstractions;
using Orchestrator.Core.Engine;
using Orchestrator.Core.Model;
using Orchestrator.Governance;
using Orchestrator.Governance.Policies;
using Orchestrator.Observability;
using Orchestrator.Persistence;

namespace Orchestrator.UnitTests.Support;

internal sealed class EngineHarness : IAsyncDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sdlc-tests", Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, IAgent> _agents = new(StringComparer.Ordinal);

    public EngineHarness(EngineOptions? engineOptions = null)
    {
        var governance = new GovernanceOptions();
        governance.Approvers.Add(new ApproverOptions { Name = "lead", Role = "Engineering Lead", MaxRisk = RiskLevel.High });
        governance.Approvers.Add(new ApproverOptions { Name = "owner", Role = "Product Owner", MaxRisk = RiskLevel.Medium });
        governance.Approvers.Add(new ApproverOptions { Name = "security", Role = "Security Officer", MaxRisk = RiskLevel.Critical });
        var governanceOptions = Options.Create(governance);

        Store = new FileRunStore(Options.Create(new PersistenceOptions { RunsDirectory = Path.Combine(_directory, "runs") }));
        Audit = new HashChainedAuditLog(Options.Create(new ObservabilityOptions { AuditFile = null }));
        Metrics = new ReliabilityMetrics();
        KillSwitch = new KillSwitch();
        Policies = new PolicyEngine(
        [
            new SecretLeakPolicy(),
            new WorkspaceBoundaryPolicy(),
            new ProtectedPathPolicy(governanceOptions),
            new ChangeBudgetPolicy(governanceOptions),
        ]);

        var journal = new RunJournal(Audit, new RunEventPublisher(), TimeProvider.System);
        Engine = new WorkflowEngine(
            new Registry(_agents),
            new GateRegistry(governanceOptions),
            Policies,
            Metrics,
            Store,
            new NullWorkspaceProvider(),
            new NullToolbox(),
            new global::Orchestrator.Agents.Llm.DisabledLlmClient(),
            KillSwitch,
            journal,
            TimeProvider.System,
            Options.Create(engineOptions ?? new EngineOptions()),
            NullLogger<WorkflowEngine>.Instance);
        Coordinator = new RunCoordinator(
            Engine, Store, KillSwitch, new ApprovalAuthority(governanceOptions), new RunInboxes(), journal, NullLogger<RunCoordinator>.Instance);
    }

    public FileRunStore Store { get; }

    public HashChainedAuditLog Audit { get; }

    public ReliabilityMetrics Metrics { get; }

    public KillSwitch KillSwitch { get; }

    public PolicyEngine Policies { get; }

    public WorkflowEngine Engine { get; }

    public RunCoordinator Coordinator { get; }

    public EngineHarness Agent(IAgent agent)
    {
        _agents[agent.Name] = agent;
        return this;
    }

    public async Task<WorkflowRun> RunToRestAsync(WorkflowRun run)
    {
        await Coordinator.StartAsync(run, CancellationToken.None);
        return await SettleAsync(run.Id);
    }

    public async Task<WorkflowRun> SettleAsync(string runId)
    {
        await Coordinator.WaitForIdleAsync(runId, TimeSpan.FromSeconds(30));
        return (await Store.LoadAsync(runId, CancellationToken.None))!;
    }

    public async Task<IReadOnlyList<string>> EventTypesAsync(string runId) =>
        [.. (await Audit.ReadAsync(runId, CancellationToken.None)).Select(e => e.Type)];

    public async ValueTask DisposeAsync()
    {
        await Coordinator.DisposeAsync();
        Audit.Dispose();
        Metrics.Dispose();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    public static WorkflowRun NewRun(params WorkflowNode[] nodes) => new()
    {
        Id = "run-" + Guid.NewGuid().ToString("N")[..12],
        Title = "test run",
        Kind = ScenarioKind.Brownfield,
        Requirement = "test requirement",
        InitiatedBy = "requester",
        CreatedAt = DateTimeOffset.UtcNow,
        Nodes = [.. nodes],
    };

    public static WorkflowNode Node(string id, string agent, params string[] dependsOn) => new()
    {
        Id = id,
        Title = id,
        Stage = Stage.Implementation,
        Agent = agent,
        DependsOn = dependsOn,
        Retry = new RetryPolicy(3, TimeSpan.FromMilliseconds(1)),
    };

    private sealed class Registry(Dictionary<string, IAgent> agents) : IAgentRegistry
    {
        public IAgent Resolve(string name) => agents.TryGetValue(name, out var agent)
            ? agent
            : throw new InvalidOperationException($"Agent '{name}' not registered in test harness.");
    }

    private sealed class NullWorkspaceProvider : IWorkspaceProvider
    {
        public IWorkspace For(WorkflowRun run) => new NullWorkspace();
    }

    private sealed class NullWorkspace : IWorkspace
    {
        public string Root => Path.GetTempPath();

        public bool Exists(string relativePath) => false;

        public Task<string> ReadAsync(string relativePath, CancellationToken cancellationToken) => Task.FromResult(string.Empty);

        public Task WriteAsync(string relativePath, string content, CancellationToken cancellationToken) => Task.CompletedTask;

        public IEnumerable<string> Files(string pattern = "*") => [];

        public Task<string> CheckpointAsync(string label, CancellationToken cancellationToken) => Task.FromResult("checkpoint");

        public Task RestoreAsync(string checkpoint, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<ChangeSet> DiffAsync(string fromCheckpoint, CancellationToken cancellationToken) => Task.FromResult(ChangeSet.Empty);
    }

    private sealed class NullToolbox : IToolbox
    {
        public Task<ProcessResult> RunAsync(string tool, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(new ProcessResult(0, string.Empty, string.Empty, TimeSpan.Zero));
    }
}
