using System.Net;
using System.Net.Http.Json;
using UrlShortener.Presentation;

namespace UrlShortener.IntegrationTests;

public sealed class RateLimitingTests : IDisposable
{
    private readonly ShortenerApiFactory _factory = new() { CreatePerMinute = 3 };

    [Fact]
    public async Task Create_is_throttled_per_client_with_retry_after()
    {
        var client = _factory.CreateClient();
        var statuses = new List<HttpStatusCode>();

        for (var i = 0; i < 5; i++)
        {
            var response = await client.PostAsJsonAsync("/api/v1/links", new CreateLinkRequest($"https://example.com/{i}", null, null));
            statuses.Add(response.StatusCode);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                Assert.True(response.Headers.Contains("Retry-After"));
            }
        }

        Assert.Equal(3, statuses.Count(s => s == HttpStatusCode.Created));
        Assert.Equal(2, statuses.Count(s => s == HttpStatusCode.TooManyRequests));
    }

    public void Dispose() => _factory.Dispose();
}
