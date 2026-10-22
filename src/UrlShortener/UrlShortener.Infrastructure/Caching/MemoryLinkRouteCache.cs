using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using UrlShortener.Application.Abstractions;
using UrlShortener.Domain;

namespace UrlShortener.Infrastructure.Caching;

internal sealed class MemoryLinkRouteCache(IMemoryCache cache, IOptions<CacheOptions> options) : ILinkRouteCache
{
    private readonly CacheOptions _options = options.Value;

    public async Task<LinkRoute?> GetOrLoadAsync(
        ShortCode code, Func<CancellationToken, Task<LinkRoute?>> load, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(load);

        var key = CacheKeys.Route(code.Value);
        if (cache.TryGetValue(key, out CachedRoute? cached) && cached is not null)
        {
            return cached.Route;
        }

        var route = await load(cancellationToken);
        var ttl = TimeSpan.FromSeconds(route is null ? _options.MissingRouteTtlSeconds : _options.RouteTtlSeconds);
        cache.Set(key, new CachedRoute(route), new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl, Size = 1 });
        return route;
    }

    public Task InvalidateAsync(ShortCode code, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(code);
        cache.Remove(CacheKeys.Route(code.Value));
        return Task.CompletedTask;
    }
}
