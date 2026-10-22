using System.ComponentModel.DataAnnotations;

namespace UrlShortener.Infrastructure;

public enum DatabaseProvider
{
    SqlServer,
    Sqlite,
}

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    public DatabaseProvider Provider { get; set; } = DatabaseProvider.SqlServer;

    public bool InitializeOnStartup { get; set; }

    [Range(0, 10)]
    public int MaxRetryCount { get; set; } = 5;
}

public sealed class CacheOptions
{
    public const string SectionName = "Cache";

    [Range(1, 86_400)]
    public int RouteTtlSeconds { get; set; } = 300;

    [Range(1, 3_600)]
    public int MissingRouteTtlSeconds { get; set; } = 30;

    [Range(1, 300)]
    public int LocalTierTtlSeconds { get; set; } = 15;
}

public sealed class AnalyticsOptions
{
    public const string SectionName = "Analytics";

    [Range(100, 1_000_000)]
    public int BufferCapacity { get; set; } = 10_000;

    [Range(1, 5_000)]
    public int BatchSize { get; set; } = 500;

    [Range(50, 60_000)]
    public int FlushIntervalMilliseconds { get; set; } = 1_000;

    [Range(1, 10)]
    public int MaxPersistAttempts { get; set; } = 3;

    public string? FingerprintKey { get; set; }

    [Required]
    public string StreamName { get; set; } = "shortener:clicks";

    [Required]
    public string ConsumerGroup { get; set; } = "analytics";

    [Range(10_000, 100_000_000)]
    public int StreamMaxLength { get; set; } = 1_000_000;

    [Range(1_000, 3_600_000)]
    public int PendingClaimIdleMilliseconds { get; set; } = 60_000;
}
