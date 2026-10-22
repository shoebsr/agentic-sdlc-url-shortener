using Orchestrator.Core.Abstractions;
using Orchestrator.Core.Engine;
using Orchestrator.Core.Model;

namespace Orchestrator.Host;

public sealed partial class RunRecoveryService(IRunStore store, RunJournal journal, ILogger<RunRecoveryService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var run in await store.ListAsync(cancellationToken))
        {
            if (run.Status is not (RunStatus.Running or RunStatus.Pending))
            {
                continue;
            }

            foreach (var state in run.NodeStates.Values.Where(s => s.Status == NodeStatus.Running))
            {
                state.Status = NodeStatus.Pending;
            }

            run.Status = RunStatus.Halted;
            run.HaltReason = "Orchestrator restarted while the run was executing; resume to continue from the last persisted state";
            await store.SaveAsync(run, cancellationToken);
            await journal.RecordAsync(run, "run.recovered", run.HaltReason, cancellationToken: cancellationToken);
            LogRecovered(logger, run.Id);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Run {RunId} was interrupted by a restart and is now halted pending resume")]
    private static partial void LogRecovered(ILogger logger, string runId);
}
