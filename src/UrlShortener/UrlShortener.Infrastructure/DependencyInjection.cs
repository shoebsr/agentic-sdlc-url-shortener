using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;
using UrlShortener.Application;
using UrlShortener.Application.Abstractions;
using UrlShortener.Infrastructure.Caching;
using UrlShortener.Infrastructure.Clicks;
using UrlShortener.Infrastructure.Codes;
using UrlShortener.Infrastructure.Persistence;
using UrlShortener.Infrastructure.Security;

namespace UrlShortener.Infrastructure;

public static class DependencyInjection
{
    public const string DatabaseConnectionName = "Shortener";
    public const string RedisConnectionName = "Redis";

    public static IServiceCollection AddShortenerInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddShortenerApplication();
        services.AddOptions<DatabaseOptions>().BindConfiguration(DatabaseOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<CacheOptions>().BindConfiguration(CacheOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<AnalyticsOptions>().BindConfiguration(AnalyticsOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();

        AddPersistence(services, configuration);

        services.AddMemoryCache(o => o.SizeLimit = 100_000);
        services.AddSingleton<IShortCodeGenerator, RandomShortCodeGenerator>();
        services.AddSingleton<IManagementTokenService, ManagementTokenService>();
        services.AddSingleton<IVisitorFingerprint, HmacVisitorFingerprint>();
        services.AddSingleton<ClickBatchPersister>();

        var health = services.AddHealthChecks().AddDbContextCheck<ShortenerDbContext>("database", tags: ["ready"]);

        var redisConnection = configuration.GetConnectionString(RedisConnectionName);
        if (string.IsNullOrWhiteSpace(redisConnection))
        {
            services.AddSingleton<ILinkRouteCache, MemoryLinkRouteCache>();
            services.AddSingleton<InProcessClickQueue>();
            services.AddSingleton<IClickTracker>(sp => sp.GetRequiredService<InProcessClickQueue>());
            services.AddHostedService<InProcessClickWriter>();
        }
        else
        {
            services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(WithResilience(redisConnection)));
            services.AddSingleton<ILinkRouteCache, TieredRedisLinkRouteCache>();
            services.AddSingleton<IClickTracker, RedisStreamClickTracker>();
            health.AddCheck<RedisHealthCheck>("redis", HealthStatus.Degraded, ["ready"]);
        }

        return services;
    }

    public static IServiceCollection AddClickStreamConsumer(this IServiceCollection services)
    {
        services.AddHostedService<RedisStreamClickConsumer>();
        return services;
    }

    private static void AddPersistence(IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(DatabaseConnectionName)
            ?? throw new InvalidOperationException($"Connection string '{DatabaseConnectionName}' is not configured.");
        var database = configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>() ?? new DatabaseOptions();

        services.AddDbContextPool<ShortenerDbContext>(options =>
        {
            _ = database.Provider switch
            {
                DatabaseProvider.SqlServer => options.UseSqlServer(connectionString, sql =>
                    sql.EnableRetryOnFailure(database.MaxRetryCount).MigrationsAssembly(typeof(ShortenerDbContext).Assembly.FullName)),
                DatabaseProvider.Sqlite => options.UseSqlite(connectionString),
                _ => throw new InvalidOperationException($"Unsupported database provider '{database.Provider}'."),
            };
        });

        services.AddScoped<IShortLinkRepository, ShortLinkRepository>();
        services.AddScoped<IAnalyticsReader, AnalyticsReader>();
        services.AddScoped<IClickSink, ClickSink>();
    }

    private static ConfigurationOptions WithResilience(string connectionString)
    {
        var options = ConfigurationOptions.Parse(connectionString);
        options.AbortOnConnectFail = false;
        options.ConnectRetry = 5;
        return options;
    }
}
