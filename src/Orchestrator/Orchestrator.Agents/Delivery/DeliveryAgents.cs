using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Orchestrator.Agents.Knowledge;
using Orchestrator.Agents.Tools;
using Orchestrator.Core;
using Orchestrator.Core.Abstractions;
using Orchestrator.Core.Model;

namespace Orchestrator.Agents.Delivery;

public sealed class TechnicalWriterAgent : IAgent
{
    public string Name => AgentNames.TechnicalWriter;

    public async Task<AgentOutcome> ExecuteAsync(AgentContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var spec = Specification.Current(context);
        var capabilities = spec.ResolvedCapabilities();
        var slug = Regex.Replace(context.Run.Title.ToLowerInvariant(), "[^a-z0-9]+", "-", RegexOptions.None, TimeSpan.FromSeconds(1)).Trim('-');

        var doc = new StringBuilder();
        doc.AppendLine($"# {context.Run.Title}").AppendLine();
        doc.AppendLine($"> {spec.Summary}").AppendLine();
        doc.AppendLine("## Capabilities");
        foreach (var capability in capabilities)
        {
            doc.AppendLine($"### {capability.Title}");
            foreach (var criterion in capability.AcceptanceCriteria)
            {
                doc.AppendLine($"- {criterion}");
            }

            doc.AppendLine();
        }

        var api = capabilities.SelectMany(c => c.ApiChanges).ToList();
        if (api.Count > 0)
        {
            doc.AppendLine("## API reference changes");
            foreach (var change in api)
            {
                doc.AppendLine($"- {change}");
            }

            doc.AppendLine();
        }

        if (spec.Resolutions.Count > 0)
        {
            doc.AppendLine("## Clarified decisions");
            foreach (var (question, answer) in spec.Resolutions)
            {
                doc.AppendLine($"- **{question}** → {answer}");
            }

            doc.AppendLine();
        }

        doc.AppendLine("## Operational notes");
        doc.AppendLine("- Configuration is environment-driven; no secrets live in the repository.");
        doc.AppendLine("- Roll back by redeploying the previous release tag; schema changes are additive only.");

        var changelogEntry = $"## {context.Clock.GetUtcNow():yyyy-MM-dd} — {context.Run.Title}\n\n" +
            string.Join('\n', capabilities.Select(c => $"- {c.Title}")) + "\n\n";
        var existing = context.Workspace.Exists("CHANGELOG.md") ? await context.Workspace.ReadAsync("CHANGELOG.md", cancellationToken) : "# Changelog\n\n";
        await context.Workspace.WriteAsync($"docs/features/{slug}.md", doc.ToString(), cancellationToken);
        await context.Workspace.WriteAsync("CHANGELOG.md", existing.Replace("# Changelog\n\n", "# Changelog\n\n" + changelogEntry, StringComparison.Ordinal), cancellationToken);

        return new AgentOutcome
        {
            Succeeded = true,
            Artifacts = [Produce.Markdown(ArtifactKeys.Documentation, "documentation", doc.ToString())],
            Decisions = [new DecisionDraft($"Documented {capabilities.Count} capability(ies) in docs/features/{slug}.md and CHANGELOG.md", "Docs ship in the same change as the code they describe")],
        };
    }
}

public sealed class ReleaseManagerAgent : IAgent
{
    public string Name => AgentNames.ReleaseManager;

