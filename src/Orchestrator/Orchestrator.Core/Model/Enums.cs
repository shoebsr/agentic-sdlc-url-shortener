namespace Orchestrator.Core.Model;

public enum Stage
{
    Requirements,
    Clarification,
    Design,
    Planning,
    Implementation,
    Verification,
    Testing,
    Documentation,
    ReleaseReadiness,
    Release,
}

public enum RunStatus
{
    Pending,
    Running,
    AwaitingApproval,
    AwaitingInput,
    Succeeded,
    Failed,
    RolledBack,
    Halted,
}

public enum NodeStatus
{
    Pending,
    Running,
    AwaitingApproval,
    AwaitingInput,
    Succeeded,
    Failed,
    Skipped,
    RolledBack,
}

public enum RiskLevel
{
    Low,
    Medium,
    High,
    Critical,
}

public enum ScenarioKind
{
    Greenfield,
    Brownfield,
    Ambiguous,
}

public enum ApprovalStatus
{
    Pending,
    Approved,
    Rejected,
}
