using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using UrlShortener.Application.Links;
using UrlShortener.Presentation;

namespace UrlShortener.IntegrationTests;

public sealed class ShortenerApiTests(ShortenerApiFactory factory) : IClassFixture<ShortenerApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Fact]
    public async Task Create_then_redirect_round_trip()
    {
        var created = await CreateAsync("https://www.schwab.com/learn", $"learn-{Suffix()}");

        var response = await _client.GetAsync(new Uri(created.Link.Code, UriKind.Relative));

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("https://www.schwab.com/learn", response.Headers.Location!.AbsoluteUri);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_returns_location_and_problem_details_for_invalid_input()
    {
        var ok = await _client.PostAsJsonAsync("/api/v1/links", new CreateLinkRequest("https://example.com", null, null));
        var bad = await _client.PostAsJsonAsync("/api/v1/links", new CreateLinkRequest("javascript:alert(1)", null, null));

        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        Assert.NotNull(ok.Headers.Location);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("application/problem+json", bad.Content.Headers.ContentType!.MediaType);
        Assert.Equal("url.scheme", (await ProblemCodeAsync(bad)));
    }

    [Fact]
    public async Task Duplicate_alias_conflicts()
    {
        var alias = $"dup-{Suffix()}";
        await CreateAsync("https://example.com/1", alias);

        var second = await _client.PostAsJsonAsync("/api/v1/links", new CreateLinkRequest("https://example.com/2", alias, null));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("code.taken", await ProblemCodeAsync(second));
    }

    [Fact]
    public async Task Unknown_code_is_404_and_reserved_paths_are_not_shadowed()
    {
        var missing = await _client.GetAsync(new Uri("nope1234", UriKind.Relative));
        var health = await _client.GetAsync(new Uri("health/live", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task Management_endpoints_are_owner_only()
    {
        var created = await CreateAsync("https://example.com", $"own-{Suffix()}");
        var path = $"/api/v1/links/{created.Link.Code}";

        var anonymous = await _client.GetAsync(new Uri(path, UriKind.Relative));
        var wrongToken = await SendAsync(HttpMethod.Delete, path, "not-the-token");
        var owner = await SendAsync(HttpMethod.Get, path, created.ManagementToken);

        Assert.Equal(HttpStatusCode.Forbidden, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, wrongToken.StatusCode);
        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
    }

    [Fact]
    public async Task Disabled_link_returns_410()
    {
        var created = await CreateAsync("https://example.com", $"off-{Suffix()}");

        var disable = await SendAsync(HttpMethod.Delete, $"/api/v1/links/{created.Link.Code}", created.ManagementToken);
        var redirect = await _client.GetAsync(new Uri(created.Link.Code, UriKind.Relative));

        Assert.Equal(HttpStatusCode.NoContent, disable.StatusCode);
        Assert.Equal(HttpStatusCode.Gone, redirect.StatusCode);
    }

    [Fact]
    public async Task Clicks_flow_asynchronously_into_analytics()
    {
        var created = await CreateAsync("https://example.com", $"stats-{Suffix()}");
        foreach (var referrer in new[] { "https://google.com/a", "https://google.com/b", "https://linkedin.com/feed" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, created.Link.Code);
            request.Headers.Referrer = new Uri(referrer);
            await _client.SendAsync(request);
        }

        var analytics = await PollAnalyticsAsync(created, expectedClicks: 3);

        Assert.Equal(3, analytics.TotalClicks);
        Assert.Equal(3, analytics.WindowClicks);
        Assert.Equal(7, analytics.Daily.Count);
        Assert.Equal("google.com", analytics.TopReferrers[0].Referrer);
        Assert.Equal(2, analytics.TopReferrers[0].Clicks);
    }

    [Fact]
    public async Task Responses_carry_security_headers_and_hide_server_banner()
    {
        var response = await _client.GetAsync(new Uri("health/live", UriKind.Relative));

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.False(response.Headers.Contains("Server"));
    }

    [Fact]
    public async Task Oversized_body_is_rejected()
    {
        var huge = new CreateLinkRequest("https://example.com/" + new string('a', 20_000), null, null);

        var response = await _client.PostAsJsonAsync("/api/v1/links", huge);

        Assert.True((int)response.StatusCode is 400 or 413);
    }

    [Fact]
    public async Task Readiness_reports_database_health()
    {
        var response = await _client.GetAsync(new Uri("health/ready", UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("database", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenApi_document_is_published()
    {
        var response = await _client.GetAsync(new Uri("openapi/v1.json", UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("/api/v1/links", body, StringComparison.Ordinal);
    }

    private async Task<CreatedShortLink> CreateAsync(string url, string? alias)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/links", new CreateLinkRequest(url, alias, null));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CreatedShortLink>())!;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string token)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add(LinkEndpoints.ManagementTokenHeader, token);
        return await _client.SendAsync(request);
    }

    private async Task<LinkAnalyticsView> PollAnalyticsAsync(CreatedShortLink link, int expectedClicks)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var response = await SendAsync(HttpMethod.Get, $"/api/v1/links/{link.Link.Code}/analytics?days=7", link.ManagementToken);
            var analytics = (await response.Content.ReadFromJsonAsync<LinkAnalyticsView>())!;
            if (analytics.WindowClicks >= expectedClicks || DateTime.UtcNow > deadline)
            {
                return analytics;
            }

            await Task.Delay(100);
        }
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];
}
