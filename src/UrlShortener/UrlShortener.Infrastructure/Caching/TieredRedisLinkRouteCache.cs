using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using UrlShortener.Application.Abstractions;
using UrlShortener.Domain;

namespace UrlShortener.Infrastructure.Caching;

internal sealed partial class TieredRedisLinkRouteCache : ILinkRouteCache
{
    private readonly IMemoryCache _local;
    private readonly IConnectionMultiplexer _redis;
    private readonly CacheOptions _options;
    private readonly ILogger<TieredRedisLinkRouteCache> _logger;

    public TieredRedisLinkRouteCache(
        IMemoryCache local, IConnectionMultiplexer redis, IOptions<CacheOptions> options, ILogger<TieredRedisLinkRouteCache> logger)
    {
        _local = local;
        _redis = redis;
        _options = options.Value;
        _logger = logger;
        _redis.GetSubscriber().Subscribe(RedisChannel.Literal(CacheKeys.InvalidationChannel), (_, code) =>
        {
            if (code.HasValue)
            {
                _local.Remove(CacheKeys.Route(code!));
            }
        });
    }

    public async Task<LinkRoute?> GetOrLoadAsync(
        ShortCode code, Func<CancellationToken, Task<LinkRoute?>> load, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(load);

        var key = CacheKeys.Route(code.Value);
        if (_local.TryGetValue(key, out CachedRoute? local) && local is not null)
        {
            return local.Route;
        }

        var shared = await TryReadSharedAsync(key);
        if (shared is not null)
        {
            StoreLocal(key, shared);
            return shared.Route;
        }

        var route = await load(cancellationToken);
        var entry = new CachedRoute(route);
        StoreLocal(key, entry);
        await TryWriteSharedAsync(key, entry);
        return route;
    }

    public async Task InvalidateAsync(ShortCode code, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(code);
        var key = CacheKeys.Route(code.Value);
        _local.Remove(key);
        try
        {
            var db = _redis.GetDatabase();
            await db.KeyDeleteAsync(key);
            await _redis.GetSubscriber().PublishAsync(RedisChannel.Literal(CacheKeys.InvalidationChannel), code.Value);
        }
        catch (RedisException ex)
        {
            LogRedisFailure(_logger, "invalidate", ex);
        }
    }

    private void StoreLocal(string key, CachedRoute entry) =>
        _local.Set(key, entry, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(_options.LocalTierTtlSeconds),
            Size = 1,
        });

    private async Task<CachedRoute?> TryReadSharedAsync(string key)
    {
        try
        {
            var value = await _redis.GetDatabase().StringGetAsync(key);
            return value.HasValue ? JsonSerializer.Deserialize<CachedRoute>((string)value!) : null;
        }
        catch (RedisException ex)
        {
            LogRedisFailure(_logger, "read", ex);
            return null;
        }
    }

    private async Task TryWriteSharedAsync(string key, CachedRoute entry)
    {
        var ttl = TimeSpan.FromSeconds(entry.Route is null ? _options.MissingRouteTtlSeconds : _options.RouteTtlSeconds);
        try
        {
            await _redis.GetDatabase().StringSetAsync(key, JsonSerializer.Serialize(entry), ttl);
        }
        catch (RedisException ex)
        {
            LogRedisFailure(_logger, "write", ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Redis route cache {Operation} failed; falling back to database")]
    private static partial void LogRedisFailure(ILogger logger, string operation, Exception exception);
}
