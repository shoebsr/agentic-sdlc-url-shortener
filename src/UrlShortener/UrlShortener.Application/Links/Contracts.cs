namespace UrlShortener.Application.Links;

public sealed record CreateShortLinkCommand(string? Url, string? CustomAlias, DateTimeOffset? ExpiresAt);

public sealed record ShortLinkView(
    string Code,
    string ShortUrl,
    string TargetUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    bool IsActive,
    long ClickCount);

public sealed record CreatedShortLink(ShortLinkView Link, string ManagementToken);

public sealed record ClickContext(string? ClientAddress, string? Referrer, string? UserAgent);

public sealed record LinkAnalyticsView(
    string Code,
    DateOnly From,
    DateOnly To,
    long TotalClicks,
    long WindowClicks,
    long UniqueVisitors,
    IReadOnlyList<Abstractions.DailyClicks> Daily,
    IReadOnlyList<Abstractions.ReferrerClicks> TopReferrers);
