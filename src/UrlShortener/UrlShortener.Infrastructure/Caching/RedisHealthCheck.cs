using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace UrlShortener.Infrastructure.Caching;

internal sealed class RedisHealthCheck(IConnectionMultiplexer redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var latency = await redis.GetDatabase().PingAsync();
            return HealthCheckResult.Healthy($"Redis responded in {latency.TotalMilliseconds:F1} ms");
        }
        catch (RedisException ex)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "Redis is unreachable", ex);
        }
    }
}
