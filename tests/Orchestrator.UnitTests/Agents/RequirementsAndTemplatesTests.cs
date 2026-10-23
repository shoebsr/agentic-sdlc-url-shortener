using Orchestrator.Agents.Knowledge;
using Orchestrator.Agents.Requirements;
using Orchestrator.Agents.Workflows;
using Orchestrator.Core;
using Orchestrator.Core.Engine;
using Orchestrator.Core.Model;

namespace Orchestrator.UnitTests.Agents;

public class RequirementsAndTemplatesTests
{
    private const string Greenfield =
        "Build a URL shortener service with APIs to create short links, optionally with a custom alias and an expiry, " +
        "redirect visitors, and report click analytics. It must be reliable under load, with rate limiting and health checks.";

    [Fact]
    public void Greenfield_requirement_maps_to_the_core_capabilities_without_ambiguity()
    {
        var spec = RequirementsAnalysis.Analyze(Greenfield, ScenarioKind.Greenfield, "test");

        Assert.Equal(["shorten-api", "redirect", "analytics", "reliability", "custom-alias", "expiry"], spec.Capabilities);
        Assert.Empty(spec.Ambiguities);
        Assert.NotEmpty(spec.AcceptanceCriteria);
    }

    [Fact]
    public void Brownfield_requirement_scopes_only_net_new_capabilities()
    {
        var spec = RequirementsAnalysis.Analyze(
            "Add an optional maximum-click limit to short links. Once reached it must stop redirecting.", ScenarioKind.Brownfield, "test");

        Assert.Equal(["click-cap"], spec.Capabilities);
    }

    [Fact]
    public void Vague_requirement_produces_questions_with_defaults_and_an_existing_data_question()
    {
        var spec = RequirementsAnalysis.Analyze("Make links safer, and it should stay fast.", ScenarioKind.Ambiguous, "test");

        Assert.Equal(["performance", "security-scope", "data-scope"], spec.Ambiguities.Select(a => a.Category));
        Assert.All(spec.Ambiguities, a => Assert.False(string.IsNullOrWhiteSpace(a.SuggestedDefault)));
    }

    [Fact]
    public void Measurable_performance_target_is_not_flagged_as_ambiguous()
    {
        var spec = RequirementsAnalysis.Analyze("Redirects must be fast: p95 under 50 ms. Add a blocklist for malicious domains.", ScenarioKind.Brownfield, "test");

        Assert.DoesNotContain(spec.Ambiguities, a => a.Category == "performance");
    }

    [Fact]
    public void Unrecognised_scope_is_flagged_rather_than_guessed()
    {
        var spec = RequirementsAnalysis.Analyze("Integrate with the mainframe ledger.", ScenarioKind.Brownfield, "test");

        Assert.Empty(spec.Capabilities);
        Assert.Equal("scope", Assert.Single(spec.Ambiguities).Category);
    }

    [Fact]
    public void Clarification_answers_drive_capability_selection()
    {
        var capabilities = Catalog.Detect("Make links safer. Block malicious destination domains", brownfield: true);

        Assert.Equal("destination-blocklist", Assert.Single(capabilities).Id);
    }

    [Fact]
    public void Specification_round_trips_through_json()
    {
        var spec = RequirementsAnalysis.Analyze(Greenfield, ScenarioKind.Greenfield, "test");

        var restored = Specification.FromJson(spec.ToJson())!;

        Assert.Equal(spec.Capabilities, restored.Capabilities);
        Assert.Equal(spec.AcceptanceCriteria, restored.AcceptanceCriteria);
    }

    [Theory]
    [InlineData(ScenarioKind.Greenfield, 12)]
    [InlineData(ScenarioKind.Brownfield, 13)]
    [InlineData(ScenarioKind.Ambiguous, 13)]
    public void Workflow_templates_are_valid_dags_with_governed_checkpoints(ScenarioKind kind, int nodes)
    {
        var graph = new WorkflowGraph(WorkflowTemplates.For(kind));

        Assert.Equal(nodes, graph.Nodes.Count);
        Assert.Equal("requirements", graph.TopologicalOrder()[0]);
        Assert.Equal("preview", graph.TopologicalOrder()[^1]);
        Assert.Equal(["release"], graph["preview"].DependsOn);
        Assert.True(graph["design"].RequiresApproval);
        Assert.True(graph["release-readiness"].RequiresApproval);
        Assert.Equal(4, graph["release-readiness"].DependsOn.Count);
        Assert.Contains("approvals.cleared", graph["release"].EntryGates);
        Assert.Equal(kind != ScenarioKind.Greenfield, graph.Contains("impact-analysis"));
        Assert.Equal(kind != ScenarioKind.Greenfield, graph["implementation"].RequiresApproval);
    }

    [Fact]
    public void Templates_run_tests_and_security_in_parallel_after_the_build()
    {
        var graph = new WorkflowGraph(WorkflowTemplates.For(ScenarioKind.Brownfield));

        Assert.All(["unit-tests", "integration-tests", "security-review"], id => Assert.Equal(["verify"], graph[id].DependsOn));
        Assert.Equal(["implementation"], graph["documentation"].DependsOn);
    }

    [Theory]
    [InlineData("unit-tests:transient:2", FaultKind.Transient, 2)]
    [InlineData("requirements:primary", FaultKind.Primary, 1)]
    [InlineData("release:fatal", FaultKind.Fatal, 1)]
    public void Fault_plans_parse(string spec, FaultKind kind, int times)
    {
        var run = new WorkflowRun
        {
            Id = "r", Title = "t", Kind = ScenarioKind.Greenfield, Requirement = "x", Nodes = [], Settings = { [RunSettings.Faults] = spec + ";bogus" },
        };

        var fault = Assert.Single(FaultPlan.Parse(run));

        Assert.Equal((kind, times), (fault.Kind, fault.Times));
    }
}
