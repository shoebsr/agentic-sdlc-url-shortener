using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Orchestrator.Core.Abstractions;
using Orchestrator.Core.Model;

namespace Orchestrator.Core.Engine;

public sealed partial class RunCoordinator(
    WorkflowEngine engine,
    IRunStore store,
    IKillSwitch killSwitch,
    IApprovalAuthority authority,
    RunInboxes inboxes,
    RunJournal journal,
    ILogger<RunCoordinator> logger) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _active = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _executions = new(StringComparer.Ordinal);

    public async Task<WorkflowRun> StartAsync(WorkflowRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        _ = run.Graph().TopologicalOrder();
        await store.SaveAsync(run, cancellationToken);
        await journal.RecordAsync(run, "run.created", $"Run created from {run.ScenarioId ?? "ad-hoc requirement"} by {run.InitiatedBy}",
            actor: run.InitiatedBy, cancellationToken: cancellationToken);
        Schedule(run.Id);
        return run;
    }

    public Task<CommandResult> ApproveAsync(string runId, string approvalId, string approver, bool approve, string? comment) =>
        SendAsync(runId, new RunCommand("approval", run => engine.DecideApprovalAsync(run, approvalId, approver, approve, comment, authority)));

    public Task<CommandResult> ClarifyAsync(string runId, IReadOnlyDictionary<string, string> answers, string actor) =>
        SendAsync(runId, new RunCommand("clarification", run => engine.SubmitClarificationsAsync(run, answers, actor)));

    public Task<CommandResult> AmendRequirementAsync(string runId, string requirement, string actor, string reason) =>
        SendAsync(runId, new RunCommand("amend", run => engine.AmendRequirementAsync(run, requirement, actor, reason)));

    public async Task<CommandResult> HaltAsync(string runId, string actor, string reason)
    {
        if (!_active.TryGetValue(runId, out var cancellation))
        {
            return CommandResult.Rejected("Run is not executing.");
        }

        LogHalt(logger, runId, actor, reason);
        await cancellation.CancelAsync();
        return CommandResult.Ok("Safe-stop requested; in-flight stages will be interrupted and state preserved.");
    }

    public async Task<CommandResult> ResumeAsync(string runId, CancellationToken cancellationToken)
    {
        var run = await store.LoadAsync(runId, cancellationToken);
        if (run is null)
        {
            return CommandResult.Rejected("Run not found.");
        }

        if (run.Status != RunStatus.Halted)
        {
            return CommandResult.Rejected($"Only halted runs can be resumed; run is {run.Status}.");
        }

        if (killSwitch.IsEngaged)
        {
            return CommandResult.Rejected("Global kill switch is engaged.");
        }

        Schedule(runId);
        return CommandResult.Ok("Run resumed.");
    }

    public async Task EngageKillSwitchAsync(string reason)
    {
        killSwitch.Engage(reason);
        foreach (var cancellation in _active.Values)
        {
            await cancellation.CancelAsync();
        }
    }

    public void ReleaseKillSwitch() => killSwitch.Release();

    public async Task WaitForIdleAsync(string runId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (_executions.TryGetValue(runId, out var execution) && DateTime.UtcNow < deadline)
        {
            await Task.WhenAny(execution, Task.Delay(TimeSpan.FromMilliseconds(50)));
        }
    }

    public bool IsExecuting(string runId) => _active.ContainsKey(runId);

    public async ValueTask DisposeAsync()
    {
        foreach (var cancellation in _active.Values)
        {
            await cancellation.CancelAsync();
        }

        await Task.WhenAll(_executions.Values);
        foreach (var gate in _locks.Values)
        {
            gate.Dispose();
        }
    }

    private async Task<CommandResult> SendAsync(string runId, RunCommand command)
    {
        if (await store.LoadAsync(runId, CancellationToken.None) is null)
        {
            return CommandResult.Rejected("Run not found.");
        }

        inboxes.For(runId).Post(command);
        Schedule(runId);
        return await command.Completion;
    }

    private void Schedule(string runId)
    {
        var execution = new Task<Task>(() => ExecuteExclusiveAsync(runId));
        if (_executions.TryAdd(runId, execution.Unwrap()))
        {
            execution.Start(TaskScheduler.Default);
        }
    }

    private async Task ExecuteExclusiveAsync(string runId)
    {
        var gate = _locks.GetOrAdd(runId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            do
            {
                var run = await store.LoadAsync(runId, CancellationToken.None);
                if (run is null)
                {
                    return;
                }

                using var cancellation = new CancellationTokenSource();
                _active[runId] = cancellation;
                try
                {
                    await engine.ExecuteAsync(run, inboxes.For(runId), cancellation.Token);
                }
                finally
                {
                    _active.TryRemove(runId, out _);
                }
            }
            while (inboxes.For(runId).HasPending);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogExecutionFault(logger, runId, ex);
        }
        finally
        {
            _executions.TryRemove(runId, out _);
            gate.Release();
            if (inboxes.For(runId).HasPending)
            {
                Schedule(runId);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Halt requested for run {RunId} by {Actor}: {Reason}")]
    private static partial void LogHalt(ILogger logger, string runId, string actor, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Execution of run {RunId} faulted")]
    private static partial void LogExecutionFault(ILogger logger, string runId, Exception exception);
}
