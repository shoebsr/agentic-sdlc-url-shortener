using Orchestrator.Core;
using Orchestrator.Core.Abstractions;
using Orchestrator.Core.Model;
using Orchestrator.UnitTests.Support;
using static Orchestrator.UnitTests.Support.EngineHarness;

namespace Orchestrator.UnitTests.Engine;

public sealed class WorkflowEngineTests
{
    [Fact]
    public async Task Linear_workflow_runs_in_dependency_order_and_succeeds()
    {
        var order = new List<string>();
        await using var harness = new EngineHarness();
        foreach (var name in new[] { "a", "b", "c" })
        {
            harness.Agent(new ScriptedAgent(name, (ctx, _) =>
            {
                lock (order)
                {
                    order.Add(ctx.Node.Id);
                }

                return Task.FromResult(ScriptedAgent.Success(ctx.Node.Id + ".out"));
            }));
        }

        var run = await harness.RunToRestAsync(NewRun(Node("a", "a"), Node("b", "b", "a"), Node("c", "c", "b")));

        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal(["a", "b", "c"], order);
        Assert.All(run.NodeStates.Values, s => Assert.Equal(NodeStatus.Succeeded, s.Status));
        Assert.Equal(1, harness.Metrics.Snapshot().RunsSucceeded);
    }

    [Fact]
    public async Task Independent_branches_execute_concurrently_and_join_waits_for_both()
    {
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        async Task<AgentOutcome> Branch(AgentContext ctx, int _)
        {
            if (Interlocked.Increment(ref started) == 2)
            {
                bothStarted.TrySetResult();
            }

            await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return ScriptedAgent.Success(ctx.Node.Id + ".out");
        }

        var joinSawBoth = false;
        await using var harness = new EngineHarness();
        harness.Agent(ScriptedAgent.Succeeds("root"))
            .Agent(new ScriptedAgent("left", Branch))
            .Agent(new ScriptedAgent("right", Branch))
            .Agent(new ScriptedAgent("join", (ctx, _) =>
            {
                joinSawBoth = ctx.Input("left.out") is not null && ctx.Input("right.out") is not null;
                return Task.FromResult(ScriptedAgent.Success("join.out"));
            }));

        var run = await harness.RunToRestAsync(NewRun(
            Node("root", "root"), Node("left", "left", "root"), Node("right", "right", "root"), Node("join", "join", "left", "right")));

        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.True(bothStarted.Task.IsCompletedSuccessfully);
        Assert.True(joinSawBoth);
    }

    [Fact]
    public async Task Transient_failures_are_retried_within_budget_and_recovery_is_measured()
    {
        var flaky = ScriptedAgent.FailsTimes("flaky", failures: 2);
        await using var harness = new EngineHarness();
        harness.Agent(flaky);

        var run = await harness.RunToRestAsync(NewRun(Node("work", "flaky")));
        var metrics = harness.Metrics.Snapshot();

        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal(3, flaky.Calls);
        Assert.Equal(3, run.StateOf("work").Attempts);
        Assert.Equal(2, metrics.Retries);
        Assert.True(metrics.MeanTimeToRecoverSeconds > 0);
        Assert.Contains("node.recovered", await harness.EventTypesAsync(run.Id));
    }

    [Fact]
    public async Task Permanent_failures_are_not_retried()
    {
        var broken = ScriptedAgent.AlwaysFails("broken", transient: false);
        await using var harness = new EngineHarness();
        harness.Agent(broken);

        var run = await harness.RunToRestAsync(NewRun(Node("work", "broken")));

        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Equal(1, broken.Calls);
        Assert.Equal(0, harness.Metrics.Snapshot().Retries);
    }

    [Fact]
    public async Task Exhausted_primary_falls_back_to_secondary_agent()
    {
        await using var harness = new EngineHarness();
        harness.Agent(ScriptedAgent.AlwaysFails("primary", transient: true)).Agent(ScriptedAgent.Succeeds("secondary"));
        var node = Node("work", "primary") with { FallbackAgent = "secondary" };

        var run = await harness.RunToRestAsync(NewRun(node));

        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal("secondary", run.StateOf("work").ExecutedBy);
        Assert.Equal(1, harness.Metrics.Snapshot().Fallbacks);
        Assert.Contains("node.fallback", await harness.EventTypesAsync(run.Id));
    }

