using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace UrlShortener.IntegrationTests;

public sealed class ShortenerApiFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString = $"Data Source=shortener-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private readonly SqliteConnection _keepAlive;

    public ShortenerApiFactory()
    {
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();
    }

    public int CreatePerMinute { get; init; } = 10_000;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Shortener", _connectionString);
        builder.UseSetting("ConnectionStrings:Redis", string.Empty);
        builder.UseSetting("Database:Provider", "Sqlite");
        builder.UseSetting("Database:InitializeOnStartup", "true");
        builder.UseSetting("Shortener:PublicBaseUrl", "https://sho.rt");
        builder.UseSetting("Analytics:FlushIntervalMilliseconds", "50");
        builder.UseSetting("Analytics:FingerprintKey", "integration-test-key");
        builder.UseSetting("RateLimits:CreatePerMinute", CreatePerMinute.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.UseSetting("RateLimits:RedirectPerMinute", "10000");
        builder.UseSetting("RateLimits:ManagementPerMinute", "10000");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _keepAlive.Dispose();
        }
    }
}
