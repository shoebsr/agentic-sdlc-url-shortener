using Orchestrator.Core;
using Orchestrator.Core.Model;

namespace Orchestrator.Agents.Workflows;

public static class WorkflowTemplates
{
    private static string Artifact(string key) => "artifact:" + key;

    public static IReadOnlyList<WorkflowNode> For(ScenarioKind kind)
    {
        var greenfield = kind == ScenarioKind.Greenfield;
        var nodes = new List<WorkflowNode>
        {
            new()
            {
                Id = "requirements",
                Title = "Requirement analysis",
                Stage = Stage.Requirements,
                Agent = AgentNames.RequirementsAnalyst,
                FallbackAgent = AgentNames.RequirementsRules,
                ExitGates = [Artifact(ArtifactKeys.RequirementsSpec)],
            },
        };

        var designDependsOn = "requirements";
        if (!greenfield)
        {
            nodes.Add(new WorkflowNode
            {
                Id = "impact-analysis",
                Title = "Codebase impact analysis",
                Stage = Stage.Design,
                Agent = AgentNames.CodebaseAnalyst,
                DependsOn = ["requirements"],
                EntryGates = ["requirements.unambiguous"],
                ExitGates = [Artifact(ArtifactKeys.ImpactAnalysis)],
            });
            designDependsOn = "impact-analysis";
        }

        nodes.AddRange(
        [
            new()
            {
                Id = "design",
                Title = "Architecture & design",
                Stage = Stage.Design,
                Agent = AgentNames.SolutionArchitect,
                DependsOn = [designDependsOn],
                EntryGates = ["requirements.unambiguous"],
                ExitGates = [Artifact(ArtifactKeys.DesignDoc)],
                RequiresApproval = true,
                Risk = RiskLevel.Medium,
            },
            new()
            {
                Id = "planning",
                Title = "Task decomposition",
                Stage = Stage.Planning,
                Agent = AgentNames.DeliveryPlanner,
                DependsOn = ["design"],
                ExitGates = [Artifact(ArtifactKeys.TaskPlan)],
            },
            new()
            {
                Id = "implementation",
                Title = "Implementation",
                Stage = Stage.Implementation,
                Agent = AgentNames.ImplementationEngineer,
                DependsOn = ["planning"],
                ExitGates = ["changes.produced"],
                RequiresApproval = !greenfield,
                Risk = RiskLevel.Medium,
                Compensable = true,
                Retry = RetryPolicy.None,
                Timeout = TimeSpan.FromMinutes(5),
            },
            new()
            {
                Id = "verify",
                Title = "Build verification",
                Stage = Stage.Verification,
                Agent = AgentNames.BuildVerifier,
                DependsOn = ["implementation"],
                ExitGates = ["build.succeeded"],
                Timeout = TimeSpan.FromMinutes(15),
            },
            new()
            {
                Id = "unit-tests",
                Title = "Unit tests",
                Stage = Stage.Testing,
                Agent = AgentNames.TestRunner,
                FallbackAgent = AgentNames.DiagnosticTestRunner,
                DependsOn = ["verify"],
                ExitGates = ["tests.passed"],
            },
            new()
            {
                Id = "integration-tests",
                Title = "Integration tests",
                Stage = Stage.Testing,
                Agent = AgentNames.TestRunner,
                FallbackAgent = AgentNames.DiagnosticTestRunner,
                DependsOn = ["verify"],
                ExitGates = ["tests.passed"],
            },
            new()
            {
                Id = "security-review",
                Title = "Security review",
                Stage = Stage.Verification,
                Agent = AgentNames.SecurityReviewer,
                DependsOn = ["verify"],
                ExitGates = ["security.no-critical"],
            },
            new()
            {
                Id = "documentation",
                Title = "Documentation",
                Stage = Stage.Documentation,
                Agent = AgentNames.TechnicalWriter,
                DependsOn = ["implementation"],
                ExitGates = [Artifact(ArtifactKeys.Documentation)],
            },
            new()
            {
                Id = "release-readiness",
                Title = "Release readiness (go/no-go)",
                Stage = Stage.ReleaseReadiness,
                Agent = AgentNames.ReleaseManager,
                DependsOn = ["unit-tests", "integration-tests", "security-review", "documentation"],
                ExitGates = ["readiness.complete"],
                RequiresApproval = true,
                Risk = RiskLevel.High,
            },
            new()
            {
                Id = "release",
                Title = "Release package",
                Stage = Stage.Release,
                Agent = AgentNames.ReleasePublisher,
                DependsOn = ["release-readiness"],
                EntryGates = ["approvals.cleared"],
                ExitGates = [Artifact(ArtifactKeys.ReleasePackage)],
                Compensable = true,
                Retry = RetryPolicy.None,
                Risk = RiskLevel.High,
            },
        ]);

        return nodes;
    }
}
