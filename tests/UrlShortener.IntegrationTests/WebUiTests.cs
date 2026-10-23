using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace UrlShortener.IntegrationTests;

public sealed class WebUiTests(ShortenerApiFactory factory) : IClassFixture<ShortenerApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Fact]
    public async Task Root_redirects_to_the_web_ui()
    {
        var response = await _client.GetAsync(new Uri("/", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/ui/", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Web_ui_is_served_under_a_self_only_content_security_policy()
    {
        var page = await _client.GetAsync(new Uri("/ui/", UriKind.Relative));
        var script = await _client.GetAsync(new Uri("/ui/app.js", UriKind.Relative));
        var csp = page.Headers.GetValues("Content-Security-Policy").Single();

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Shortly", await page.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Contains("script-src 'self'", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-inline", csp, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Api_responses_keep_the_strict_policy()
    {
        var response = await _client.GetAsync(new Uri("/health/live", UriKind.Relative));

        Assert.Equal("default-src 'none'; frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
    }
}
