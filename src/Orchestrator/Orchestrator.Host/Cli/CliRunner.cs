using Orchestrator.Agents.Workflows;
using Orchestrator.Core.Abstractions;
using Orchestrator.Core.Engine;
using Orchestrator.Core.Model;

namespace Orchestrator.Host.Cli;

public static class CliRunner
{
    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(args);

        var options = CliOptions.Parse(args);
        if (options.Scenario is null)
        {
            Console.Error.WriteLine("usage: Orchestrator.Host run <scenario-id> [--approve-as <approver>] [--accept-defaults] [--simulate] [--faults <spec>]");
            return 2;
        }

        var factory = services.GetRequiredService<RunFactory>();
        var coordinator = services.GetRequiredService<RunCoordinator>();
        var store = services.GetRequiredService<IRunStore>();
        var events = services.GetRequiredService<IRunEventPublisher>();
        var metrics = services.GetRequiredService<IReliabilityMetrics>();

        var run = factory.Create(new RunRequest(options.Scenario, null, null, null, null,
            options.Simulate ? "simulated" : null, options.Faults, null));

        using var stop = new CancellationTokenSource();
        var printer = PrintEventsAsync(events, run.Id, stop.Token);
        await coordinator.StartAsync(run, CancellationToken.None);

        while (true)
        {
            await coordinator.WaitForIdleAsync(run.Id, TimeSpan.FromMinutes(30));
            run = (await store.LoadAsync(run.Id, CancellationToken.None))!;

            if (run.Status == RunStatus.AwaitingApproval && options.ApproveAs is not null)
            {
                foreach (var approval in run.Approvals.Where(a => a.Status == ApprovalStatus.Pending).ToList())
                {
                    var result = await coordinator.ApproveAsync(run.Id, approval.Id, options.ApproveAs, approve: true, "Approved via CLI delegation");
                    Console.WriteLine($"  ⮑ {approval.Id} [{approval.Risk}] {approval.NodeId}: {result.Message}");
                    if (!result.Accepted)
                    {
                        return await FinishAsync(stop, printer, run, metrics, 3);
                    }
                }

                continue;
            }

            if (run.Status == RunStatus.AwaitingInput && options.AcceptDefaults)
            {
                Console.WriteLine("  ⮑ accepting suggested defaults for open questions:");
                foreach (var question in run.OpenQuestions)
                {
                    Console.WriteLine($"     {question.Id}: {question.Question} → {question.SuggestedDefault}");
                }

                await coordinator.ClarifyAsync(run.Id, new Dictionary<string, string>(), "cli");
                continue;
            }

            break;
        }

        return await FinishAsync(stop, printer, run, metrics, run.Status == RunStatus.Succeeded ? 0 : 1);
    }

    private static async Task<int> FinishAsync(CancellationTokenSource stop, Task printer, WorkflowRun run, IReliabilityMetrics metrics, int exitCode)
    {
        await Task.Delay(200);
        await stop.CancelAsync();
        await printer;
        var snapshot = metrics.Snapshot();
        Console.WriteLine();
        Console.WriteLine($"Run {run.Id} finished: {run.Status} (plan v{run.PlanVersion}, {run.Decisions.Count} decisions, {run.Artifacts.Count} artifacts)");
        Console.WriteLine($"Retries {snapshot.Retries} · fallbacks {snapshot.Fallbacks} · rollbacks {snapshot.Rollbacks} · MTTR {snapshot.MeanTimeToRecoverSeconds:F2}s · E2E {snapshot.MeanEndToEndSeconds:F1}s");
        return exitCode;
    }

    private static async Task PrintEventsAsync(IRunEventPublisher events, string runId, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var e in events.SubscribeAsync(runId, cancellationToken))
            {
                Console.WriteLine($"{e.At:HH:mm:ss.fff}  {e.Type,-22} {(e.NodeId ?? "-"),-18} {e.Message}");
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed record CliOptions(string? Scenario, string? ApproveAs, bool AcceptDefaults, bool Simulate, string? Faults)
    {
        public static CliOptions Parse(string[] args)
        {
            string? Value(string flag) => args.SkipWhile(a => a != flag).Skip(1).FirstOrDefault();
            return new CliOptions(
                args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : null,
                Value("--approve-as"),
                args.Contains("--accept-defaults"),
                args.Contains("--simulate"),
                Value("--faults"));
        }
    }
}
