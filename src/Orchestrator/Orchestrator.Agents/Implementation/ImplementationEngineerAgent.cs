using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using Orchestrator.Agents.Knowledge;
using Orchestrator.Agents.Tools;
using Orchestrator.Core;
using Orchestrator.Core.Abstractions;

namespace Orchestrator.Agents.Implementation;

public sealed class ImplementationEngineerAgent(IOptions<WorkspaceOptions> options) : IAgent
{
    public const string CheckpointKey = "implementation.checkpoint";
    public const string TemplateRecipe = "template";
    public const string PatchSettingPrefix = "patch.";
    public const string InjectSecretSetting = "injectSecret";

    private const string LlmSystemPrompt =
        "You are a senior .NET engineer. Produce a single unified diff (git apply compatible, paths relative to the repository root) " +
        "that implements the requested capability following Clean Architecture, with unit tests. Output only the diff.";

    public string Name => AgentNames.ImplementationEngineer;

    public async Task<AgentOutcome> ExecuteAsync(AgentContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Workspace is not GitWorkspace workspace)
        {
            return AgentOutcome.Failure("Implementation requires a git-backed workspace.");
        }

        var spec = Specification.Current(context);
        var capabilities = spec.ResolvedCapabilities();
        var checkpoint = await workspace.CheckpointAsync($"pre-implementation (plan v{context.Run.PlanVersion})", cancellationToken);
        var applied = new List<string>();
        var source = options.Value.ResolveSourceRoot();

        if (string.Equals(context.Setting(RunSettings.Recipe), TemplateRecipe, StringComparison.OrdinalIgnoreCase))
        {
            var copied = await MaterializeTemplateAsync(workspace, source, cancellationToken);
            applied.Add($"template:url-shortener ({copied} files)");
        }
        else
        {
            foreach (var capability in capabilities)
            {
                var patch = context.Setting(PatchSettingPrefix + capability.Id);
                if (patch is null)
                {
                    continue;
                }

                var path = Path.GetFullPath(Path.Combine(source, patch));
                if (!path.StartsWith(source, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                {
                    return AgentOutcome.Failure($"Change recipe '{patch}' for {capability.Id} was not found inside the repository.");
                }

                await workspace.ApplyPatchAsync(path, cancellationToken);
                applied.Add($"patch:{capability.Id} ({patch})");
            }
        }

        if (applied.Count == 0 && context.Llm.IsEnabled)
        {
            applied.Add(await GenerateWithLlmAsync(context, workspace, capabilities, cancellationToken));
        }

        if (applied.Count == 0)
        {
            await workspace.RestoreAsync(checkpoint, cancellationToken);
            return AgentOutcome.Failure(
                $"No implementation recipe for [{string.Join(", ", capabilities.Select(c => c.Id))}] and no LLM provider is configured.");
        }

        if (string.Equals(context.Setting(InjectSecretSetting), "true", StringComparison.OrdinalIgnoreCase))
        {
            await workspace.WriteAsync("src/UrlShortener/UrlShortener.Api/appsettings.Staging.json",
                "{\n  \"Storage\": { \"AccessKeyId\": \"AKIAIOSFODNN7EXAMPLE\" }\n}\n", cancellationToken);
            applied.Add("config:staging-settings");
        }

        var changes = await workspace.DiffAsync(checkpoint, cancellationToken);
        var md = new StringBuilder();
        md.AppendLine("## Implementation change set").AppendLine();
        md.AppendLine($"Applied: {string.Join(", ", applied)}  ");
        md.AppendLine($"Checkpoint: `{checkpoint[..12]}` · **{changes.Files.Count}** files · **+{changes.AddedLines} / -{changes.RemovedLines}** lines").AppendLine();
        md.AppendLine("| Change | File | + | - |").AppendLine("|---|---|---|---|");
        foreach (var file in changes.Files.OrderBy(f => f.Path, StringComparer.Ordinal).Take(80))
        {
            md.AppendLine($"| {file.Kind} | `{file.Path}` | {file.AddedLines} | {file.RemovedLines} |");
        }

        if (changes.Files.Count > 80)
        {
            md.AppendLine($"| … | {changes.Files.Count - 80} more files | | |");
        }

        return new AgentOutcome
        {
            Succeeded = true,
            Changes = changes,
            Artifacts =
            [
                Produce.Markdown(ArtifactKeys.ChangeSummary, "change-set", md.ToString()),
                Produce.Markdown(CheckpointKey, "checkpoint", checkpoint),
            ],
            Decisions =
            [
                new DecisionDraft(
                    $"Implemented via {string.Join(", ", applied)}",
                    "Changes are applied in an isolated git workspace from a recorded checkpoint so they can be reviewed as a diff and rolled back atomically",
                    ["Edit the live repository directly", "Generate code without a reviewable diff"]),
            ],
            Metrics = new Dictionary<string, string>
            {
                ["files"] = changes.Files.Count.ToString(CultureInfo.InvariantCulture),
                ["added"] = changes.AddedLines.ToString(CultureInfo.InvariantCulture),
                ["removed"] = changes.RemovedLines.ToString(CultureInfo.InvariantCulture),
            },
        };
    }

    public async Task CompensateAsync(AgentContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Workspace is GitWorkspace workspace && context.Input(CheckpointKey) is { } checkpoint)
        {
            await workspace.RestoreAsync(checkpoint.Content.Trim(), cancellationToken);
        }
    }

    private async Task<int> MaterializeTemplateAsync(GitWorkspace workspace, string source, CancellationToken cancellationToken)
    {
        var count = 0;
        foreach (var entry in options.Value.RepositoryBaseline.Where(e => e.StartsWith("src/", StringComparison.Ordinal) || e.StartsWith("tests/", StringComparison.Ordinal)))
        {
            var root = Path.Combine(source, entry);
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, file).Replace('\\', '/');
                if (relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/obj/", StringComparison.Ordinal))
                {
                    continue;
                }

                await workspace.WriteAsync(relative, await File.ReadAllTextAsync(file, cancellationToken), cancellationToken);
                count++;
            }
        }

        return count;
    }

    private static async Task<string> GenerateWithLlmAsync(
        AgentContext context, GitWorkspace workspace, IReadOnlyList<Capability> capabilities, CancellationToken cancellationToken)
    {
        var prompt = new StringBuilder()
            .AppendLine($"Requirement: {context.Requirement}")
            .AppendLine($"Capabilities: {string.Join("; ", capabilities.Select(c => $"{c.Title}: {string.Join(" / ", c.AcceptanceCriteria)}"))}")
            .AppendLine("Design:")
            .AppendLine(context.Input(ArtifactKeys.DesignDoc)?.Content)
            .AppendLine("Impact analysis:")
            .AppendLine(context.Input(ArtifactKeys.ImpactAnalysis)?.Content)
            .ToString();

        var response = await context.Llm.CompleteAsync(new LlmRequest(LlmSystemPrompt, prompt, 16_000), cancellationToken);
        var diff = response.Text.Trim().Trim('`');
        await workspace.WriteAsync(".sdlc/generated.patch", diff + "\n", cancellationToken);
        await workspace.ApplyPatchAsync(Path.Combine(workspace.Root, ".sdlc", "generated.patch"), cancellationToken);
        File.Delete(Path.Combine(workspace.Root, ".sdlc", "generated.patch"));
        return $"llm:{response.Model}";
    }
}
