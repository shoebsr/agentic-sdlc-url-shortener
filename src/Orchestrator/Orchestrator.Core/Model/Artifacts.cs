using System.Security.Cryptography;
using System.Text;

namespace Orchestrator.Core.Model;

public sealed record Artifact
{
    public required string Key { get; init; }

    public required string Kind { get; init; }

    public required string ProducedBy { get; init; }

    public required string Content { get; init; }

    public string MediaType { get; init; } = "text/markdown";

    public int Version { get; init; } = 1;

    public DateTimeOffset CreatedAt { get; init; }

    public IReadOnlyDictionary<string, string> DerivedFrom { get; init; } = new Dictionary<string, string>();

    public string Hash => ContentHash.Of(Content);
}

public sealed record Decision
{
    public required string Id { get; init; }

    public required string NodeId { get; init; }

    public required string Actor { get; init; }

    public required string Summary { get; init; }

    public string Rationale { get; init; } = string.Empty;

    public IReadOnlyList<string> AlternativesConsidered { get; init; } = [];

    public IReadOnlyDictionary<string, string> InputHashes { get; init; } = new Dictionary<string, string>();

    public int PlanVersion { get; init; }

    public DateTimeOffset At { get; init; }
}

public sealed record ApprovalRequest
{
    public required string Id { get; init; }

    public required string NodeId { get; init; }

    public required string Reason { get; init; }

    public required RiskLevel Risk { get; init; }

    public IReadOnlyList<string> Evidence { get; init; } = [];

    public ApprovalStatus Status { get; init; } = ApprovalStatus.Pending;

    public DateTimeOffset RequestedAt { get; init; }

    public string? DecidedBy { get; init; }

    public string? Comment { get; init; }

    public DateTimeOffset? DecidedAt { get; init; }
}

public sealed record ClarificationQuestion(string Id, string Question, string? SuggestedDefault, string Category);

public static class ContentHash
{
    public static string Of(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content ?? string.Empty)))[..16];
}
