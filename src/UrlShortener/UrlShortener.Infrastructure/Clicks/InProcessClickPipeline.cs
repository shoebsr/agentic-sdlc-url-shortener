using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using UrlShortener.Application.Abstractions;
using UrlShortener.Domain;

namespace UrlShortener.Infrastructure.Clicks;

internal sealed class InProcessClickQueue(IOptions<AnalyticsOptions> options) : IClickTracker
{
    private readonly Channel<ClickEvent> _channel = Channel.CreateBounded<ClickEvent>(
        new BoundedChannelOptions(options.Value.BufferCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });

    public ChannelReader<ClickEvent> Reader => _channel.Reader;

    public ValueTask<bool> TryTrackAsync(ClickEvent click, CancellationToken cancellationToken) =>
        ValueTask.FromResult(_channel.Writer.TryWrite(click));

    public void Complete() => _channel.Writer.TryComplete();
}

internal sealed class InProcessClickWriter(
    InProcessClickQueue queue, ClickBatchPersister persister, IOptions<AnalyticsOptions> options) : BackgroundService
{
    private readonly AnalyticsOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<ClickEvent>(_options.BatchSize);
        var flushInterval = TimeSpan.FromMilliseconds(_options.FlushIntervalMilliseconds);

        try
        {
            while (await queue.Reader.WaitToReadAsync(stoppingToken))
            {
                using var window = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                window.CancelAfter(flushInterval);
                await FillBatchAsync(batch, window.Token);
                await FlushAsync(batch, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        while (queue.Reader.TryRead(out var remaining))
        {
            batch.Add(remaining);
        }

        await FlushAsync(batch, CancellationToken.None);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        queue.Complete();
        return base.StopAsync(cancellationToken);
    }

    private async Task FillBatchAsync(List<ClickEvent> batch, CancellationToken windowToken)
    {
        try
        {
            while (batch.Count < _options.BatchSize)
            {
                if (queue.Reader.TryRead(out var click))
                {
                    batch.Add(click);
                    continue;
                }

                if (!await queue.Reader.WaitToReadAsync(windowToken))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task FlushAsync(List<ClickEvent> batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
        {
            return;
        }

        await persister.PersistAsync(batch.ToArray(), cancellationToken);
        batch.Clear();
    }
}
