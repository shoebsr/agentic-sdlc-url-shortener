using Microsoft.EntityFrameworkCore;
using UrlShortener.Application.Abstractions;
using UrlShortener.Domain;

namespace UrlShortener.Infrastructure.Persistence;

internal sealed class ShortLinkRepository(ShortenerDbContext db) : IShortLinkRepository
{
    public Task<ShortLink?> FindByCodeAsync(ShortCode code, CancellationToken cancellationToken) =>
        db.ShortLinks.SingleOrDefaultAsync(l => l.Code == code, cancellationToken);

    public async Task<LinkRoute?> FindRouteAsync(ShortCode code, CancellationToken cancellationToken)
    {
        var link = await db.ShortLinks.AsNoTracking().SingleOrDefaultAsync(l => l.Code == code, cancellationToken);
        return link is null ? null : LinkRoute.From(link);
    }

    public async Task<bool> TryAddAsync(ShortLink link, CancellationToken cancellationToken)
    {
        db.ShortLinks.Add(link);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (UniqueViolation.IsMatch(ex))
        {
            db.Entry(link).State = EntityState.Detached;
            return false;
        }
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
