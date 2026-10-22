using System.ComponentModel.DataAnnotations;
using Orchestrator.Core.Model;

namespace Orchestrator.Governance;

public sealed class GovernanceOptions
{
    public const string SectionName = "Orchestrator:Governance";

    [Range(1, 1000)]
    public int MaxFilesWithoutReview { get; set; } = 12;

    [Range(1, 100_000)]
    public int MaxLinesWithoutReview { get; set; } = 400;

    [Range(0, 100)]
    public double MinimumPassRate { get; set; } = 100;

    public IList<string> ProtectedPaths { get; } =
    [
        "/Migrations/",
        ".github/workflows",
        "deploy/",
        "/Security/",
        "Directory.Build.props",
    ];

    public IList<ApproverOptions> Approvers { get; } = [];

    public bool RequireSegregationOfDuties { get; set; } = true;
}

public sealed class ApproverOptions
{
    [Required]
    public string Name { get; set; } = string.Empty;

    public string Role { get; set; } = string.Empty;

    public RiskLevel MaxRisk { get; set; } = RiskLevel.Medium;
}
