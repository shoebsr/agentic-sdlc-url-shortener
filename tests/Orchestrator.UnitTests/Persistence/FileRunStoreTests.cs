using Microsoft.Extensions.Options;
using Orchestrator.Core.Model;
using Orchestrator.Persistence;
using static Orchestrator.UnitTests.Support.EngineHarness;

namespace Orchestrator.UnitTests.Persistence;

public sealed class FileRunStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sdlc-tests", Guid.NewGuid().ToString("N"));

    private FileRunStore Store() => new(Options.Create(new PersistenceOptions { RunsDirectory = _directory }));

    [Fact]
    public async Task Run_state_round_trips_with_artifacts_decisions_and_approvals()
    {
        var run = NewRun(Node("a", "x"), Node("b", "x", "a") with { RequiresApproval = true, Risk = RiskLevel.High });
        run.StateOf("a").Status = NodeStatus.Succeeded;
        run.Artifacts["spec"] = Support.ScriptedAgent.Success("spec", "body").Artifacts[0];
        run.Approvals.Add(new ApprovalRequest { Id = "A01", NodeId = "b", Reason = "gate", Risk = RiskLevel.High });
        run.Decisions.Add(new Decision { Id = "D001", NodeId = "a", Actor = "agent", Summary = "chose", PlanVersion = 1 });

        await Store().SaveAsync(run, default);
        var loaded = await Store().LoadAsync(run.Id, default);

        Assert.NotNull(loaded);
        Assert.Equal(NodeStatus.Succeeded, loaded.StateOf("a").Status);
        Assert.Equal(run.Artifacts["spec"].Hash, loaded.Artifacts["spec"].Hash);
        Assert.Equal(RiskLevel.High, loaded.Approvals[0].Risk);
        Assert.Equal(TimeSpan.FromMilliseconds(1), loaded.Nodes[0].Retry.InitialBackoff);
        Assert.Equal(["a", "b"], loaded.Graph().TopologicalOrder());
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("run/../../x")]
    [InlineData("")]
    public async Task Unsafe_run_ids_are_never_resolved_to_paths(string id)
    {
        Assert.Null(await Store().LoadAsync(id, default));
    }

    [Fact]
    public async Task Listing_returns_newest_first()
    {
        var older = NewRun(Node("a", "x"));
        var newer = new WorkflowRun
        {
            Id = "run-newer", Title = "t", Kind = ScenarioKind.Greenfield, Requirement = "r",
            CreatedAt = older.CreatedAt.AddMinutes(5), Nodes = older.Nodes,
        };
        await Store().SaveAsync(older, default);
        await Store().SaveAsync(newer, default);

        var runs = await Store().ListAsync(default);

        Assert.Equal(["run-newer", older.Id], runs.Select(r => r.Id));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
