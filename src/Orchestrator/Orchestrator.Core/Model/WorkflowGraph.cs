namespace Orchestrator.Core.Model;

public sealed class WorkflowGraph
{
    private readonly Dictionary<string, WorkflowNode> _nodes;

    public WorkflowGraph(IEnumerable<WorkflowNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        _nodes = new Dictionary<string, WorkflowNode>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (!_nodes.TryAdd(node.Id, node))
            {
                throw new WorkflowDefinitionException($"Duplicate node id '{node.Id}'.");
            }
        }

        Validate();
    }

    public IReadOnlyCollection<WorkflowNode> Nodes => _nodes.Values;

    public WorkflowNode this[string id] => _nodes.TryGetValue(id, out var node)
        ? node
        : throw new KeyNotFoundException($"Node '{id}' is not part of the workflow.");

    public bool Contains(string id) => _nodes.ContainsKey(id);

    public IEnumerable<WorkflowNode> Dependents(string id) => _nodes.Values.Where(n => n.DependsOn.Contains(id));

    public IReadOnlyList<string> TopologicalOrder()
    {
        var inDegree = _nodes.Values.ToDictionary(n => n.Id, n => n.DependsOn.Count, StringComparer.Ordinal);
        var ready = new PriorityQueue<string, (int Stage, string Id)>();
        foreach (var node in _nodes.Values.Where(n => n.DependsOn.Count == 0))
        {
            ready.Enqueue(node.Id, ((int)node.Stage, node.Id));
        }

        var order = new List<string>(_nodes.Count);
        while (ready.TryDequeue(out var id, out _))
        {
            order.Add(id);
            foreach (var dependent in Dependents(id))
            {
                if (--inDegree[dependent.Id] == 0)
                {
                    ready.Enqueue(dependent.Id, ((int)dependent.Stage, dependent.Id));
                }
            }
        }

        if (order.Count != _nodes.Count)
        {
            var cyclic = inDegree.Where(kv => kv.Value > 0).Select(kv => kv.Key).Order(StringComparer.Ordinal);
            throw new WorkflowDefinitionException($"Workflow contains a cycle through: {string.Join(", ", cyclic)}.");
        }

        return order;
    }

    public IReadOnlySet<string> Descendants(string id)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>([id]);
        while (stack.TryPop(out var current))
        {
            foreach (var dependent in Dependents(current))
            {
                if (seen.Add(dependent.Id))
                {
                    stack.Push(dependent.Id);
                }
            }
        }

        return seen;
    }

    public WorkflowGraph WithInsertedNode(WorkflowNode node, string before)
    {
        ArgumentNullException.ThrowIfNull(node);
        var rewired = _nodes.Values.Select(n => n.Id == before
            ? n with { DependsOn = [.. n.DependsOn, node.Id] }
            : n);
        return new WorkflowGraph([.. rewired, node]);
    }

    private void Validate()
    {
        foreach (var node in _nodes.Values)
        {
            foreach (var dependency in node.DependsOn)
            {
                if (!_nodes.ContainsKey(dependency))
                {
                    throw new WorkflowDefinitionException($"Node '{node.Id}' depends on unknown node '{dependency}'.");
                }
            }
        }

        _ = TopologicalOrder();
    }
}

public sealed class WorkflowDefinitionException(string message) : Exception(message);
