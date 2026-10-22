using UrlShortener.Domain;

namespace UrlShortener.Application.Abstractions;

public interface IShortLinkRepository
{
    Task<ShortLink?> FindByCodeAsync(ShortCode code, CancellationToken cancellationToken);

    Task<LinkRoute?> FindRouteAsync(ShortCode code, CancellationToken cancellationToken);

    Task<bool> TryAddAsync(ShortLink link, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public interface ILinkRouteCache
{
    Task<LinkRoute?> GetOrLoadAsync(ShortCode code, Func<CancellationToken, Task<LinkRoute?>> load, CancellationToken cancellationToken);

    Task InvalidateAsync(ShortCode code, CancellationToken cancellationToken);
}

public interface IAnalyticsReader
{
    Task<AnalyticsSnapshot> ReadAsync(Guid shortLinkId, DateOnly from, DateOnly to, int topReferrers, CancellationToken cancellationToken);
}

public interface IClickTracker
{
    ValueTask<bool> TryTrackAsync(ClickEvent click, CancellationToken cancellationToken);
}

public interface IClickSink
{
    Task PersistAsync(IReadOnlyCollection<ClickEvent> clicks, CancellationToken cancellationToken);
}

public interface IShortCodeGenerator
{
    ShortCode Next();
}

public interface IManagementTokenService
{
    IssuedToken Issue();

    bool Verify(string? presentedToken, string storedHash);
}

public interface IVisitorFingerprint
{
    string? Compute(string? clientAddress, string? userAgent);
}

public sealed record IssuedToken(string Token, string Hash);

public sealed record AnalyticsSnapshot(
    long WindowClicks,
    long UniqueVisitors,
    IReadOnlyList<DailyClicks> Daily,
    IReadOnlyList<ReferrerClicks> TopReferrers);

public sealed record DailyClicks(DateOnly Date, long Clicks);

public sealed record ReferrerClicks(string Referrer, long Clicks);
