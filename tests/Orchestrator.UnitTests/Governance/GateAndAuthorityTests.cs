using Microsoft.Extensions.Options;
using Orchestrator.Core;
using Orchestrator.Core.Abstractions;
using Orchestrator.Core.Model;
using Orchestrator.Governance;
using static Orchestrator.UnitTests.Support.EngineHarness;

namespace Orchestrator.UnitTests.Governance;

public class GateAndAuthorityTests
{
    private static readonly GateRegistry Gates = new(Options.Create(new GovernanceOptions()));

    private static GateResult Evaluate(string gate, WorkflowRun run, AgentOutcome? outcome = null) =>
        Gates.Resolve(gate).Evaluate(new GateContext(run, run.Nodes[0], GatePhase.Exit, outcome));

    private static AgentOutcome WithMetrics(params (string Key, string Value)[] metrics) =>
        new() { Succeeded = true, Metrics = metrics.ToDictionary(m => m.Key, m => m.Value) };

    [Fact]
    public void Artifact_gate_checks_outcome_then_run_artifacts()
    {
        var run = NewRun(Node("n", "a"));

        Assert.False(Evaluate("artifact:design.doc", run).Passed);
        Assert.True(Evaluate("artifact:design.doc", run, Support.ScriptedAgent.Success("design.doc")).Passed);
    }

    [Theory]
    [InlineData("10", "0", true)]
    [InlineData("9", "1", false)]
    [InlineData("0", "0", false)]
    public void Tests_gate_requires_executed_tests_and_zero_failures(string passed, string failed, bool expected)
    {
        Assert.Equal(expected, Evaluate("tests.passed", NewRun(Node("n", "a")), WithMetrics(("passed", passed), ("failed", failed))).Passed);
    }

    [Fact]
    public void Build_and_security_gates_read_agent_metrics()
    {
        var run = NewRun(Node("n", "a"));

        Assert.True(Evaluate("build.succeeded", run, WithMetrics(("build", "succeeded"))).Passed);
        Assert.False(Evaluate("build.succeeded", run, WithMetrics(("build", "failed"))).Passed);
        Assert.True(Evaluate("security.no-critical", run, WithMetrics(("critical", "0"))).Passed);
        Assert.False(Evaluate("security.no-critical", run, WithMetrics(("critical", "2"))).Passed);
    }

    [Fact]
    public void Ambiguity_gate_passes_only_once_questions_are_answered()
    {
        var run = NewRun(Node("n", "a"));
        run.OpenQuestions = [new ClarificationQuestion("Q1", "?", "default", "scope")];

        Assert.False(Evaluate("requirements.unambiguous", run).Passed);

        run.Artifacts[ArtifactKeys.ClarificationAnswers] = Support.ScriptedAgent.Success(ArtifactKeys.ClarificationAnswers).Artifacts[0];

        Assert.True(Evaluate("requirements.unambiguous", run).Passed);
    }

    [Fact]
    public void Approvals_gate_blocks_while_any_approval_is_pending()
    {
        var run = NewRun(Node("n", "a"));
        run.Approvals.Add(new ApprovalRequest { Id = "A01", NodeId = "n", Reason = "r", Risk = RiskLevel.High });

        Assert.False(Evaluate("approvals.cleared", run).Passed);
    }

    [Fact]
    public void Unknown_gate_ids_fail_fast()
    {
        Assert.Throws<InvalidOperationException>(() => Gates.Resolve("does.not.exist"));
    }

    [Fact]
    public void Approval_authority_allows_an_authorised_independent_approver()
    {
        var options = new GovernanceOptions();
        options.Approvers.Add(new ApproverOptions { Name = "sec", Role = "Security Officer", MaxRisk = RiskLevel.Critical });
        var authority = new ApprovalAuthority(Options.Create(options));
        var request = new ApprovalRequest { Id = "A01", NodeId = "n", Reason = "r", Risk = RiskLevel.Critical };

        var check = authority.CanApprove(NewRun(Node("n", "a")), request, "SEC");

        Assert.True(check.Allowed);
        Assert.Contains("Security Officer", check.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Kill_switch_engages_with_a_reason_and_releases()
    {
        var killSwitch = new KillSwitch();

        killSwitch.Engage("incident 42");
        var engaged = (killSwitch.IsEngaged, killSwitch.Reason);
        killSwitch.Release();

        Assert.Equal((true, "incident 42"), engaged);
        Assert.False(killSwitch.IsEngaged);
    }
}
