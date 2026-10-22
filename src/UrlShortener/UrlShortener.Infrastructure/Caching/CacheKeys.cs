using UrlShortener.Application.Abstractions;

namespace UrlShortener.Infrastructure.Caching;

internal static class CacheKeys
{
    public const string InvalidationChannel = "shortener:route-invalidation";

    public static string Route(string code) => $"shortener:route:{code}";
}

internal sealed record CachedRoute(LinkRoute? Route);
