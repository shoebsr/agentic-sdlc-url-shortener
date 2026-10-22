using Microsoft.EntityFrameworkCore;
using UrlShortener.Application.Abstractions;

namespace UrlShortener.Infrastructure.Persistence;

internal sealed class AnalyticsReader(ShortenerDbContext db) : IAnalyticsReader
{
    public async Task<AnalyticsSnapshot> ReadAsync(
        Guid shortLinkId, DateOnly from, DateOnly to, int topReferrers, CancellationToken cancellationToken)
    {
        var window = db.ClickEvents.AsNoTracking()
            .Where(c => c.ShortLinkId == shortLinkId && c.OccurredOn >= from && c.OccurredOn <= to);

        var daily = await window
            .GroupBy(c => c.OccurredOn)
            .Select(g => new DailyClicks(g.Key, g.LongCount()))
            .ToListAsync(cancellationToken);

        var referrers = await window
            .Where(c => c.ReferrerHost != null)
            .GroupBy(c => c.ReferrerHost!)
            .OrderByDescending(g => g.LongCount())
            .Take(topReferrers)
            .Select(g => new ReferrerClicks(g.Key, g.LongCount()))
            .ToListAsync(cancellationToken);

        var uniqueVisitors = await window
            .Where(c => c.VisitorHash != null)
            .Select(c => c.VisitorHash)
            .Distinct()
            .LongCountAsync(cancellationToken);

        return new AnalyticsSnapshot(
            daily.Sum(d => d.Clicks),
            uniqueVisitors,
            FillGaps(daily, from, to),
            referrers);
    }

    private static List<DailyClicks> FillGaps(List<DailyClicks> daily, DateOnly from, DateOnly to)
    {
        var byDate = daily.ToDictionary(d => d.Date, d => d.Clicks);
        var series = new List<DailyClicks>();
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            series.Add(new DailyClicks(day, byDate.GetValueOrDefault(day)));
        }

        return series;
    }
}
