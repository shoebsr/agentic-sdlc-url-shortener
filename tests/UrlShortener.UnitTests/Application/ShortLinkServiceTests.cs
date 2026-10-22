using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UrlShortener.Application;
using UrlShortener.Application.Links;
using UrlShortener.Domain;

namespace UrlShortener.UnitTests.Application;

public class ShortLinkServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);
    private static readonly ClickContext Visitor = new("203.0.113.7", "https://news.example.com/item?id=1", "Mozilla/5.0");

    private readonly InMemoryShortLinkRepository _repository = new();
    private readonly PassThroughRouteCache _cache = new();
    private readonly RecordingClickTracker _clicks = new();
    private readonly ManualTimeProvider _clock = new(Start);
    private SequenceCodeGenerator _codes = new("Gen0001", "Gen0002", "Gen0003");

    [Fact]
    public async Task Create_with_generated_code_returns_short_url_and_owner_token()
    {
        var result = await Service().CreateAsync(new("https://example.com/a", null, null), default);

        Assert.True(result.IsSuccess);
        Assert.Equal("Gen0001", result.Value.Link.Code);
        Assert.Equal("https://sho.rt/Gen0001", result.Value.Link.ShortUrl);
        Assert.Equal(FixedTokenService.Token, result.Value.ManagementToken);
    }

    [Fact]
    public async Task Create_retries_generated_code_on_collision()
    {
        _codes = new SequenceCodeGenerator("Gen0001", "Gen0001", "Gen0002");
        var service = Service();
        await service.CreateAsync(new("https://example.com/first", null, null), default);

        var second = await service.CreateAsync(new("https://example.com/second", null, null), default);

        Assert.Equal("Gen0002", second.Value.Link.Code);
    }

    [Fact]
    public async Task Create_gives_up_after_bounded_attempts()
    {
        _codes = new SequenceCodeGenerator("Gen0001");
        var service = Service();
        await service.CreateAsync(new("https://example.com/first", null, null), default);

        var result = await service.CreateAsync(new("https://example.com/second", null, null), default);

        Assert.Equal(DomainErrors.ShortCode.Exhausted, result.Error);
    }

    [Fact]
    public async Task Create_rejects_taken_custom_alias()
    {
        var service = Service();
        await service.CreateAsync(new("https://example.com", "promo", null), default);

        var result = await service.CreateAsync(new("https://example.org", "promo", null), default);

        Assert.Equal(DomainErrors.ShortCode.Taken, result.Error);
    }

    [Fact]
    public async Task Create_rejects_links_back_to_the_shortener()
    {
        var result = await Service().CreateAsync(new("https://sho.rt/loop", null, null), default);

        Assert.Equal(DomainErrors.Url.SelfReference, result.Error);
    }

    [Fact]
    public async Task Create_rejects_expiry_beyond_policy()
    {
        var result = await Service().CreateAsync(new("https://example.com", null, Start.AddDays(31)), default);

        Assert.Equal(DomainErrors.Link.ExpiryTooFar, result.Error);
    }

    [Fact]
    public async Task Resolve_returns_destination_and_records_privacy_preserving_click()
    {
        var service = Service();
        await service.CreateAsync(new("https://example.com/target", "promo", null), default);

        var result = await service.ResolveAsync("promo", Visitor, default);

        Assert.Equal(new Uri("https://example.com/target"), result.Value);
        var click = Assert.Single(_clicks.Clicks);
        Assert.Equal("news.example.com", click.ReferrerHost);
        Assert.Equal("fp:203.0.113.7", click.VisitorHash);
    }

    [Fact]
    public async Task Resolve_still_redirects_when_analytics_pipeline_is_saturated()
    {
        var service = Service();
        await service.CreateAsync(new("https://example.com/target", "promo", null), default);
        _clicks.Accept = false;

        var result = await service.ResolveAsync("promo", Visitor, default);

        Assert.True(result.IsSuccess);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("bad code!")]
    [InlineData(null)]
    public async Task Resolve_unknown_or_malformed_code_is_not_found(string? code)
    {
        var result = await Service().ResolveAsync(code, Visitor, default);

        Assert.Equal(DomainErrors.Link.NotFound, result.Error);
    }

    [Fact]
    public async Task Resolve_expired_link_is_gone()
    {
        var service = Service();
        await service.CreateAsync(new("https://example.com", "promo", Start.AddHours(1)), default);
        _clock.Advance(TimeSpan.FromHours(2));

        var result = await service.ResolveAsync("promo", Visitor, default);

        Assert.Equal(DomainErrors.Link.Expired, result.Error);
        Assert.Empty(_clicks.Clicks);
    }

    [Fact]
    public async Task Management_operations_require_the_owner_token()
    {
        var service = Service();
        await service.CreateAsync(new("https://example.com", "promo", null), default);

        Assert.Equal(DomainErrors.Link.Unauthorized, (await service.GetAsync("promo", null, default)).Error);
        Assert.Equal(DomainErrors.Link.Unauthorized, (await service.DisableAsync("promo", "wrong", default)).Error);
        Assert.Equal(DomainErrors.Link.Unauthorized, (await service.GetAnalyticsAsync("promo", "wrong", 7, default)).Error);
    }

    [Fact]
    public async Task Disable_persists_invalidates_cache_and_stops_redirects()
    {
        var service = Service();
        await service.CreateAsync(new("https://example.com", "promo", null), default);

        var disabled = await service.DisableAsync("promo", FixedTokenService.Token, default);
        var resolved = await service.ResolveAsync("promo", Visitor, default);

        Assert.True(disabled.IsSuccess);
        Assert.Equal(1, _repository.SaveCount);
        Assert.Contains("promo", _cache.Invalidated);
        Assert.Equal(DomainErrors.Link.Disabled, resolved.Error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(91)]
    public async Task Analytics_window_is_bounded(int days)
    {
        var result = await Service().GetAnalyticsAsync("promo", FixedTokenService.Token, days, default);

        Assert.Equal(DomainErrors.Analytics.Window, result.Error);
    }

    private ShortLinkService Service() => new(
        _repository,
        _cache,
        new EmptyAnalyticsReader(),
        _clicks,
        _codes,
        new FixedTokenService(),
        new FixedFingerprint(),
        _clock,
        Options.Create(new ShortenerOptions { PublicBaseUrl = "https://sho.rt", MaxExpiryDays = 30, MaxCodeAllocationAttempts = 3 }),
        NullLogger<ShortLinkService>.Instance);
}
