using System.Text.Json;
using Orchestrator.Agents.Knowledge;
using Orchestrator.Core;
using Orchestrator.Core.Model;

namespace Orchestrator.ScenarioTests;

public sealed class ScenarioTests : IDisposable
{
    private readonly ScenarioFixture _fixture = new();

    [Fact]
    public async Task Brownfield_click_cap_is_analysed_governed_implemented_and_released()
    {
        var run = await _fixture.DriveAsync("brownfield-click-cap");

        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal(["click-cap"], Spec(run, ArtifactKeys.RequirementsSpec).Capabilities);
        Assert.Contains("ShortLinkService", run.FindArtifact(ArtifactKeys.ImpactAnalysis)!.Content, StringComparison.Ordinal);
        Assert.Contains("Schema change required", run.FindArtifact(ArtifactKeys.ImpactAnalysis)!.Content, StringComparison.Ordinal);

        var implementationApproval = run.Approvals.Single(a => a.NodeId == "implementation");
        Assert.Equal(RiskLevel.High, implementationApproval.Risk);
        Assert.Contains(implementationApproval.Evidence, e => e.Contains("Migrations", StringComparison.Ordinal));
        Assert.Equal(3, run.Approvals.Count(a => a.Status == ApprovalStatus.Approved));

        Assert.Contains("MaxClicks", run.FindArtifact(ArtifactKeys.ReleasePackage)!.Content, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_fixture.WorkspaceOf(run.Id), "docs", "features", "brownfield-maximum-click-limit.md")));
        Assert.True((await _fixture.Audit.VerifyAsync(default)).Intact);
    }

    [Fact]
    public async Task Ambiguous_request_is_clarified_by_a_human_before_design_and_replanned()
    {
        var run = await _fixture.DriveAsync("ambiguous-safer-links");

        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal(2, run.PlanVersion);
        Assert.Contains(run.Nodes, n => n.Id == "clarification" && n.RequiresHumanInput);
        Assert.Contains("clarification", run.Nodes.Single(n => n.Id == "impact-analysis").DependsOn);
        Assert.Equal(3, Spec(run, ArtifactKeys.RequirementsSpec).Ambiguities.Count);
        Assert.Equal(["destination-blocklist"], Spec(run, ArtifactKeys.ClarifiedRequirements).Capabilities);
        Assert.Contains(run.Decisions, d => d.Summary.Contains("Block malicious destination domains", StringComparison.Ordinal));
        Assert.Contains("BlockedDomains", run.FindArtifact(ArtifactKeys.ReleasePackage)!.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ambiguous_request_waits_for_a_human_when_nobody_answers()
    {
        var run = await _fixture.DriveAsync("ambiguous-safer-links", acceptDefaults: false);

        Assert.Equal(RunStatus.AwaitingInput, run.Status);
        Assert.Equal(NodeStatus.Pending, run.StateOf("design").Status);
        Assert.Null(run.FindArtifact(ArtifactKeys.DesignDoc));
    }

    [Fact]
    public async Task Greenfield_recovers_from_injected_faults_through_fallback_and_retry()
    {
        var run = await _fixture.DriveAsync("greenfield-url-shortener");
        var metrics = _fixture.Metrics.Snapshot();

        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal("requirements-rules", run.StateOf("requirements").ExecutedBy);
        Assert.Equal(2, run.StateOf("integration-tests").Attempts);
        Assert.True(metrics.Fallbacks >= 1);
        Assert.True(metrics.Retries >= 3);
        Assert.True(metrics.MeanTimeToRecoverSeconds > 0);
        Assert.False(run.Nodes.Exists(n => n.Id == "impact-analysis"));
        Assert.Contains(run.Approvals, a => a.NodeId == "implementation" && a.Risk == RiskLevel.High);
    }

    [Fact]
    public async Task Committed_secret_is_denied_and_the_workspace_is_rolled_back()
    {
        var run = await _fixture.DriveAsync("brownfield-policy-violation");

        Assert.Equal(RunStatus.RolledBack, run.Status);
        Assert.Equal(NodeStatus.RolledBack, run.StateOf("implementation").Status);
        Assert.Contains("security.secret-leak", run.StateOf("implementation").LastError, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_fixture.WorkspaceOf(run.Id), "src", "UrlShortener", "UrlShortener.Api", "appsettings.Staging.json")));
        Assert.DoesNotContain(
            await File.ReadAllTextAsync(Path.Combine(_fixture.WorkspaceOf(run.Id), "src", "UrlShortener", "UrlShortener.Domain", "ShortLink.cs")),
            "MaxClicks",
            StringComparison.Ordinal);
        Assert.Equal(NodeStatus.Pending, run.StateOf("verify").Status);
    }

    [Fact]
    public async Task Initiator_cannot_approve_their_own_run()
    {
        var run = await _fixture.DriveAsync("brownfield-click-cap", approver: "product-owner");

        Assert.Equal(RunStatus.AwaitingApproval, run.Status);
        Assert.All(run.Approvals, a => Assert.Equal(ApprovalStatus.Pending, a.Status));
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Dispose() => _fixture.DisposeAsync().AsTask().GetAwaiter().GetResult();

    private static Specification Spec(WorkflowRun run, string key) =>
        JsonSerializer.Deserialize<Specification>(run.FindArtifact(key)!.Content, Json)!;
}