    public Task<AgentOutcome> ExecuteAsync(AgentContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var checks = new List<(string Check, bool Passed, string Evidence)>
        {
            Check(context, "Build verified", ArtifactKeys.BuildReport),
            Check(context, "Unit tests green", ArtifactKeys.UnitTestReport),
            Check(context, "Integration tests green", ArtifactKeys.IntegrationTestReport),
            Check(context, "No critical security findings", ArtifactKeys.SecurityReport),
            (Check: "Documentation updated", Passed: context.Input(ArtifactKeys.Documentation) is not null, Evidence: ArtifactKeys.Documentation),
            (Check: "Design reviewed", Passed: context.Input(ArtifactKeys.DesignDoc) is not null, Evidence: ArtifactKeys.DesignDoc),
        };

        var governed = context.Run.Approvals.Where(a => a.Status == ApprovalStatus.Approved).ToList();
        checks.Add(("Governed changes approved", context.Run.Approvals.All(a => a.Status != ApprovalStatus.Pending || a.NodeId == context.Node.Id),
            $"{governed.Count} approval(s): {string.Join(", ", governed.Select(a => $"{a.Id} by {a.DecidedBy}"))}"));

        var blocking = checks.Count(c => !c.Passed);
        var md = new StringBuilder("## Release readiness\n\n| Check | Status | Evidence |\n|---|---|---|\n");
        foreach (var (check, passed, evidence) in checks)
        {
            md.AppendLine($"| {check} | {(passed ? "✅" : "❌")} | {evidence} |");
        }

        md.AppendLine().AppendLine("### Rollback plan");
        md.AppendLine("1. Halt rollout via the orchestrator kill switch or deployment pause.");
        md.AppendLine("2. Redeploy the previous release tag; database migrations in this release are additive and need no down-migration.");
        md.AppendLine("3. Invalidate route cache entries (TTL-bounded) and verify `/health/ready` on all services.");
        md.AppendLine().AppendLine($"**Blocking items:** {blocking}");

        var spec = Specification.Current(context);
        var notes = new StringBuilder($"# Release notes — {context.Run.Title}\n\n");
        foreach (var capability in spec.ResolvedCapabilities())
        {
            notes.AppendLine($"- **{capability.Title}**: {(capability.AcceptanceCriteria.Count > 0 ? capability.AcceptanceCriteria[0] : capability.Title)}");
        }

        notes.AppendLine().AppendLine($"Plan version v{context.Run.PlanVersion}; {context.Run.Decisions.Count} recorded decisions; {governed.Count} human approvals.");

        return Task.FromResult(new AgentOutcome
        {
            Succeeded = true,
            Artifacts =
            [
                Produce.Markdown(ArtifactKeys.ReleaseChecklist, "checklist", md.ToString()),
                Produce.Markdown(ArtifactKeys.ReleaseNotes, "release-notes", notes.ToString()),
            ],
            Decisions = [new DecisionDraft(blocking == 0 ? "Recommend GO for release" : $"Recommend NO-GO: {blocking} blocking item(s)", "Derived from verification evidence and approval history")],
            Metrics = new Dictionary<string, string> { ["blocking"] = blocking.ToString(CultureInfo.InvariantCulture) },
        });
    }

    private static (string Check, bool Passed, string Evidence) Check(AgentContext context, string label, string key)
    {
        var artifact = context.Input(key);
        var passed = artifact is not null && artifact.Content.Contains("**Result:** PASSED", StringComparison.Ordinal);
        return (label, passed, artifact is null ? $"{key} missing" : $"{key} v{artifact.Version} (`{artifact.Hash}`)");
    }
}

public sealed class ReleasePublisherAgent : IAgent
{
    public string Name => AgentNames.ReleasePublisher;

    public async Task<AgentOutcome> ExecuteAsync(AgentContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Workspace is not GitWorkspace workspace)
        {
            return AgentOutcome.Failure("Release requires a git-backed workspace.");
        }

        var baseline = await workspace.BaselineCommitAsync(cancellationToken);
        var commit = await workspace.CheckpointAsync($"release: {context.Run.Title} (plan v{context.Run.PlanVersion})", cancellationToken);
        var tag = $"release-{context.Run.Id}";
        await workspace.TagAsync(tag, cancellationToken);
        var patch = await workspace.PatchAsync(baseline, cancellationToken);
        const int limit = 400_000;

        var md = new StringBuilder("## Release package\n\n");
        md.AppendLine($"- Tag: `{tag}` → commit `{commit[..12]}`");
        md.AppendLine($"- Workspace: `{workspace.Root}`");
        md.AppendLine($"- Patch against baseline: {patch.Length:N0} characters (reviewable, `git apply`-ready)");
        md.AppendLine().AppendLine("```diff").AppendLine(patch.Length <= limit ? patch : patch[..limit] + "\n… truncated").AppendLine("```");

        return new AgentOutcome
        {
            Succeeded = true,
            Artifacts = [Produce.Markdown(ArtifactKeys.ReleasePackage, "release", md.ToString())],
            Decisions = [new DecisionDraft($"Published release candidate {tag}", "Approved go/no-go; package is a tagged commit plus a patch, so promotion to the main repository stays a human-controlled merge")],
        };
    }

    public async Task CompensateAsync(AgentContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Workspace is GitWorkspace workspace)
        {
            await workspace.DeleteTagAsync($"release-{context.Run.Id}", cancellationToken);
        }
    }
}
