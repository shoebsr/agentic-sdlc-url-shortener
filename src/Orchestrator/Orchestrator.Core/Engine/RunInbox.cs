using System.Collections.Concurrent;
using System.Threading.Channels;
using Orchestrator.Core.Model;

namespace Orchestrator.Core.Engine;

public sealed record CommandResult(bool Accepted, string Message)
{
    public static CommandResult Ok(string message) => new(true, message);

    public static CommandResult Rejected(string message) => new(false, message);
}

public sealed class RunCommand(string name, Func<WorkflowRun, Task<CommandResult>> apply)
{
    private readonly TaskCompletionSource<CommandResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Name { get; } = name;

    public Task<CommandResult> Completion => _completion.Task;

    public async Task ApplyAsync(WorkflowRun run)
    {
        try
        {
            _completion.TrySetResult(await apply(run));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _completion.TrySetException(ex);
        }
    }

    public void Abandon(string reason) => _completion.TrySetResult(CommandResult.Rejected(reason));
}

public sealed class RunInbox
{
    private readonly Channel<RunCommand> _commands = Channel.CreateUnbounded<RunCommand>();

    public bool HasPending => _commands.Reader.Count > 0;

    public void Post(RunCommand command) => _commands.Writer.TryWrite(command);

    public Task WaitAsync(CancellationToken cancellationToken) => _commands.Reader.WaitToReadAsync(cancellationToken).AsTask();

    public async Task<bool> DrainAsync(WorkflowRun run)
    {
        var applied = false;
        while (_commands.Reader.TryRead(out var command))
        {
            await command.ApplyAsync(run);
            applied = true;
        }

        return applied;
    }
}

public sealed class RunInboxes
{
    private readonly ConcurrentDictionary<string, RunInbox> _inboxes = new(StringComparer.Ordinal);

    public RunInbox For(string runId) => _inboxes.GetOrAdd(runId, _ => new RunInbox());
}
