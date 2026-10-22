using Orchestrator.Core.Model;

namespace Orchestrator.Core.Abstractions;

public sealed record AuditEvent
{
    public long Sequence { get; init; }

    public required string RunId { get; init; }

    public required string Type { get; init; }

    public string? NodeId { get; init; }

    public string Actor { get; init; } = "orchestrator";

    public string Message { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, string> Data { get; init; } = new Dictionary<string, string>();

    public DateTimeOffset At { get; init; }

    public string PreviousHash { get; init; } = string.Empty;

    public string Hash { get; init; } = string.Empty;
}

public interface IAuditLog
{
    Task<AuditEvent> AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken);

    Task<IReadOnlyList<AuditEvent>> ReadAsync(string? runId, CancellationToken cancellationToken);

    Task<AuditVerification> VerifyAsync(CancellationToken cancellationToken);
}

public sealed record AuditVerification(bool Intact, long EventsChecked, long? FirstBrokenSequence);

public interface IRunEventPublisher
{
    void Publish(AuditEvent auditEvent);

    IAsyncEnumerable<AuditEvent> SubscribeAsync(string? runId, CancellationToken cancellationToken);
}

public interface IReliabilityMetrics
{
    void NodeStarted(WorkflowRun run, WorkflowNode node);

    void NodeSucceeded(WorkflowRun run, WorkflowNode node, TimeSpan duration);

    void NodeFailed(WorkflowRun run, WorkflowNode node, string reason);

    void Retried(WorkflowRun run, WorkflowNode node, int attempt);

    void FellBack(WorkflowRun run, WorkflowNode node);

    void RolledBack(WorkflowRun run, WorkflowNode node);

    void Recovered(WorkflowRun run, WorkflowNode node, TimeSpan timeToRecover);

    void ApprovalWaited(WorkflowRun run, TimeSpan waited);

    void RunCompleted(WorkflowRun run, TimeSpan endToEnd);

    MetricsSnapshot Snapshot();
}

public sealed record MetricsSnapshot(
    int RunsCompleted,
    int RunsSucceeded,
    double RunSuccessRate,
    int NodeExecutions,
    int NodeFailures,
    double NodeSuccessRate,
    int Retries,
    int Fallbacks,
    int Rollbacks,
    double RetryRate,
    double RollbackRate,
    double MeanTimeToRecoverSeconds,
    double MeanEndToEndSeconds,
    double P95EndToEndSeconds,
    double MeanApprovalWaitSeconds,
    IReadOnlyDictionary<string, StageMetrics> ByStage);

public sealed record StageMetrics(int Executions, int Failures, double MeanDurationSeconds);
