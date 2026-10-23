using Orchestrator.Core;
using Orchestrator.Core.Abstractions;
using Orchestrator.Core.Model;
using Orchestrator.UnitTests.Support;
using static Orchestrator.UnitTests.Support.EngineHarness;

namespace Orchestrator.UnitTests.Engine;

public sealed class GovernedExecutionTests
{
    [Fact]
    public async Task Governed_checkpoint_pauses_until_an_authorised_human_approves()
    {
        var downstream = ScriptedAgent.Succeeds("next");
        await using var harness = new EngineHarness();
        harness.Agent(ScriptedAgent.Succeeds("design")).Agent(downstream);

        var run = await harness.RunToRestAsync(NewRun(
            Node("design", "design") with { RequiresApproval = true, Risk = RiskLevel.Medium },
            Node("next", "next", "design")));

        Assert.Equal(RunStatus.AwaitingApproval, run.Status);
        Assert.Equal(0, downstream.Calls);
        var approval = Assert.Single(run.Approvals);

        var result = await harness.Coordinator.ApproveAsync(run.Id, approval.Id, "lead", approve: true, "looks good");
        run = await harness.SettleAsync(run.Id);

        Assert.True(result.Accepted);
        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal(ApprovalStatus.Approved, run.Approvals[0].Status);
        Assert.Equal("lead", run.Approvals[0].DecidedBy);
        Assert.Equal(1, downstream.Calls);
        Assert.Contains(run.Decisions, d => d.Actor == "lead" && d.Rationale == "looks good");
    }

