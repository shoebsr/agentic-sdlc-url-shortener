using BuildingBlocks;
using UrlShortener.Domain;

namespace UrlShortener.Application.Abstractions;

public sealed record LinkRoute(Guid LinkId, string Code, Uri Destination, DateTimeOffset? ExpiresAt, DateTimeOffset? DisabledAt)
{
    public static LinkRoute From(ShortLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        return new LinkRoute(link.Id, link.Code.Value, link.Destination.Value, link.ExpiresAt, link.DisabledAt);
    }

    public Result EnsureResolvable(DateTimeOffset now)
    {
        if (DisabledAt is not null)
        {
            return DomainErrors.Link.Disabled;
        }

        return ExpiresAt is not null && ExpiresAt <= now ? DomainErrors.Link.Expired : Result.Success();
    }
}
