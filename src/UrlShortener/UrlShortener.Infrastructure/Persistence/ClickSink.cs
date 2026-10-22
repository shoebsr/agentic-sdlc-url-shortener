using Microsoft.EntityFrameworkCore;
using UrlShortener.Application.Abstractions;
using UrlShortener.Domain;

namespace UrlShortener.Infrastructure.Persistence;

internal sealed class ClickSink(ShortenerDbContext db) : IClickSink
{
    public async Task PersistAsync(IReadOnlyCollection<ClickEvent> clicks, CancellationToken cancellationToken)
    {
        if (clicks.Count == 0)
        {
            return;
        }

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async ct =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            var incoming = clicks.Select(c => c.Id).ToList();
            var alreadyStored = await db.ClickEvents.AsNoTracking()
                .Where(c => incoming.Contains(c.Id))
                .Select(c => c.Id)
                .ToListAsync(ct);
            var fresh = clicks.Where(c => !alreadyStored.Contains(c.Id)).ToList();

            db.ClickEvents.AddRange(fresh);
            await db.SaveChangesAsync(ct);

            foreach (var group in fresh.GroupBy(c => c.ShortLinkId))
            {
                var increment = group.LongCount();
                await db.ShortLinks
                    .Where(l => l.Id == group.Key)
                    .ExecuteUpdateAsync(s => s.SetProperty(l => l.ClickCount, l => l.ClickCount + increment), ct);
            }

            await transaction.CommitAsync(ct);
            db.ChangeTracker.Clear();
        }, cancellationToken);
    }
}