    [Fact]
    public async Task Failure_compensates_completed_work_in_reverse_order_and_stops_scheduling()
    {
        var compensated = new List<string>();
        ScriptedAgent Compensable(string name) => new(name, (_, _) => Task.FromResult(ScriptedAgent.Success(name + ".out")));
        var first = Compensable("first");
        var second = Compensable("second");
        var never = ScriptedAgent.Succeeds("never");
        await using var harness = new EngineHarness();
        harness.Agent(first).Agent(second).Agent(ScriptedAgent.AlwaysFails("boom")).Agent(never);

        var run = await harness.RunToRestAsync(NewRun(
            Node("first", "first") with { Compensable = true },
            Node("second", "second", "first") with { Compensable = true },
            Node("boom", "boom", "second"),
            Node("never", "never", "boom")));

        Assert.Equal(RunStatus.RolledBack, run.Status);
        Assert.Equal(1, first.Compensations);
        Assert.Equal(1, second.Compensations);
        Assert.Equal(0, never.Calls);
        Assert.Equal(NodeStatus.RolledBack, run.StateOf("first").Status);
        Assert.Equal(NodeStatus.Pending, run.StateOf("never").Status);
        var events = await harness.EventTypesAsync(run.Id);
        Assert.True(events.ToList().LastIndexOf("node.rolled_back") > events.ToList().IndexOf("node.failed"));
    }

    [Fact]
    public async Task Run_without_compensable_work_ends_failed_not_rolled_back()
    {
        await using var harness = new EngineHarness();
        harness.Agent(ScriptedAgent.Succeeds("ok")).Agent(ScriptedAgent.AlwaysFails("boom"));

        var run = await harness.RunToRestAsync(NewRun(Node("ok", "ok"), Node("boom", "boom", "ok")));

        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Equal(NodeStatus.Failed, run.StateOf("boom").Status);
    }

    [Fact]
    public async Task Agent_exceeding_its_timeout_fails_as_transient()
    {
        var slow = new ScriptedAgent("slow", async (_, _) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            return ScriptedAgent.Success("slow.out");
        });
        await using var harness = new EngineHarness();
        harness.Agent(slow);
        var node = Node("work", "slow") with { Timeout = TimeSpan.FromMilliseconds(50), Retry = RetryPolicy.None };

        var run = await harness.RunToRestAsync(NewRun(node));

        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Contains("timeout", run.StateOf("work").LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Artifacts_are_versioned_hashed_and_carry_input_lineage()
    {
        await using var harness = new EngineHarness();
        harness.Agent(ScriptedAgent.Succeeds("up", "spec")).Agent(ScriptedAgent.Succeeds("down", "design"));

        var run = await harness.RunToRestAsync(NewRun(Node("up", "up"), Node("down", "down", "up")));
        var spec = run.FindArtifact("spec")!;
        var design = run.FindArtifact("design")!;

        Assert.Equal(1, spec.Version);
        Assert.Equal("up", spec.ProducedBy);
        Assert.Equal(spec.Hash, design.DerivedFrom["spec"]);
    }

    [Fact]
    public async Task Decisions_are_recorded_with_actor_plan_version_and_input_hashes()
    {
        await using var harness = new EngineHarness();
        harness.Agent(ScriptedAgent.Succeeds("up", "spec"))
            .Agent(new ScriptedAgent("decider", (_, _) => Task.FromResult(new AgentOutcome
            {
                Succeeded = true,
                Decisions = [new DecisionDraft("Chose A", "Because", ["B"])],
            })));

        var run = await harness.RunToRestAsync(NewRun(Node("up", "up"), Node("decide", "decider", "up")));
        var decision = Assert.Single(run.Decisions);

        Assert.Equal("decider", decision.Actor);
        Assert.Equal(1, decision.PlanVersion);
        Assert.Equal(["B"], decision.AlternativesConsidered);
        Assert.True(decision.InputHashes.ContainsKey("spec"));
    }

    [Fact]
    public async Task Fault_plan_injects_transient_faults_that_are_audited()
    {
        await using var harness = new EngineHarness();
        harness.Agent(ScriptedAgent.Succeeds("work"));
        var run = NewRun(Node("work", "work"));
        run.Settings[RunSettings.Faults] = "work:transient:1";

        run = await harness.RunToRestAsync(run);

        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal(2, run.StateOf("work").Attempts);
        Assert.Contains("fault.injected", await harness.EventTypesAsync(run.Id));
    }
}
