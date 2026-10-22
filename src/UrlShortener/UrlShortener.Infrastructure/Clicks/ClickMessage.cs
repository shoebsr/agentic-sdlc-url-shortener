using UrlShortener.Domain;

namespace UrlShortener.Infrastructure.Clicks;

internal sealed record ClickMessage(
    Guid Id, Guid ShortLinkId, DateTimeOffset OccurredAt, string? ReferrerHost, string? UserAgent, string? VisitorHash)
{
    public static ClickMessage From(ClickEvent click) =>
        new(click.Id, click.ShortLinkId, click.OccurredAt, click.ReferrerHost, click.UserAgent, click.VisitorHash);

    public ClickEvent ToEvent() =>
        ClickEvent.Rehydrate(Id, ShortLinkId, OccurredAt, ReferrerHost, UserAgent, VisitorHash);
}
