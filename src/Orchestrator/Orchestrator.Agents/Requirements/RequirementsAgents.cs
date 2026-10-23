using System.Text.Json;
using System.Text.RegularExpressions;
using Orchestrator.Agents.Knowledge;
using Orchestrator.Agents.Llm;
using Orchestrator.Core;
using Orchestrator.Core.Abstractions;
using Orchestrator.Core.Model;

namespace Orchestrator.Agents.Requirements;

public static partial class RequirementsAnalysis
{
    private static readonly string[] BaselineNonFunctional =
    [
        "All errors are RFC 9457 problem details with stable error codes",
        "Security headers on every response; no server banner",
        "No secrets in source control; credentials supplied via environment",
        "Every behaviour change is covered by unit or integration tests",
    ];

    public static Specification Analyze(string requirement, ScenarioKind kind, string source)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        var capabilities = Catalog.Detect(requirement, kind != ScenarioKind.Greenfield);
        var ambiguities = new List<Ambiguity>();

        foreach (var vague in Catalog.VagueTerms)
        {
            if (Regex.IsMatch(requirement, $@"\b{Regex.Escape(vague.Term)}(r|er|st|est|ly|ness)?\b", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1))
                && ambiguities.All(a => a.Category != vague.Category)
                && !HasMeasurableTarget(requirement, vague.Category))
            {
                ambiguities.Add(new Ambiguity($"Q{ambiguities.Count + 1}", vague.Term, vague.Category, vague.Question, vague.SuggestedDefault));
            }
        }

        if (kind != ScenarioKind.Greenfield && ambiguities.Any(a => a.Category == "security-scope"))
        {
            var existing = Catalog.ExistingDataQuestion;
            ambiguities.Add(new Ambiguity($"Q{ambiguities.Count + 1}", existing.Term, existing.Category, existing.Question, existing.SuggestedDefault));
        }

        if (capabilities.Count == 0 && ambiguities.Count == 0)
        {
            ambiguities.Add(new Ambiguity("Q1", "scope", "scope", "Which concrete capability should change? The request did not map to a known capability.", "No default - scope must be confirmed"));
        }

        var assumptions = new List<string>
        {
            "SQL Server remains the system of record; Redis is a cache and queue, never a source of truth",
            "Traffic enters through the API gateway, which supplies the trusted client address",
        };
        if (kind == ScenarioKind.Brownfield || kind == ScenarioKind.Ambiguous)
        {
            assumptions.Add("Public API changes must be backward compatible; existing clients keep working unchanged");
        }

        return new Specification(
            Summarize(requirement),
            [.. capabilities.Select(c => c.Id)],
            [.. capabilities.SelectMany(c => c.AcceptanceCriteria)],
            [.. BaselineNonFunctional],
            ambiguities,
            assumptions,
            new Dictionary<string, string>(),
            source);
    }

    public static AgentOutcome ToOutcome(Specification spec, AgentContext context, string reasoning)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(context);

        var questions = spec.Ambiguities.Select(a => new ClarificationQuestion(a.Id, a.Question, a.SuggestedDefault, a.Category)).ToList();
        var insertions = new List<NodeInsertion>();
        if (questions.Count > 0)
        {
            var graph = context.Run.Graph();
            var firstDependent = graph.Dependents(context.Node.Id).OrderBy(n => n.Stage).FirstOrDefault();
            if (firstDependent is not null && !graph.Contains("clarification"))
            {
                insertions.Add(new NodeInsertion(new WorkflowNode
                {
                    Id = "clarification",
                    Title = "Human clarification",
                    Stage = Stage.Clarification,
                    Agent = AgentNames.ClarificationBroker,
                    DependsOn = [context.Node.Id],
                    RequiresHumanInput = true,
                    Risk = RiskLevel.Medium,
                    Retry = RetryPolicy.None,
                    ExitGates = ["artifact:" + ArtifactKeys.ClarifiedRequirements],
                }, firstDependent.Id, $"{questions.Count} ambiguity(ies) block design; a human must clarify intent before work proceeds"));
            }
        }

        return new AgentOutcome
        {
            Succeeded = true,
            Artifacts = [Produce.Json(ArtifactKeys.RequirementsSpec, "specification", spec.ToJson())],
            Questions = questions,
            NodesToInsert = insertions,
            Decisions =
            [
                new DecisionDraft(
                    $"Normalised requirement into {spec.Capabilities.Count} capability(ies): {string.Join(", ", spec.Capabilities)}",
                    reasoning,
                    ["Treat the request literally without normalisation", "Ask the requester to rewrite the requirement"]),
                .. spec.Ambiguities.Select(a => new DecisionDraft($"Flagged ambiguity '{a.Term}' ({a.Category})", a.Question)),
            ],
            Metrics = new Dictionary<string, string>
            {
                ["capabilities"] = spec.Capabilities.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["ambiguities"] = spec.Ambiguities.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
        };
    }

    private static bool HasMeasurableTarget(string requirement, string category) =>
        category == "performance" && LatencyTarget().IsMatch(requirement);

    private static string Summarize(string requirement)
    {
        var firstSentence = requirement.Split(['.', '\n'], 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? requirement;
        return firstSentence.Length <= 200 ? firstSentence : firstSentence[..200] + "…";
    }

    [GeneratedRegex(@"\b(p9\d|\d+\s?ms|\d+\s?(rps|req/s|requests per second))\b", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LatencyTarget();
}

public sealed class RuleBasedRequirementsAgent : IAgent
{
    public string Name => AgentNames.RequirementsRules;

    public Task<AgentOutcome> ExecuteAsync(AgentContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var spec = RequirementsAnalysis.Analyze(context.Requirement, context.Run.Kind, "rules");
        return Task.FromResult(RequirementsAnalysis.ToOutcome(spec, context,
            "Rule-based analysis against the capability catalogue and vague-term lexicon"));
    }
}

public sealed class RequirementsAnalystAgent : IAgent
{
    private const string SystemPrompt =
        "You review software requirements for a URL shortener. Return only JSON: " +
        "{\"ambiguities\":[{\"term\":\"\",\"question\":\"\",\"default\":\"\"}],\"acceptanceCriteria\":[\"\"]}. " +
        "List only genuine ambiguities that block design, and testable acceptance criteria.";

    public string Name => AgentNames.RequirementsAnalyst;

    public async Task<AgentOutcome> ExecuteAsync(AgentContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var spec = RequirementsAnalysis.Analyze(context.Requirement, context.Run.Kind, "rules");
        if (!context.Llm.IsEnabled)
        {
            return RequirementsAnalysis.ToOutcome(spec, context,
                "Deterministic analysis (no LLM provider configured): capability catalogue plus vague-term lexicon");
        }

        var response = await context.Llm.CompleteAsync(new LlmRequest(SystemPrompt, context.Requirement, 4000), cancellationToken);
        var review = LlmJson.TryParse<LlmReview>(response.Text)
            ?? throw new FormatException("LLM review was not valid JSON.");

        var extra = review.Ambiguities
            .Where(a => !string.IsNullOrWhiteSpace(a.Question))
            .Select((a, i) => new Ambiguity($"Q{spec.Ambiguities.Count + i + 1}", a.Term ?? "llm", "llm-review", a.Question!, a.Default ?? "Confirm with requester"));
        var enriched = spec with
        {
            Ambiguities = [.. spec.Ambiguities, .. extra],
            AcceptanceCriteria = [.. spec.AcceptanceCriteria, .. review.AcceptanceCriteria.Where(c => !string.IsNullOrWhiteSpace(c))],
            Source = $"rules+{context.Llm.Provider}",
        };
        return RequirementsAnalysis.ToOutcome(enriched, context,
            $"Rule-based analysis enriched by {response.Model} ({response.InputTokens}+{response.OutputTokens} tokens)");
    }

    private sealed record LlmReview(IReadOnlyList<LlmAmbiguity> Ambiguities, IReadOnlyList<string> AcceptanceCriteria);

    private sealed record LlmAmbiguity(string? Term, string? Question, string? Default);
}

public sealed class ClarificationBrokerAgent : IAgent
{
    public string Name => AgentNames.ClarificationBroker;

    public Task<AgentOutcome> ExecuteAsync(AgentContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var original = Specification.FromJson(context.RequireInput(ArtifactKeys.RequirementsSpec).Content)
            ?? throw new FormatException("Requirements specification is not valid.");
        var answers = context.Input(ArtifactKeys.ClarificationAnswers) is { } answered
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(answered.Content) ?? []
            : original.Ambiguities.ToDictionary(a => a.Id, a => $"{a.SuggestedDefault} (default accepted)");

        var resolutions = original.Ambiguities.ToDictionary(
            a => $"{a.Id} {a.Term}",
            a => answers.GetValueOrDefault(a.Id, $"{a.SuggestedDefault} (default accepted)"));
        var combined = context.Requirement + " " + string.Join(" ", resolutions.Values);
        var capabilities = Catalog.Detect(combined, context.Run.Kind != ScenarioKind.Greenfield);

        var nonFunctional = original.NonFunctional.ToList();
        foreach (var ambiguity in original.Ambiguities.Where(a => a.Category is "performance" or "capacity" or "reliability"))
        {
            nonFunctional.Add($"{ambiguity.Category}: {resolutions[$"{ambiguity.Id} {ambiguity.Term}"]}");
        }

        var clarified = original with
        {
            Capabilities = [.. capabilities.Select(c => c.Id)],
            AcceptanceCriteria = [.. capabilities.SelectMany(c => c.AcceptanceCriteria)],
            NonFunctional = nonFunctional,
            Ambiguities = [],
            Resolutions = resolutions,
            Source = original.Source + "+clarified",
        };

        var outcome = new AgentOutcome
        {
            Succeeded = capabilities.Count > 0,
            Error = capabilities.Count > 0 ? null : "Clarifications did not identify an actionable capability; amend the requirement.",
            Artifacts = [Produce.Json(ArtifactKeys.ClarifiedRequirements, "specification", clarified.ToJson())],
            Decisions =
            [
                .. resolutions.Select(r => new DecisionDraft($"Interpreted {r.Key} as: {r.Value}", "Human clarification captured before design")),
                new DecisionDraft(
                    $"Clarified scope maps to: {string.Join(", ", capabilities.Select(c => c.Title))}",
                    "Capabilities re-derived from the requirement plus clarification answers"),
            ],
        };
        return Task.FromResult(outcome);
    }
}
