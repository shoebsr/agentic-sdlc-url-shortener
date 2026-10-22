using Orchestrator.Core.Model;

namespace Orchestrator.Core.Abstractions;

public interface IRunStore
{
    Task SaveAsync(WorkflowRun run, CancellationToken cancellationToken);

    Task<WorkflowRun?> LoadAsync(string runId, CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkflowRun>> ListAsync(CancellationToken cancellationToken);
}

public interface IWorkspace
{
    string Root { get; }

    bool Exists(string relativePath);

    Task<string> ReadAsync(string relativePath, CancellationToken cancellationToken);

    Task WriteAsync(string relativePath, string content, CancellationToken cancellationToken);

    IEnumerable<string> Files(string pattern = "*");

    Task<string> CheckpointAsync(string label, CancellationToken cancellationToken);

    Task RestoreAsync(string checkpoint, CancellationToken cancellationToken);

    Task<ChangeSet> DiffAsync(string fromCheckpoint, CancellationToken cancellationToken);
}

public interface IWorkspaceProvider
{
    IWorkspace For(WorkflowRun run);
}

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, TimeSpan Duration)
{
    public bool Succeeded => ExitCode == 0;
}

public interface IToolbox
{
    Task<ProcessResult> RunAsync(string tool, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed record LlmRequest(string System, string Prompt, int MaxTokens = 2048, double Temperature = 0);

public sealed record LlmResponse(string Text, string Model, int InputTokens, int OutputTokens);

public interface ILlmClient
{
    bool IsEnabled { get; }

    string Provider { get; }

    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken);
}
