using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Orchestrator.Core.Abstractions;

namespace Orchestrator.Observability;

public sealed class HashChainedAuditLog : IAuditLog, IDisposable
{
    private const string Genesis = "GENESIS";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<AuditEvent> _memory = [];
    private readonly string? _path;

    public HashChainedAuditLog(IOptions<ObservabilityOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!string.IsNullOrWhiteSpace(options.Value.AuditFile))
        {
            _path = Path.GetFullPath(options.Value.AuditFile);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        }
    }

    public async Task<AuditEvent> AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_path is null)
            {
                var previous = _memory.Count == 0 ? null : _memory[^1];
                var sealedInMemory = Seal(auditEvent, previous);
                _memory.Add(sealedInMemory);
                return sealedInMemory;
            }

            await using var stream = await OpenExclusiveAsync(_path, cancellationToken);
            var last = await ReadLastAsync(stream, cancellationToken);
            var sealedEvent = Seal(auditEvent, last);
            stream.Seek(0, SeekOrigin.End);
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(sealedEvent, Json) + "\n");
            await stream.WriteAsync(bytes, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            return sealedEvent;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<AuditEvent>> ReadAsync(string? runId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            IReadOnlyList<AuditEvent> events = _path is null ? [.. _memory] : await ReadAllAsync(_path, cancellationToken);
            return runId is null ? events : [.. events.Where(e => e.RunId == runId)];
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AuditVerification> VerifyAsync(CancellationToken cancellationToken)
    {
        var events = await ReadAsync(null, cancellationToken);
        var previous = Genesis;
        foreach (var auditEvent in events)
        {
            if (auditEvent.PreviousHash != previous || auditEvent.Hash != ComputeHash(auditEvent with { Hash = string.Empty }))
            {
                return new AuditVerification(false, events.Count, auditEvent.Sequence);
            }

            previous = auditEvent.Hash;
        }

        return new AuditVerification(true, events.Count, null);
    }

    public void Dispose() => _gate.Dispose();

    internal static string ComputeHash(AuditEvent auditEvent)
    {
        var canonical = string.Join('|',
            auditEvent.Sequence.ToString(CultureInfo.InvariantCulture),
            auditEvent.RunId,
            auditEvent.Type,
            auditEvent.NodeId ?? string.Empty,
            auditEvent.Actor,
            auditEvent.Message,
            string.Join(',', auditEvent.Data.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}")),
            auditEvent.At.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            auditEvent.PreviousHash);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static AuditEvent Seal(AuditEvent auditEvent, AuditEvent? previous)
    {
        var sequenced = auditEvent with
        {
            Sequence = (previous?.Sequence ?? 0) + 1,
            PreviousHash = previous?.Hash ?? Genesis,
            Hash = string.Empty,
        };
        return sequenced with { Hash = ComputeHash(sequenced) };
    }

    private static async Task<FileStream> OpenExclusiveAsync(string path, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + LockTimeout;
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.Asynchronous);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(Random.Shared.Next(5, 25), cancellationToken);
            }
        }
    }

    private static async Task<AuditEvent?> ReadLastAsync(FileStream stream, CancellationToken cancellationToken)
    {
        if (stream.Length == 0)
        {
            return null;
        }

        var window = (int)Math.Min(stream.Length, 64 * 1024);
        var buffer = new byte[window];
        stream.Seek(-window, SeekOrigin.End);
        await stream.ReadExactlyAsync(buffer, cancellationToken);
        var lines = Encoding.UTF8.GetString(buffer).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length == 0 ? null : JsonSerializer.Deserialize<AuditEvent>(lines[^1], Json);
    }

    private static async Task<IReadOnlyList<AuditEvent>> ReadAllAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.Asynchronous);
        using var reader = new StreamReader(stream);
        var events = new List<AuditEvent>();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                events.Add(JsonSerializer.Deserialize<AuditEvent>(line, Json)!);
            }
        }

        return events;
    }
}
