using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Orchestrator.Core.Abstractions;

namespace Orchestrator.Observability;

public sealed class HashChainedAuditLog : IAuditLog, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<AuditEvent> _events = [];
    private readonly string? _path;
    private string _lastHash = "GENESIS";
    private long _sequence;

    public HashChainedAuditLog(IOptions<ObservabilityOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Value.AuditFile))
        {
            return;
        }

        _path = Path.GetFullPath(options.Value.AuditFile);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        if (!File.Exists(_path))
        {
            return;
        }

        foreach (var line in File.ReadLines(_path).Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            var restored = JsonSerializer.Deserialize<AuditEvent>(line, Json)!;
            _events.Add(restored);
            _lastHash = restored.Hash;
            _sequence = restored.Sequence;
        }
    }

    public async Task<AuditEvent> AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var sequenced = auditEvent with { Sequence = ++_sequence, PreviousHash = _lastHash, Hash = string.Empty };
            var sealedEvent = sequenced with { Hash = ComputeHash(sequenced) };
            _events.Add(sealedEvent);
            _lastHash = sealedEvent.Hash;

            if (_path is not null)
            {
                await File.AppendAllTextAsync(_path, JsonSerializer.Serialize(sealedEvent, Json) + Environment.NewLine, cancellationToken);
            }

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
            return runId is null ? [.. _events] : [.. _events.Where(e => e.RunId == runId)];
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AuditVerification> VerifyAsync(CancellationToken cancellationToken)
    {
        var events = await ReadAsync(null, cancellationToken);
        var previous = "GENESIS";
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
}
