using Orchestrator.Core.Model;

namespace Orchestrator.Agents;

public static class AgentNames
{
    public const string RequirementsAnalyst = "requirements-analyst";
    public const string RequirementsRules = "requirements-rules";
    public const string ClarificationBroker = "clarification-broker";
    public const string CodebaseAnalyst = "codebase-analyst";
    public const string SolutionArchitect = "solution-architect";
    public const string DeliveryPlanner = "delivery-planner";
    public const string ImplementationEngineer = "implementation-engineer";
    public const string BuildVerifier = "build-verifier";
    public const string TestRunner = "test-runner";
    public const string DiagnosticTestRunner = "test-runner-diagnostic";
    public const string SecurityReviewer = "security-reviewer";
    public const string TechnicalWriter = "technical-writer";
    public const string ReleaseManager = "release-manager";
    public const string ReleasePublisher = "release-publisher";
}

internal static class Produce
{
    public static Artifact Markdown(string key, string kind, string content) => new()
    {
        Key = key,
        Kind = kind,
        ProducedBy = string.Empty,
        Content = content,
        MediaType = "text/markdown",
    };

    public static Artifact Json(string key, string kind, string content) => new()
    {
        Key = key,
        Kind = kind,
        ProducedBy = string.Empty,
        Content = content,
        MediaType = "application/json",
    };
}
