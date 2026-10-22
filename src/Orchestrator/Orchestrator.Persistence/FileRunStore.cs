using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Orchestrator.Core.Abstractions;
using Orchestrator.Core.Model;

namespace Orchestrator.Persistence;

public sealed class FileRunStore : IRunStore
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _directory;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public FileRunStore(IOptions<PersistenceOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _directory = Path.GetFullPath(options.Value.RunsDirectory);
        Directory.CreateDirectory(_directory);
    }

    public async Task SaveAsync(WorkflowRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        var path = PathFor(run.Id);
        var gate = _locks.GetOrAdd(run.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var temp = path + ".tmp";
            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, run, Json, cancellationToken);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<WorkflowRun?> LoadAsync(string runId, CancellationToken cancellationToken)
    {
        if (!IsSafeId(runId))
        {
            return null;
        }

        var path = PathFor(runId);
        if (!File.Exists(path))
        {
            return null;
        }

        var gate = _locks.GetOrAdd(runId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<WorkflowRun>(stream, Json, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<WorkflowRun>> ListAsync(CancellationToken cancellationToken)
    {
        var runs = new List<WorkflowRun>();
        foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            var run = await LoadAsync(Path.GetFileNameWithoutExtension(file), cancellationToken);
            if (run is not null)
            {
                runs.Add(run);
            }
        }

        return [.. runs.OrderByDescending(r => r.CreatedAt)];
    }

    private string PathFor(string runId) => IsSafeId(runId)
        ? Path.Combine(_directory, runId + ".json")
        : throw new ArgumentException("Invalid run id.", nameof(runId));

    private static bool IsSafeId(string runId) =>
        !string.IsNullOrWhiteSpace(runId) && runId.Length <= 64 && runId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}

public sealed class PersistenceOptions
{
    public const string SectionName = "Orchestrator:Persistence";

    public string RunsDirectory { get; set; } = "data/runs";
}
