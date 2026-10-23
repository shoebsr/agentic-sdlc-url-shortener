using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orchestrator.Agents.Workflows;
using Orchestrator.Core.Abstractions;
using Orchestrator.Core.Engine;
using Orchestrator.Core.Model;
using Orchestrator.Host;

namespace Orchestrator.ScenarioTests;

public sealed class ScenarioFixture : IAsyncDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "sdlc-scenarios", Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider _services;

    public ScenarioFixture()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [OrchestratorHosting.DataRootKey] = _dataRoot,
                ["Orchestrator:Governance:Approvers:0:Name"] = "eng-lead",
                ["Orchestrator:Governance:Approvers:0:Role"] = "Engineering Lead",
                ["Orchestrator:Governance:Approvers:0:MaxRisk"] = "High",
                ["Orchestrator:Governance:Approvers:1:Name"] = "product-owner",
                ["Orchestrator:Governance:Approvers:1:Role"] = "Product Owner",
                ["Orchestrator:Governance:Approvers:1:MaxRisk"] = "Medium",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(l => l.SetMinimumLevel(LogLevel.Warning));
        services.AddOrchestrator(configuration);
        _services = services.BuildServiceProvider();
    }

    public RunCoordinator Coordinator => _services.GetRequiredService<RunCoordinator>();

    public IRunStore Store => _services.GetRequiredService<IRunStore>();

    public IAuditLog Audit => _services.GetRequiredService<IAuditLog>();

    public IReliabilityMetrics Metrics => _services.GetRequiredService<IReliabilityMetrics>();

    public string WorkspaceOf(string runId) => Path.Combine(_dataRoot, "workspaces", runId);

    public async Task<WorkflowRun> DriveAsync(string scenarioId, string approver = "eng-lead", bool acceptDefaults = true, bool preview = false)
    {
        var settings = new Dictionary<string, string> { ["preview"] = preview ? "true" : "false" };
        var run = _services.GetRequiredService<RunFactory>()
            .Create(new RunRequest(scenarioId, null, null, null, null, "simulated", null, settings));
        await Coordinator.StartAsync(run, CancellationToken.None);

        for (var step = 0; step < 20; step++)
        {
            await Coordinator.WaitForIdleAsync(run.Id, TimeSpan.FromMinutes(2));
            run = (await Store.LoadAsync(run.Id, CancellationToken.None))!;

            if (run.Status == RunStatus.AwaitingApproval)
            {
                foreach (var approval in run.Approvals.Where(a => a.Status == ApprovalStatus.Pending).ToList())
                {
                    await Coordinator.ApproveAsync(run.Id, approval.Id, approver, approve: true, "scenario test");
                }

                continue;
            }

            if (run.Status == RunStatus.AwaitingInput && acceptDefaults)
            {
                await Coordinator.ClarifyAsync(run.Id, new Dictionary<string, string>(), "requester");
                continue;
            }

            break;
        }

        return run;
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        try
        {
            foreach (var file in Directory.EnumerateFiles(_dataRoot, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_dataRoot, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
