using Orchestrator.Core.Model;

namespace Orchestrator.Core.Abstractions;

public enum GatePhase
{
    Entry,
    Exit,
}

public interface IGate
{
    string Id { get; }

    GateResult Evaluate(GateContext context);
}

public sealed record GateContext(WorkflowRun Run, WorkflowNode Node, GatePhase Phase, AgentOutcome? Outcome);

public sealed record GateResult(bool Passed, string Detail)
{
    public static GateResult Pass(string detail) => new(true, detail);

    public static GateResult Fail(string detail) => new(false, detail);
}

public interface IGateRegistry
{
    IGate Resolve(string id);
}

public enum PolicyEffect
{
    Allow,
    RequireApproval,
    Deny,
}

public sealed record PolicyVerdict(string PolicyId, PolicyEffect Effect, string Reason, RiskLevel Risk)
{
    public static PolicyVerdict Allow(string policyId) => new(policyId, PolicyEffect.Allow, "compliant", RiskLevel.Low);
}

public sealed record PolicyContext(WorkflowRun Run, WorkflowNode Node, AgentOutcome Outcome);

public interface IPolicy
{
    string Id { get; }

    PolicyVerdict Evaluate(PolicyContext context);
}

public interface IPolicyEngine
{
    IReadOnlyList<PolicyVerdict> Evaluate(PolicyContext context);
}

public interface IApprovalAuthority
{
    ApprovalCheck CanApprove(WorkflowRun run, ApprovalRequest request, string approver);
}

public sealed record ApprovalCheck(bool Allowed, string Reason);

public interface IKillSwitch
{
    bool IsEngaged { get; }

    string? Reason { get; }

    void Engage(string reason);

    void Release();
}
