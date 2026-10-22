using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UrlShortener.Application;
using UrlShortener.Infrastructure;
using UrlShortener.Infrastructure.Codes;
using UrlShortener.Infrastructure.Security;

namespace UrlShortener.UnitTests.Infrastructure;

public class SecurityTests
{
    [Fact]
    public void Management_token_is_high_entropy_and_only_its_hash_is_stored()
    {
        var service = new ManagementTokenService();

        var issued = service.Issue();

        Assert.True(issued.Token.Length >= 43);
        Assert.DoesNotContain(issued.Token, issued.Hash, StringComparison.Ordinal);
        Assert.True(service.Verify(issued.Token, issued.Hash));
        Assert.False(service.Verify(issued.Token + "x", issued.Hash));
        Assert.False(service.Verify(null, issued.Hash));
        Assert.False(service.Verify(new string('a', 500), issued.Hash));
    }

    [Fact]
    public void Visitor_fingerprint_is_keyed_and_never_contains_the_address()
    {
        var a = Fingerprint("key-a");
        var b = Fingerprint("key-b");

        var hash = a.Compute("203.0.113.7", "agent");

        Assert.NotNull(hash);
        Assert.DoesNotContain("203.0.113.7", hash, StringComparison.Ordinal);
        Assert.Equal(hash, a.Compute("203.0.113.7", "agent"));
        Assert.NotEqual(hash, b.Compute("203.0.113.7", "agent"));
        Assert.Null(a.Compute(null, "agent"));
    }

    [Fact]
    public void Generated_codes_have_configured_length_and_do_not_repeat()
    {
        var generator = new RandomShortCodeGenerator(Options.Create(new ShortenerOptions { GeneratedCodeLength = 8 }));

        var codes = Enumerable.Range(0, 10_000).Select(_ => generator.Next().Value).ToList();

        Assert.All(codes, c => Assert.Equal(8, c.Length));
        Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());
    }

    private static HmacVisitorFingerprint Fingerprint(string key) => new(
        Options.Create(new AnalyticsOptions { FingerprintKey = key }), NullLogger<HmacVisitorFingerprint>.Instance);
}
