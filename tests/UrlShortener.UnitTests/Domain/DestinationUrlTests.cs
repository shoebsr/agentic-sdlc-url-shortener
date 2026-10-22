using UrlShortener.Domain;

namespace UrlShortener.UnitTests.Domain;

public class DestinationUrlTests
{
    [Theory]
    [InlineData("https://www.schwab.com/")]
    [InlineData("http://example.com/path?q=1#frag")]
    [InlineData("https://münchen.de/")]
    [InlineData("https://8.8.8.8/dns")]
    public void Create_accepts_public_web_urls(string url)
    {
        Assert.True(DestinationUrl.Create(url).IsSuccess);
    }

    [Theory]
    [InlineData("", "url.empty")]
    [InlineData("not a url", "url.malformed")]
    [InlineData("/relative/path", "url.malformed")]
    [InlineData("javascript:alert(1)", "url.scheme")]
    [InlineData("data:text/html,<script>alert(1)</script>", "url.scheme")]
    [InlineData("file:///etc/passwd", "url.scheme")]
    [InlineData("ftp://example.com", "url.scheme")]
    [InlineData("https://user:pass@example.com", "url.credentials")]
    [InlineData("http://localhost:8080", "url.internal_host")]
    [InlineData("http://127.0.0.1", "url.internal_host")]
    [InlineData("http://10.0.0.5", "url.internal_host")]
    [InlineData("http://172.20.1.1", "url.internal_host")]
    [InlineData("http://192.168.1.1", "url.internal_host")]
    [InlineData("http://169.254.169.254/latest/meta-data", "url.internal_host")]
    [InlineData("http://[::1]/", "url.internal_host")]
    [InlineData("http://[fd00::1]/", "url.internal_host")]
    [InlineData("http://[::ffff:10.0.0.1]/", "url.internal_host")]
    [InlineData("http://db.internal/", "url.internal_host")]
    [InlineData("http://printer.local/", "url.internal_host")]
    public void Create_rejects_unsafe_urls(string url, string expectedError)
    {
        var result = DestinationUrl.Create(url);

        Assert.False(result.IsSuccess);
        Assert.Equal(expectedError, result.Error!.Code);
    }

    [Fact]
    public void Create_rejects_overlong_urls()
    {
        var url = "https://example.com/" + new string('a', DestinationUrl.MaxLength);

        Assert.Equal("url.too_long", DestinationUrl.Create(url).Error!.Code);
    }
}
