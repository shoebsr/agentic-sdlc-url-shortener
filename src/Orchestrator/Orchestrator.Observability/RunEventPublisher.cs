using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Orchestrator.Core.Abstractions;

namespace Orchestrator.Observability;

public sealed class RunEventPublisher : IRunEventPublisher
{
    private const int SubscriberBuffer = 1024;

    private readonly ConcurrentDictionary<Guid, Subscription> _subscriptions = new();

    public void Publish(AuditEvent auditEvent)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        foreach (var subscription in _subscriptions.Values)
        {
            if (subscription.RunId is null || subscription.RunId == auditEvent.RunId)
            {
                subscription.Channel.Writer.TryWrite(auditEvent);
            }
        }
    }

    public async IAsyncEnumerable<AuditEvent> SubscribeAsync(string? runId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var subscription = new Subscription(runId, Channel.CreateBounded<AuditEvent>(new BoundedChannelOptions(SubscriberBuffer)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        }));
        _subscriptions[id] = subscription;
        try
        {
            await foreach (var auditEvent in subscription.Channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return auditEvent;
            }
        }
        finally
        {
            _subscriptions.TryRemove(id, out _);
        }
    }

    private sealed record Subscription(string? RunId, Channel<AuditEvent> Channel);
}
