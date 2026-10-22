using UrlShortener.Application.Abstractions;
using UrlShortener.Domain;

namespace UrlShortener.UnitTests.Application;

internal sealed class InMemoryShortLinkRepository : IShortLinkRepository
{
    private readonly Dictionary<string, ShortLink> _links = new(StringComparer.Ordinal);

    public int SaveCount { get; private set; }

    public Task<ShortLink?> FindByCodeAsync(ShortCode code, CancellationToken cancellationToken) =>
        Task.FromResult(_links.GetValueOrDefault(code.Value));

    public Task<LinkRoute?> FindRouteAsync(ShortCode code, CancellationToken cancellationToken) =>
        Task.FromResult(_links.TryGetValue(code.Value, out var link) ? LinkRoute.From(link) : null);

    public Task<bool> TryAddAsync(ShortLink link, CancellationToken cancellationToken) =>
        Task.FromResult(_links.TryAdd(link.Code.Value, link));

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        SaveCount++;
        return Task.CompletedTask;
    }
}

internal sealed class PassThroughRouteCache : ILinkRouteCache
{
    public List<string> Invalidated { get; } = [];

    public Task<LinkRoute?> GetOrLoadAsync(ShortCode code, Func<CancellationToken, Task<LinkRoute?>> load, CancellationToken cancellationToken) =>
        load(cancellationToken);

    public Task InvalidateAsync(ShortCode code, CancellationToken cancellationToken)
    {
        Invalidated.Add(code.Value);
        return Task.CompletedTask;
    }
}

internal sealed class RecordingClickTracker : IClickTracker
{
    public bool Accept { get; set; } = true;

    public List<ClickEvent> Clicks { get; } = [];

    public ValueTask<bool> TryTrackAsync(ClickEvent click, CancellationToken cancellationToken)
    {
        if (Accept)
        {
            Clicks.Add(click);
        }

        return ValueTask.FromResult(Accept);
    }
}

internal sealed class EmptyAnalyticsReader : IAnalyticsReader
{
    public Task<AnalyticsSnapshot> ReadAsync(Guid shortLinkId, DateOnly from, DateOnly to, int topReferrers, CancellationToken cancellationToken) =>
        Task.FromResult(new AnalyticsSnapshot(0, 0, [], []));
}

internal sealed class SequenceCodeGenerator(params string[] codes) : IShortCodeGenerator
{
    private int _next;

    public ShortCode Next() => ShortCode.Create(codes[Math.Min(_next++, codes.Length - 1)]).Value;
}

internal sealed class FixedTokenService : IManagementTokenService
{
    public const string Token = "owner-token";

    public IssuedToken Issue() => new(Token, "hash:" + Token);

    public bool Verify(string? presentedToken, string storedHash) => "hash:" + presentedToken == storedHash;
}

internal sealed class FixedFingerprint : IVisitorFingerprint
{
    public string? Compute(string? clientAddress, string? userAgent) => clientAddress is null ? null : "fp:" + clientAddress;
}

internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
