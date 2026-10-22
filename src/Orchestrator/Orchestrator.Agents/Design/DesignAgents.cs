using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Orchestrator.Agents.Knowledge;
using Orchestrator.Core;
using Orchestrator.Core.Abstractions;
using Orchestrator.Core.Model;

namespace Orchestrator.Agents.Design;

public sealed partial class CodebaseAnalystAgent : IAgent
{
    public string Name => AgentNames.CodebaseAnalyst;

    public async Task<AgentOutcome> ExecuteAsync(AgentContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var spec = Specification.Current(context);
        var capabilities = spec.ResolvedCapabilities();
        var workspace = context.Workspace;
        var sources = workspace.Files("*.cs").Where(f => !f.Contains("/Migrations/", StringComparison.Ordinal)).ToList();
        var projects = workspace.Files("*.csproj").ToList();

        var md = new StringBuilder();
        md.AppendLine("## Codebase impact analysis").AppendLine();
        md.AppendLine($"Scanned **{projects.Count}** projects and **{sources.Count}** source files in the sandboxed workspace.").AppendLine();

        md.AppendLine("### Project dependency graph").AppendLine("```mermaid").AppendLine("graph LR");
        foreach (var project in projects)
        {
            var name = Path.GetFileNameWithoutExtension(project);
            var content = await workspace.ReadAsync(project, cancellationToken);
            foreach (Match reference in ProjectReference().Matches(content))
            {
                md.AppendLine($"  {Sanitize(name)} --> {Sanitize(Path.GetFileNameWithoutExtension(reference.Groups["path"].Value.Replace('\\', '/')))}");
            }
        }

        md.AppendLine("```").AppendLine();

        var impacted = new List<(string File, string Layer, string Symbols)>();
        foreach (var file in sources)
        {
            var content = await workspace.ReadAsync(file, cancellationToken);
            var symbols = capabilities.SelectMany(c => c.ImpactedSymbols).Distinct(StringComparer.Ordinal)
                .Where(s => Regex.IsMatch(content, $@"\b(class|record|interface|enum)\s+{Regex.Escape(s)}\b", RegexOptions.None, TimeSpan.FromSeconds(1)))
                .ToList();
            if (symbols.Count > 0)
            {
                impacted.Add((file, LayerOf(file), string.Join(", ", symbols)));
            }
        }

        md.AppendLine("### Impacted modules").AppendLine("| Layer | File | Defines |").AppendLine("|---|---|---|");
        foreach (var item in impacted.OrderBy(i => LayerRank(i.Layer)))
        {
            md.AppendLine($"| {item.Layer} | `{item.File}` | {item.Symbols} |");
        }

        var endpoints = new List<string>();
        foreach (var file in sources.Where(f => f.Contains("Presentation", StringComparison.Ordinal) || f.EndsWith("Program.cs", StringComparison.Ordinal)))
        {
            var content = await workspace.ReadAsync(file, cancellationToken);
            endpoints.AddRange(EndpointMapping().Matches(content).Select(m => $"{m.Groups["verb"].Value.ToUpperInvariant()} {m.Groups["route"].Value}"));
        }

        md.AppendLine().AppendLine("### Existing API surface");
        foreach (var endpoint in endpoints.Distinct(StringComparer.Ordinal))
        {
            md.AppendLine($"- `{endpoint}`");
        }

        md.AppendLine().AppendLine("### Data flows touched");
        md.AppendLine("- Create: API → ShortLinkService → ShortLinkRepository → SQL Server");
        md.AppendLine("- Redirect: Gateway → Redirect service → route cache (memory → Redis) → SQL Server on miss → click stream");
        md.AppendLine("- Analytics: Redis Stream → Analytics worker → ClickSink (idempotent batch) → SQL Server");

        var schemaChange = capabilities.Any(c => c.RequiresSchemaChange);
        md.AppendLine().AppendLine("### Change risk");
        md.AppendLine(schemaChange
            ? "- **Schema change required** → additive EF Core migration; protected path, needs approval"
            : "- No schema change required");
        if (impacted.Any(i => i.Symbols.Contains("LinkRoute", StringComparison.Ordinal) || i.Symbols.Contains("RedirectEndpoints", StringComparison.Ordinal)))
        {
            md.AppendLine("- **Redirect hot path affected** → cached route shape changes; cache entries are versioned by TTL, verify latency");
        }

        md.AppendLine("- Public contract changes must be additive (optional fields only) to keep existing clients working");

        return new AgentOutcome
        {
            Succeeded = true,
            Artifacts = [Produce.Markdown(ArtifactKeys.ImpactAnalysis, "analysis", md.ToString())],
            Decisions =
            [
                new DecisionDraft(
                    $"{impacted.Count} file(s) across {impacted.Select(i => i.Layer).Distinct().Count()} layer(s) are impacted",
                    "Derived from type definitions of capability symbols in the current codebase, not from naming guesses",
                    ["Text search on feature keywords only", "Manual inspection"]),
            ],
            Metrics = new Dictionary<string, string>
            {
                ["impactedFiles"] = impacted.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["schemaChange"] = schemaChange.ToString(),
            },
        };
    }

