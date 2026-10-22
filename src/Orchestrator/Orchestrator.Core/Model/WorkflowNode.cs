namespace Orchestrator.Core.Model;

public sealed record RetryPolicy(int MaxAttempts, TimeSpan InitialBackoff, double BackoffMultiplier = 2)
{
    public static readonly RetryPolicy None = new(1, TimeSpan.Zero);
    public static readonly RetryPolicy Standard = new(3, TimeSpan.FromMilliseconds(250));

    public TimeSpan BackoffFor(int attempt) =>
        TimeSpan.FromMilliseconds(InitialBackoff.TotalMilliseconds * Math.Pow(BackoffMultiplier, attempt - 1));
}

public sealed record WorkflowNode
{
    public required string Id { get; init; }

    public required Stage Stage { get; init; }

    public required string Agent { get; init; }

    public string? FallbackAgent { get; init; }

    public string Title { get; init; } = string.Empty;

    public IReadOnlyList<string> DependsOn { get; init; } = [];

    public IReadOnlyList<string> EntryGates { get; init; } = [];

    public IReadOnlyList<string> ExitGates { get; init; } = [];

    public RiskLevel Risk { get; init; } = RiskLevel.Low;

    public bool RequiresApproval { get; init; }

    public bool RequiresHumanInput { get; init; }

    public bool Compensable { get; init; }

    public RetryPolicy Retry { get; init; } = RetryPolicy.Standard;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);
}
