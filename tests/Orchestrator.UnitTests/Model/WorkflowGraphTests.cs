using Orchestrator.Core.Model;
using static Orchestrator.UnitTests.Support.EngineHarness;

namespace Orchestrator.UnitTests.Model;

public class WorkflowGraphTests
{
    [Fact]
    public void Topological_order_respects_dependencies()
    {
        var graph = new WorkflowGraph([Node("c", "x", "b"), Node("a", "x"), Node("b", "x", "a"), Node("d", "x", "a")]);

        var order = graph.TopologicalOrder().ToList();

        Assert.True(order.IndexOf("a") < order.IndexOf("b"));
        Assert.True(order.IndexOf("b") < order.IndexOf("c"));
        Assert.True(order.IndexOf("a") < order.IndexOf("d"));
    }

    [Fact]
    public void Cycles_are_rejected_with_the_nodes_involved()
    {
        var error = Assert.Throws<WorkflowDefinitionException>(() =>
            new WorkflowGraph([Node("a", "x", "c"), Node("b", "x", "a"), Node("c", "x", "b")]));

        Assert.Contains("cycle", error.Message, StringComparison.Ordinal);
        Assert.Contains("a", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_dependencies_are_rejected()
    {
        var error = Assert.Throws<WorkflowDefinitionException>(() => new WorkflowGraph([Node("a", "x", "ghost")]));

        Assert.Contains("ghost", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_node_ids_are_rejected()
    {
        Assert.Throws<WorkflowDefinitionException>(() => new WorkflowGraph([Node("a", "x"), Node("a", "y")]));
    }

    [Fact]
    public void Descendants_include_transitive_dependents_only()
    {
        var graph = new WorkflowGraph([Node("a", "x"), Node("b", "x", "a"), Node("c", "x", "b"), Node("d", "x")]);

        Assert.Equal(new HashSet<string> { "b", "c" }, graph.Descendants("a"));
        Assert.Empty(graph.Descendants("c"));
    }

    [Fact]
    public void Inserting_a_node_rewires_the_target_and_keeps_the_graph_valid()
    {
        var graph = new WorkflowGraph([Node("req", "x"), Node("design", "x", "req")]);

        var updated = graph.WithInsertedNode(Node("clarify", "x", "req"), before: "design");

        Assert.Contains("clarify", updated["design"].DependsOn);
        Assert.Equal(["req", "clarify", "design"], updated.TopologicalOrder());
    }

    [Fact]
    public void Insertion_that_would_create_a_cycle_is_rejected()
    {
        var graph = new WorkflowGraph([Node("a", "x"), Node("b", "x", "a")]);

        Assert.Throws<WorkflowDefinitionException>(() => graph.WithInsertedNode(Node("loop", "x", "b"), before: "a"));
    }

    [Fact]
    public void Retry_backoff_grows_exponentially()
    {
        var policy = new RetryPolicy(4, TimeSpan.FromMilliseconds(100));

        Assert.Equal(TimeSpan.FromMilliseconds(100), policy.BackoffFor(1));
        Assert.Equal(TimeSpan.FromMilliseconds(200), policy.BackoffFor(2));
        Assert.Equal(TimeSpan.FromMilliseconds(400), policy.BackoffFor(3));
    }
}
