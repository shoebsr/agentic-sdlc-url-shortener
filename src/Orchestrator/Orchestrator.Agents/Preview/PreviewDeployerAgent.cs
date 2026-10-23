using System.Text;
using Orchestrator.Core;
using Orchestrator.Core.Abstractions;

namespace Orchestrator.Agents.Preview;

public sealed class PreviewDeployerAgent(PreviewEnvironments previews) : IAgent
{
    public const string PreviewSetting = "preview";

    public string Name => AgentNames.PreviewDeployer;

    public async Task<AgentOutcome> ExecuteAsync(AgentContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!previews.Options.Enabled || string.Equals(context.Setting(PreviewSetting), "false", StringComparison.OrdinalIgnoreCase))
        {
            return new AgentOutcome
            {
                Succeeded = true,
                Artifacts = [Produce.Markdown(ArtifactKeys.ReleasePreview, "preview", "## Preview environment\n\nPreview deployment is disabled for this run.\n")],
                Decisions = [new DecisionDraft("Skipped preview deployment", "Disabled by configuration for this run")],
            };
        }

        var preview = await previews.StartAsync(context.Run.Id, context.Workspace.Root, cancellationToken);
        var md = new StringBuilder("## Preview environment\n\n");
        md.AppendLine($"The released build is running from the run's sandbox.").AppendLine();
        md.AppendLine("| | |").AppendLine("|---|---|");
        md.AppendLine($"| Application | {preview.AppUrl} |");
        md.AppendLine($"| API documentation | {preview.SwaggerUrl} |");
        md.AppendLine($"| Base URL (short links) | {preview.BaseUrl} |");
        md.AppendLine($"| Health | {preview.HealthUrl} |");
        md.AppendLine($"| Database | SQLite file inside the sandbox (isolated from every other environment) |");
        md.AppendLine($"| Started | {preview.StartedAt:u} |");
        md.AppendLine().AppendLine("The preview stops when it is stopped from the dashboard, when the run is rolled back, or when the orchestrator shuts down.");

        return new AgentOutcome
        {
            Succeeded = true,
            Artifacts = [Produce.Markdown(ArtifactKeys.ReleasePreview, "preview", md.ToString())],
            Decisions = [new DecisionDraft($"Deployed preview at {preview.BaseUrl}", "Released build smoke-tested by health check in an isolated environment before hand-over")],
            Metrics = new Dictionary<string, string> { ["previewUrl"] = preview.BaseUrl.AbsoluteUri, ["port"] = preview.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) },
        };
    }

    public Task CompensateAsync(AgentContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return previews.StopAsync(context.Run.Id);
    }
}
