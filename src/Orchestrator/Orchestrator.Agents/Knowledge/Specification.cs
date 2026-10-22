using System.Text;
using System.Text.Json;
using Orchestrator.Core;
using Orchestrator.Core.Abstractions;

namespace Orchestrator.Agents.Knowledge;

public sealed record Ambiguity(string Id, string Term, string Category, string Question, string SuggestedDefault);

public sealed record Specification(
    string Summary,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> AcceptanceCriteria,
    IReadOnlyList<string> NonFunctional,
    IReadOnlyList<Ambiguity> Ambiguities,
    IReadOnlyList<string> Assumptions,
    IReadOnlyDictionary<string, string> Resolutions,
    string Source)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static Specification? FromJson(string json) => JsonSerializer.Deserialize<Specification>(json, Json);

    public static Specification Current(AgentContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var artifact = context.Input(ArtifactKeys.ClarifiedRequirements) ?? context.RequireInput(ArtifactKeys.RequirementsSpec);
        return FromJson(artifact.Content) ?? throw new FormatException($"Artifact {artifact.Key} is not a valid specification.");
    }

    public IReadOnlyList<Capability> ResolvedCapabilities() =>
        [.. Capabilities.Select(Catalog.Find).OfType<Capability>()];

    public string ToMarkdown()
    {
        var md = new StringBuilder();
        md.AppendLine($"**Summary:** {Summary}").AppendLine();
        md.AppendLine("| Capability | Title |").AppendLine("|---|---|");
        foreach (var capability in ResolvedCapabilities())
        {
            md.AppendLine($"| `{capability.Id}` | {capability.Title} |");
        }

        Section(md, "Acceptance criteria", AcceptanceCriteria);
        Section(md, "Non-functional requirements", NonFunctional);
        Section(md, "Open ambiguities", Ambiguities.Select(a => $"**{a.Id}** ({a.Category}, from \"{a.Term}\"): {a.Question} _Default: {a.SuggestedDefault}_"));
        Section(md, "Resolutions", Resolutions.Select(kv => $"**{kv.Key}**: {kv.Value}"));
        Section(md, "Assumptions", Assumptions);
        return md.ToString();
    }

    private static void Section(StringBuilder md, string title, IEnumerable<string> items)
    {
        var list = items.ToList();
        if (list.Count == 0)
        {
            return;
        }

        md.AppendLine().AppendLine($"### {title}");
        foreach (var item in list)
        {
            md.AppendLine($"- {item}");
        }
    }
}
