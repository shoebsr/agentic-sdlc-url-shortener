using System.ComponentModel.DataAnnotations;
using Orchestrator.Core.Model;

namespace Orchestrator.Core;

public sealed class EngineOptions
{
    public const string SectionName = "Orchestrator:Engine";

    [Range(1, 32)]
    public int MaxParallelNodes { get; set; } = 4;

    public RiskLevel AutoApproveUpTo { get; set; } = RiskLevel.Low;

    public bool AllowAutoApproval { get; set; }
}

public static class RunSettings
{
    public const string ExecutionMode = "execution";
    public const string Faults = "faults";
    public const string AutoApprove = "autoApprove";
    public const string Recipe = "recipe";
    public const string Patch = "patch";
    public const string Baseline = "baseline";
    public const string Feature = "feature";
}

public static class ArtifactKeys
{
    public const string RequirementsSpec = "requirements.spec";
    public const string ClarificationAnswers = "clarification.answers";
    public const string ClarifiedRequirements = "requirements.clarified";
    public const string ImpactAnalysis = "impact.analysis";
    public const string DesignDoc = "design.doc";
    public const string ApiContract = "design.api";
    public const string TaskPlan = "plan.tasks";
    public const string ChangeSummary = "implementation.changes";
    public const string BuildReport = "verify.build";
    public const string UnitTestReport = "tests.unit";
    public const string IntegrationTestReport = "tests.integration";
    public const string SecurityReport = "security.report";
    public const string Documentation = "docs.feature";
    public const string ReleaseChecklist = "release.checklist";
    public const string ReleaseNotes = "release.notes";
    public const string ReleasePackage = "release.package";
    public const string ReleasePreview = "release.preview";
}
