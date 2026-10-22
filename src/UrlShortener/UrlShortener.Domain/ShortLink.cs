using BuildingBlocks;

namespace UrlShortener.Domain;

public sealed class ShortLink
{
    private ShortLink()
    {
        Code = null!;
        Destination = null!;
        ManagementTokenHash = null!;
    }

    private ShortLink(
        ShortCode code,
        DestinationUrl destination,
        string managementTokenHash,
        DateTimeOffset createdAt,
        DateTimeOffset? expiresAt,
        bool isCustomAlias)
    {
        Id = Guid.CreateVersion7(createdAt);
        Code = code;
        Destination = destination;
        ManagementTokenHash = managementTokenHash;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        IsCustomAlias = isCustomAlias;
    }

    public Guid Id { get; private set; }

    public ShortCode Code { get; private set; }

    public DestinationUrl Destination { get; private set; }

    public string ManagementTokenHash { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? ExpiresAt { get; private set; }

    public DateTimeOffset? DisabledAt { get; private set; }

    public bool IsCustomAlias { get; private set; }

    public long ClickCount { get; private set; }

    public bool IsDisabled => DisabledAt is not null;

    public static Result<ShortLink> Create(
        ShortCode code,
        DestinationUrl destination,
        string managementTokenHash,
        DateTimeOffset now,
        DateTimeOffset? expiresAt,
        bool isCustomAlias)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentException.ThrowIfNullOrWhiteSpace(managementTokenHash);

        if (expiresAt is not null && expiresAt <= now)
        {
            return DomainErrors.Link.ExpiryInPast;
        }

        return new ShortLink(code, destination, managementTokenHash, now, expiresAt, isCustomAlias);
    }

    public bool IsExpired(DateTimeOffset now) => ExpiresAt is not null && ExpiresAt <= now;

    public bool IsActive(DateTimeOffset now) => !IsDisabled && !IsExpired(now);

    public Result EnsureResolvable(DateTimeOffset now)
    {
        if (IsDisabled)
        {
            return DomainErrors.Link.Disabled;
        }

        return IsExpired(now) ? DomainErrors.Link.Expired : Result.Success();
    }

    public void Disable(DateTimeOffset now) => DisabledAt ??= now;
}
