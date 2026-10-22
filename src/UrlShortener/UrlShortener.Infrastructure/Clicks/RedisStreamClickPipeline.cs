using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using UrlShortener.Application.Abstractions;
using UrlShortener.Domain;

namespace UrlShortener.Infrastructure.Clicks;

internal sealed partial class RedisStreamClickTracker(
    IConnectionMultiplexer redis, IOptions<AnalyticsOptions> options, ILogger<RedisStreamClickTracker> logger) : IClickTracker
{
    private const string PayloadField = "payload";
    private readonly AnalyticsOptions _options = options.Value;

    public async ValueTask<bool> TryTrackAsync(ClickEvent click, CancellationToken cancellationToken)
    {
        try
        {
            await redis.GetDatabase().StreamAddAsync(
                _options.StreamName,
                PayloadField,
                JsonSerializer.Serialize(ClickMessage.From(click)),
                maxLength: _options.StreamMaxLength,
                useApproximateMaxLength: true);
            return true;
        }
        catch (RedisException ex)
        {
            LogPublishFailed(logger, ex);
            return false;
        }
    }

    internal static ClickEvent? Decode(StreamEntry entry)
    {
        var payload = entry[PayloadField];
        return payload.HasValue ? JsonSerializer.Deserialize<ClickMessage>((string)payload!)?.ToEvent() : null;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Publishing click to Redis stream failed")]
    private static partial void LogPublishFailed(ILogger logger, Exception exception);
}

internal sealed partial class RedisStreamClickConsumer(
    IConnectionMultiplexer redis,
    ClickBatchPersister persister,
    IOptions<AnalyticsOptions> options,
    ILogger<RedisStreamClickConsumer> logger) : BackgroundService
{
    private readonly AnalyticsOptions _options = options.Value;
    private readonly string _consumerName = $"{Environment.MachineName}-{Environment.ProcessId}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var db = redis.GetDatabase();
        await EnsureConsumerGroupAsync(db);
        LogStarted(logger, _consumerName, _options.StreamName);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var claimed = await db.StreamAutoClaimAsync(
                    _options.StreamName, _options.ConsumerGroup, _consumerName,
                    _options.PendingClaimIdleMilliseconds, "0-0", _options.BatchSize);
                var entries = claimed.ClaimedEntries.Length > 0
                    ? claimed.ClaimedEntries
                    : await db.StreamReadGroupAsync(
                        _options.StreamName, _options.ConsumerGroup, _consumerName, ">", _options.BatchSize);

                if (entries.Length == 0)
                {
                    await Task.Delay(_options.FlushIntervalMilliseconds, stoppingToken);
                    continue;
                }

                await ProcessAsync(db, entries, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (RedisException ex)
            {
                LogRedisUnavailable(logger, ex);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }

    private async Task ProcessAsync(IDatabase db, StreamEntry[] entries, CancellationToken cancellationToken)
    {
        var clicks = entries.Select(RedisStreamClickTracker.Decode).OfType<ClickEvent>().ToArray();
        if (!await persister.PersistAsync(clicks, cancellationToken))
        {
            return;
        }

        await db.StreamAcknowledgeAsync(_options.StreamName, _options.ConsumerGroup, entries.Select(e => e.Id).ToArray());
    }

    private async Task EnsureConsumerGroupAsync(IDatabase db)
    {
        try
        {
            await db.StreamCreateConsumerGroupAsync(_options.StreamName, _options.ConsumerGroup, StreamPosition.Beginning, createStream: true);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP", StringComparison.Ordinal))
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Click consumer {Consumer} reading stream {Stream}")]
    private static partial void LogStarted(ILogger logger, string consumer, string stream);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Redis unavailable for click consumption; backing off")]
    private static partial void LogRedisUnavailable(ILogger logger, Exception exception);
}
