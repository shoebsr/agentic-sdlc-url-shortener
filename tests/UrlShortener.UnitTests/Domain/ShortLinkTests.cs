using UrlShortener.Domain;

namespace UrlShortener.UnitTests.Domain;

public class ShortLinkTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void New_link_is_active_and_resolvable()
    {
        var link = NewLink(expiresAt: Now.AddDays(1));

        Assert.True(link.IsActive(Now));
        Assert.True(link.EnsureResolvable(Now).IsSuccess);
    }

    [Fact]
    public void Expiry_in_the_past_is_rejected()
    {
        var result = ShortLink.Create(Code(), Url(), "hash", Now, Now.AddSeconds(-1), false);

        Assert.Equal(DomainErrors.Link.ExpiryInPast, result.Error);
    }

    [Fact]
    public void Expired_link_is_gone()
    {
        var link = NewLink(expiresAt: Now.AddMinutes(5));

        var result = link.EnsureResolvable(Now.AddMinutes(5));

        Assert.Equal(DomainErrors.Link.Expired, result.Error);
        Assert.False(link.IsActive(Now.AddMinutes(5)));
    }

    [Fact]
    public void Disabled_link_is_gone_and_disable_is_idempotent()
    {
        var link = NewLink();

        link.Disable(Now);
        link.Disable(Now.AddHours(1));

        Assert.Equal(Now, link.DisabledAt);
        Assert.Equal(DomainErrors.Link.Disabled, link.EnsureResolvable(Now).Error);
    }

    [Fact]
    public void Click_event_truncates_untrusted_header_values()
    {
        var click = new ClickEvent(Guid.NewGuid(), Now, new string('r', 1000), new string('u', 1000), null);

        Assert.Equal(ClickEvent.MaxReferrerLength, click.ReferrerHost!.Length);
        Assert.Equal(ClickEvent.MaxUserAgentLength, click.UserAgent!.Length);
        Assert.Equal(DateOnly.FromDateTime(Now.UtcDateTime), click.OccurredOn);
    }

    private static ShortLink NewLink(DateTimeOffset? expiresAt = null) =>
        ShortLink.Create(Code(), Url(), "hash", Now, expiresAt, false).Value;

    private static ShortCode Code() => ShortCode.Create("test-code").Value;

    private static DestinationUrl Url() => DestinationUrl.Create("https://example.com").Value;
}