    [Fact]
    public async Task Rejection_fails_the_node_and_rolls_back_compensable_work()
    {
        var implementation = ScriptedAgent.Succeeds("impl");
        await using var harness = new EngineHarness();
        harness.Agent(implementation);

        var run = await harness.RunToRestAsync(NewRun(
            Node("impl", "impl") with { RequiresApproval = true, Risk = RiskLevel.High, Compensable = true }));
        await harness.Coordinator.ApproveAsync(run.Id, "A01", "lead", approve: false, "unsafe");
        run = await harness.SettleAsync(run.Id);

        Assert.Equal(RunStatus.RolledBack, run.Status);
        Assert.Equal(1, implementation.Compensations);
        Assert.Contains("unsafe", run.StateOf("impl").LastError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("lead", "Segregation of duties")]
    [InlineData("mallory", "not a registered approver")]
    [InlineData("owner", "may approve up to Medium")]
    public async Task Approval_authority_refuses_unauthorised_approvers(string approver, string reason)
    {
        await using var harness = new EngineHarness();
        harness.Agent(ScriptedAgent.Succeeds("impl"));
        var initiatedByLead = NewRun(Node("impl", "impl") with { RequiresApproval = true, Risk = RiskLevel.High });
        initiatedByLead = new WorkflowRun
        {
            Id = initiatedByLead.Id,
            Title = initiatedByLead.Title,
            Kind = initiatedByLead.Kind,
            Requirement = initiatedByLead.Requirement,
            InitiatedBy = "lead",
            CreatedAt = initiatedByLead.CreatedAt,
            Nodes = initiatedByLead.Nodes,
        };

        var run = await harness.RunToRestAsync(initiatedByLead);
        var result = await harness.Coordinator.ApproveAsync(run.Id, "A01", approver, approve: true, null);
        run = await harness.SettleAsync(run.Id);

        Assert.False(result.Accepted);
        Assert.Contains(reason, result.Message, StringComparison.Ordinal);
        Assert.Equal(RunStatus.AwaitingApproval, run.Status);
        Assert.Equal(ApprovalStatus.Pending, run.Approvals[0].Status);
    }

    [Fact]
    public async Task Approval_cannot_be_decided_twice()
    {
        await using var harness = new EngineHarness();
        harness.Agent(ScriptedAgent.Succeeds("impl"));
        var run = await harness.RunToRestAsync(NewRun(Node("impl", "impl") with { RequiresApproval = true }));

        await harness.Coordinator.ApproveAsync(run.Id, "A01", "lead", true, null);
        await harness.SettleAsync(run.Id);
        var second = await harness.Coordinator.ApproveAsync(run.Id, "A01", "lead", false, null);

        Assert.False(second.Accepted);
    }

    [Fact]
    public async Task Policy_denial_fails_the_node_and_compensates_its_side_effects()
    {
        var leaky = new ScriptedAgent("leaky", (_, _) => Task.FromResult(new AgentOutcome
        {
            Succeeded = true,
            Artifacts = [new Artifact { Key = "changes", Kind = "change-set", ProducedBy = string.Empty, Content = "diff" }],
            Changes = new ChangeSet([new FileChange("appsettings.json", FileChangeKind.Added, 1, 0, "\"key\": \"AKIAIOSFODNN7EXAMPLE\"")]),
        }));
        await using var harness = new EngineHarness();
        harness.Agent(leaky);

        var run = await harness.RunToRestAsync(NewRun(Node("impl", "leaky") with { Compensable = true }));

        Assert.Equal(RunStatus.RolledBack, run.Status);
        Assert.Equal(1, leaky.Compensations);
        Assert.Contains("security.secret-leak", run.StateOf("impl").LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Policy_escalation_requires_approval_with_the_highest_risk_and_evidence()
    {
        var changes = new ChangeSet([new FileChange("src/App/Persistence/Migrations/0002_Add.cs", FileChangeKind.Added, 10, 0, "AddColumn")]);
        await using var harness = new EngineHarness();
        harness.Agent(new ScriptedAgent("impl", (_, _) => Task.FromResult(new AgentOutcome { Succeeded = true, Changes = changes })));

        var run = await harness.RunToRestAsync(NewRun(Node("impl", "impl") with { Risk = RiskLevel.Low }));
        var approval = Assert.Single(run.Approvals);

        Assert.Equal(RunStatus.AwaitingApproval, run.Status);
        Assert.Equal(RiskLevel.High, approval.Risk);
        Assert.Contains(approval.Evidence, e => e.Contains("protected-paths", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Failing_entry_gate_blocks_the_node()
    {
        var agent = ScriptedAgent.Succeeds("design");
        await using var harness = new EngineHarness();
        harness.Agent(agent);

        var run = await harness.RunToRestAsync(NewRun(Node("design", "design") with { EntryGates = ["artifact:requirements.spec"] }));

        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Equal(0, agent.Calls);
        Assert.Contains("Entry gate failed", run.StateOf("design").LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failing_exit_gate_fails_the_node()
    {
        await using var harness = new EngineHarness();
        harness.Agent(new ScriptedAgent("tests", (_, _) => Task.FromResult(new AgentOutcome
        {
            Succeeded = true,
            Metrics = new Dictionary<string, string> { ["passed"] = "9", ["failed"] = "1" },
        })));

        var run = await harness.RunToRestAsync(NewRun(Node("tests", "tests") with { ExitGates = ["tests.passed"] }));

        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Contains("1 of 10 tests failed", run.StateOf("tests").LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Auto_approval_is_off_unless_enabled_and_never_above_its_ceiling()
    {
        await using var harness = new EngineHarness(new EngineOptions { AllowAutoApproval = true, AutoApproveUpTo = RiskLevel.Low });
        harness.Agent(ScriptedAgent.Succeeds("low")).Agent(ScriptedAgent.Succeeds("high"));
        var run = NewRun(
            Node("low", "low") with { RequiresApproval = true, Risk = RiskLevel.Low },
            Node("high", "high", "low") with { RequiresApproval = true, Risk = RiskLevel.High });
        run.Settings[RunSettings.AutoApprove] = "true";

        run = await harness.RunToRestAsync(run);

        Assert.Equal(NodeStatus.Succeeded, run.StateOf("low").Status);
        Assert.Equal(NodeStatus.AwaitingApproval, run.StateOf("high").Status);
        Assert.Contains(run.Decisions, d => d.Actor == "policy:auto-approval");
    }

    [Fact]
    public async Task Approval_on_one_branch_does_not_block_a_parallel_branch()
    {
        var parallel = ScriptedAgent.Succeeds("parallel");
        await using var harness = new EngineHarness();
        harness.Agent(ScriptedAgent.Succeeds("root")).Agent(ScriptedAgent.Succeeds("gated")).Agent(parallel);

        var run = await harness.RunToRestAsync(NewRun(
            Node("root", "root"),
            Node("gated", "gated", "root") with { RequiresApproval = true },
            Node("parallel", "parallel", "root")));

        Assert.Equal(RunStatus.AwaitingApproval, run.Status);
        Assert.Equal(NodeStatus.Succeeded, run.StateOf("parallel").Status);
    }
}
