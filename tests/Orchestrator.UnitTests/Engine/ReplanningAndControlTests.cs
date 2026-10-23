using Orchestrator.Core;
using Orchestrator.Core.Abstractions;
using Orchestrator.Core.Model;
using Orchestrator.UnitTests.Support;
using static Orchestrator.UnitTests.Support.EngineHarness;

namespace Orchestrator.UnitTests.Engine;

public sealed class ReplanningAndControlTests
{
    private static ScriptedAgent AskingRequirementsAgent() => new("requirements", (ctx, _) => Task.FromResult(new AgentOutcome
    {
        Succeeded = true,
        Artifacts = [new Artifact { Key = ArtifactKeys.RequirementsSpec, Kind = "spec", ProducedBy = string.Empty, Content = "spec" }],
        Questions = [new ClarificationQuestion("Q1", "How fast?", "p95 < 50 ms", "performance")],
        NodesToInsert =
        [
            new NodeInsertion(new WorkflowNode
            {
                Id = "clarification",
                Title = "Human clarification",
                Stage = Stage.Clarification,
                Agent = "clarifier",
                DependsOn = [ctx.Node.Id],
                RequiresHumanInput = true,
                Retry = RetryPolicy.None,
            }, "design", "ambiguity"),
        ],
    }));

    [Fact]
    public async Task Ambiguity_inserts_a_clarification_stage_and_waits_for_a_human()
    {
        var design = ScriptedAgent.Succeeds("design");
        var clarifier = new ScriptedAgent("clarifier", (ctx, _) =>
            Task.FromResult(ScriptedAgent.Success(ArtifactKeys.ClarifiedRequirements, ctx.RequireInput(ArtifactKeys.ClarificationAnswers).Content)));
        await using var harness = new EngineHarness();
        harness.Agent(AskingRequirementsAgent()).Agent(clarifier).Agent(design);

        var run = await harness.RunToRestAsync(NewRun(
            Node("requirements", "requirements") with { Stage = Stage.Requirements },
            Node("design", "design", "requirements") with { Stage = Stage.Design, EntryGates = ["requirements.unambiguous"] }));

        Assert.Equal(RunStatus.AwaitingInput, run.Status);
        Assert.Equal(2, run.PlanVersion);
        Assert.Contains("clarification", run.Graph().Nodes.First(n => n.Id == "design").DependsOn);
        Assert.Equal(0, design.Calls);

        var answered = await harness.Coordinator.ClarifyAsync(run.Id, new Dictionary<string, string> { ["Q1"] = "p95 under 20 ms" }, "requester");
        run = await harness.SettleAsync(run.Id);

        Assert.True(answered.Accepted);
        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Contains("p95 under 20 ms", run.FindArtifact(ArtifactKeys.ClarifiedRequirements)!.Content, StringComparison.Ordinal);
        Assert.Contains(run.Decisions, d => d.Summary.StartsWith("Inserted 'Human clarification'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unanswered_questions_without_defaults_are_rejected()
    {
        await using var harness = new EngineHarness();
        harness.Agent(new ScriptedAgent("requirements", (ctx, _) => Task.FromResult(new AgentOutcome
        {
            Succeeded = true,
            Questions = [new ClarificationQuestion("Q1", "Scope?", null, "scope")],
            NodesToInsert = [new NodeInsertion(new WorkflowNode
            {
                Id = "clarification", Title = "c", Stage = Stage.Clarification, Agent = "x", DependsOn = [ctx.Node.Id], RequiresHumanInput = true,
            }, "design", "ambiguity")],
        }))).Agent(ScriptedAgent.Succeeds("design"));

        var run = await harness.RunToRestAsync(NewRun(
            Node("requirements", "requirements") with { Stage = Stage.Requirements }, Node("design", "design", "requirements")));
        var result = await harness.Coordinator.ClarifyAsync(run.Id, new Dictionary<string, string>(), "requester");

        Assert.False(result.Accepted);
        Assert.Contains("Q1", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Amending_the_requirement_invalidates_downstream_work_and_replans()
    {
        var implementation = ScriptedAgent.Succeeds("impl");
        var requirementsSeen = new List<string>();
        await using var harness = new EngineHarness();
        harness.Agent(new ScriptedAgent("requirements", (ctx, _) =>
            {
                requirementsSeen.Add(ctx.Requirement);
                return Task.FromResult(ScriptedAgent.Success(ArtifactKeys.RequirementsSpec, ctx.Requirement));
            }))
            .Agent(implementation)
            .Agent(ScriptedAgent.Succeeds("release"));

        var run = await harness.RunToRestAsync(NewRun(
            Node("requirements", "requirements") with { Stage = Stage.Requirements },
            Node("impl", "impl", "requirements") with { Compensable = true },
            Node("release", "release", "impl") with { RequiresApproval = true }));
        Assert.Equal(RunStatus.AwaitingApproval, run.Status);

        var amended = await harness.Coordinator.AmendRequirementAsync(run.Id, "a different requirement", "owner", "scope changed");
        run = await harness.SettleAsync(run.Id);

        Assert.True(amended.Accepted);
        Assert.Equal(["test requirement", "a different requirement"], requirementsSeen);
        Assert.Equal(2, run.PlanVersion);
        Assert.Equal(1, implementation.Compensations);
        Assert.Equal(2, implementation.Calls);
        Assert.Equal(ApprovalStatus.Rejected, run.Approvals[0].Status);
        Assert.Contains("Superseded", run.Approvals[0].Comment, StringComparison.Ordinal);
        Assert.Equal(ApprovalStatus.Pending, run.Approvals[1].Status);
        Assert.Equal(2, run.FindArtifact(ArtifactKeys.RequirementsSpec)!.Version);
    }

    [Fact]
    public async Task Safe_stop_interrupts_in_flight_work_preserves_state_and_resumes()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowCalls = 0;
        await using var harness = new EngineHarness();
        harness.Agent(ScriptedAgent.Succeeds("first"))
            .Agent(new ScriptedAgent("slow", async (_, _) =>
            {
                if (Interlocked.Increment(ref slowCalls) == 1)
                {
                    gate.TrySetResult();
                    await Task.Delay(Timeout.Infinite, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
                }

                return ScriptedAgent.Success("slow.out");
            }));

        var run = NewRun(Node("first", "first"), Node("slow", "slow", "first") with { Retry = RetryPolicy.None });
        await harness.Coordinator.StartAsync(run, CancellationToken.None);
        await gate.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var halted = await harness.Coordinator.HaltAsync(run.Id, "operator", "maintenance");
        run = await harness.SettleAsync(run.Id);

        Assert.True(halted.Accepted);
        Assert.Equal(RunStatus.Halted, run.Status);
        Assert.Equal(NodeStatus.Succeeded, run.StateOf("first").Status);
        Assert.Equal(NodeStatus.Pending, run.StateOf("slow").Status);
    }

    [Fact]
    public async Task Halted_run_resumes_from_persisted_state_without_redoing_completed_work()
    {
        var first = ScriptedAgent.Succeeds("first");
        await using var harness = new EngineHarness();
        harness.Agent(first).Agent(ScriptedAgent.Succeeds("second"));
        var run = NewRun(Node("first", "first"), Node("second", "second", "first"));
        run.Status = RunStatus.Halted;
        run.StartedAt = DateTimeOffset.UtcNow;
        run.StateOf("first").Status = NodeStatus.Succeeded;
        run.StateOf("second").Status = NodeStatus.Pending;
        await harness.Store.SaveAsync(run, CancellationToken.None);

        var resumed = await harness.Coordinator.ResumeAsync(run.Id, CancellationToken.None);
        run = await harness.SettleAsync(run.Id);

        Assert.True(resumed.Accepted);
        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal(0, first.Calls);
    }

    [Fact]
    public async Task Kill_switch_prevents_execution_and_blocks_resume_until_released()
    {
        var agent = ScriptedAgent.Succeeds("work");
        await using var harness = new EngineHarness();
        harness.Agent(agent);
        await harness.Coordinator.EngageKillSwitchAsync("incident");

        var run = await harness.RunToRestAsync(NewRun(Node("work", "work")));
        var blocked = await harness.Coordinator.ResumeAsync(run.Id, CancellationToken.None);
        harness.Coordinator.ReleaseKillSwitch();
        var resumed = await harness.Coordinator.ResumeAsync(run.Id, CancellationToken.None);
        run = await harness.SettleAsync(run.Id);

        Assert.False(blocked.Accepted);
        Assert.True(resumed.Accepted);
        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal(1, agent.Calls);
    }

    [Fact]
    public async Task Commands_for_unknown_runs_are_rejected()
    {
        await using var harness = new EngineHarness();

        var result = await harness.Coordinator.ApproveAsync("run-missing", "A01", "lead", true, null);

        Assert.False(result.Accepted);
    }

    [Fact]
    public async Task Every_transition_is_audited_in_an_intact_hash_chain()
    {
        await using var harness = new EngineHarness();
        harness.Agent(ScriptedAgent.FailsTimes("flaky", 1)).Agent(ScriptedAgent.Succeeds("next"));

        var run = await harness.RunToRestAsync(NewRun(Node("flaky", "flaky"), Node("next", "next", "flaky")));
        var types = await harness.EventTypesAsync(run.Id);
        var verification = await harness.Audit.VerifyAsync(CancellationToken.None);

        Assert.Equal("run.created", types[0]);
        Assert.Equal("run.succeeded", types[^1]);
        Assert.Contains("node.retry", types);
        Assert.True(verification.Intact);
    }
}
