using System.ComponentModel.DataAnnotations;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;

namespace UrlShortener.Presentation;

public static class RateLimitPolicies
{
    public const string Create = "create";
    public const string Management = "management";
    public const string Redirect = "redirect";

    internal static void Configure(RateLimiterOptions options, RateLimitSettings settings)
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.OnRejected = (context, _) =>
        {
            if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            {
                context.HttpContext.Response.Headers.RetryAfter = ((int)retryAfter.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            return ValueTask.CompletedTask;
        };

        options.AddPolicy(Create, http => PerClient(http, settings.CreatePerMinute));
        options.AddPolicy(Management, http => PerClient(http, settings.ManagementPerMinute));
        options.AddPolicy(Redirect, http => PerClient(http, settings.RedirectPerMinute));
    }

    private static RateLimitPartition<string> PerClient(HttpContext http, int permitsPerMinute) =>
        RateLimitPartition.GetFixedWindowLimiter(
            http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitsPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
}

public sealed class RateLimitSettings
{
    public const string SectionName = "RateLimits";

    [Range(1, 1_000_000)]
    public int CreatePerMinute { get; set; } = 30;

    [Range(1, 1_000_000)]
    public int ManagementPerMinute { get; set; } = 120;

    [Range(1, 10_000_000)]
    public int RedirectPerMinute { get; set; } = 600;
}
