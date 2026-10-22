using Orchestrator.Core.Abstractions;

namespace Orchestrator.Governance;

public sealed class PolicyEngine(IEnumerable<IPolicy> policies) : IPolicyEngine
{
    private readonly IReadOnlyList<IPolicy> _policies = [.. policies];

    public IReadOnlyList<PolicyVerdict> Evaluate(PolicyContext context) =>
        [.. _policies.Select(p => p.Evaluate(context))];
}