    private static string LayerOf(string file) => file switch
    {
        _ when file.Contains(".Domain/", StringComparison.Ordinal) => "Domain",
        _ when file.Contains(".Application/", StringComparison.Ordinal) => "Application",
        _ when file.Contains(".Infrastructure/", StringComparison.Ordinal) => "Infrastructure",
        _ when file.Contains(".Presentation/", StringComparison.Ordinal) || file.Contains(".Api/", StringComparison.Ordinal) => "Presentation",
        _ when file.StartsWith("tests/", StringComparison.Ordinal) => "Tests",
        _ => "Shared",
    };

    private static int LayerRank(string layer) => layer switch
    {
        "Domain" => 0,
        "Application" => 1,
        "Infrastructure" => 2,
        "Presentation" => 3,
        "Tests" => 4,
        _ => 5,
    };

    private static string Sanitize(string name) => name.Replace('.', '_');

    [GeneratedRegex(@"ProjectReference\s+Include=""(?<path>[^""]+)""", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ProjectReference();

    [GeneratedRegex(@"Map(?<verb>Get|Post|Put|Delete|Patch|Group)\(""(?<route>[^""]+)""", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EndpointMapping();
}

public sealed class SolutionArchitectAgent : IAgent
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    public string Name => AgentNames.SolutionArchitect;

    public Task<AgentOutcome> ExecuteAsync(AgentContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var spec = Specification.Current(context);
        var capabilities = spec.ResolvedCapabilities();
        var greenfield = context.Run.Kind == ScenarioKind.Greenfield;

        var md = new StringBuilder();
        md.AppendLine($"## Design: {context.Run.Title}").AppendLine();
        md.AppendLine($"**Plan version:** v{context.Run.PlanVersion} · **Scope:** {string.Join(", ", capabilities.Select(c => c.Title))}").AppendLine();

        if (greenfield)
        {
            md.AppendLine("### Architecture").AppendLine("```mermaid").AppendLine("graph LR");
            md.AppendLine("  Client --> Gateway[Ocelot Gateway]");
            md.AppendLine("  Gateway --> Links[Links API]");
            md.AppendLine("  Gateway --> Redirect[Redirect service xN]");
            md.AppendLine("  Links --> SQL[(SQL Server)]");
            md.AppendLine("  Redirect --> Cache[(Redis cache)]");
            md.AppendLine("  Redirect --> Stream[(Redis Stream)]");
            md.AppendLine("  Stream --> Worker[Analytics worker xN]");
            md.AppendLine("  Worker --> SQL");
            md.AppendLine("```").AppendLine();
            md.AppendLine("Clean Architecture per service: Domain → Application → Infrastructure → Presentation; hosts are thin composition roots.").AppendLine();
        }
        else if (context.Input(ArtifactKeys.ImpactAnalysis) is { } impact)
        {
            md.AppendLine($"Builds on impact analysis v{impact.Version} (hash `{impact.Hash}`).").AppendLine();
        }

        md.AppendLine("### API contract changes");
        foreach (var change in capabilities.SelectMany(c => c.ApiChanges).DefaultIfEmpty("No public API change"))
        {
            md.AppendLine($"- {change}");
        }

        md.AppendLine().AppendLine("### Data model changes");
        foreach (var change in capabilities.SelectMany(c => c.DataChanges).DefaultIfEmpty("No schema change"))
        {
            md.AppendLine($"- {change}");
        }

        md.AppendLine().AppendLine("### Non-functional requirements");
        foreach (var nfr in spec.NonFunctional)
        {
            md.AppendLine($"- {nfr}");
        }

        var choices = capabilities.SelectMany(c => c.Decisions).ToList();
        md.AppendLine().AppendLine("### Architecture decisions");
        foreach (var (choice, index) in choices.Select((c, i) => (c, i + 1)))
        {
            md.AppendLine($"**ADR-{index:00}: {choice.Decision}**  ").AppendLine($"Rationale: {choice.Rationale}  ");
            md.AppendLine($"Rejected: {string.Join("; ", choice.Alternatives)}").AppendLine();
        }

        md.AppendLine("### Risks and mitigations");
        md.AppendLine("| Risk | Mitigation |").AppendLine("|---|---|");
        md.AppendLine("| Behaviour regression in existing flows | Full unit + integration suite must pass at the testing gate |");
        md.AppendLine("| Unsafe change slips through | Policy engine (secrets, insecure APIs, PII, protected paths) + human approval |");
        if (capabilities.Any(c => c.RequiresSchemaChange))
        {
            md.AppendLine("| Migration fails or loses data | Additive-only migration; destructive DDL is denied by policy; rollback restores the pre-change checkpoint |");
        }

        md.AppendLine("| Stale cached routes after change | Route cache TTL bounds staleness; invalidation on disable |");

        var api = JsonSerializer.Serialize(new
        {
            capabilities = capabilities.Select(c => c.Id),
            endpoints = capabilities.SelectMany(c => c.ApiChanges),
            schema = capabilities.SelectMany(c => c.DataChanges),
        }, IndentedJson);

        return Task.FromResult(new AgentOutcome
        {
            Succeeded = true,
            Artifacts =
            [
                Produce.Markdown(ArtifactKeys.DesignDoc, "design", md.ToString()),
                Produce.Json(ArtifactKeys.ApiContract, "contract", api),
            ],
            Decisions = [.. choices.Select(c => new DecisionDraft(c.Decision, c.Rationale, c.Alternatives))],
        });
    }
}

public sealed class DeliveryPlannerAgent : IAgent
{
    public string Name => AgentNames.DeliveryPlanner;

    public Task<AgentOutcome> ExecuteAsync(AgentContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var capabilities = Specification.Current(context).ResolvedCapabilities();
        var tasks = capabilities.SelectMany(c => c.Tasks).DistinctBy(t => t.Id).ToList();
        var implementation = tasks.Select(t => t.Id).ToList();
        tasks.Add(new PlannedTask("T-verify", "Build and run unit + integration suites", "Verification", implementation, "Testing gate"));
        tasks.Add(new PlannedTask("T-security", "Dependency and static security review", "Verification", implementation, "No critical findings"));
        tasks.Add(new PlannedTask("T-docs", "Feature documentation and changelog", "Documentation", implementation, "Docs artifact present"));
        tasks.Add(new PlannedTask("T-release", "Readiness review, go/no-go and release package", "Release", ["T-verify", "T-security", "T-docs"], "Human go/no-go"));

        var md = new StringBuilder();
        md.AppendLine($"## Delivery plan v{context.Run.PlanVersion}").AppendLine();
        md.AppendLine("| Task | Layer | Depends on | Verification |").AppendLine("|---|---|---|---|");
        foreach (var task in tasks)
        {
            md.AppendLine($"| **{task.Id}** {task.Title} | {task.Layer} | {(task.DependsOn.Count == 0 ? "—" : string.Join(", ", task.DependsOn))} | {task.Verification} |");
        }

        md.AppendLine().AppendLine("```mermaid").AppendLine("graph TD");
        foreach (var task in tasks)
        {
            foreach (var dependency in task.DependsOn)
            {
                md.AppendLine($"  {dependency.Replace('-', '_')} --> {task.Id.Replace('-', '_')}");
            }
        }

        md.AppendLine("```");

        var parallel = tasks.Count(t => t.DependsOn.Count == 0);
        return Task.FromResult(new AgentOutcome
        {
            Succeeded = tasks.Count > 0,
            Error = tasks.Count > 0 ? null : "No tasks could be derived from the specification.",
            Artifacts = [Produce.Markdown(ArtifactKeys.TaskPlan, "plan", md.ToString())],
            Decisions =
            [
                new DecisionDraft(
                    $"Decomposed into {tasks.Count} tasks ({parallel} can start in parallel)",
                    "Inside-out sequencing (domain → application → infrastructure → presentation) keeps every step compilable and testable; verification, security and docs run in parallel before release"),
            ],
        });
    }
}
