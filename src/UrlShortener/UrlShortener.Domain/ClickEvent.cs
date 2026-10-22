namespace UrlShortener.Domain;

public sealed class ClickEvent
{
    public const int MaxReferrerLength = 255;
    public const int MaxUserAgentLength = 512;

    private ClickEvent() { }

    public ClickEvent(Guid shortLinkId, DateTimeOffset occurredAt, string? referrerHost, string? userAgent, string? visitorHash)
    {
        Id = Guid.CreateVersion7(occurredAt);
        ShortLinkId = shortLinkId;
        OccurredAt = occurredAt;
        OccurredOn = DateOnly.FromDateTime(occurredAt.UtcDateTime);
        ReferrerHost = Truncate(referrerHost, MaxReferrerLength);
        UserAgent = Truncate(userAgent, MaxUserAgentLength);
        VisitorHash = visitorHash;
    }

    public static ClickEvent Rehydrate(
        Guid id, Guid shortLinkId, DateTimeOffset occurredAt, string? referrerHost, string? userAgent, string? visitorHash) =>
        new(shortLinkId, occurredAt, referrerHost, userAgent, visitorHash) { Id = id };

    public Guid Id { get; private set; }

    public Guid ShortLinkId { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public DateOnly OccurredOn { get; private set; }

    public string? ReferrerHost { get; private set; }

    public string? UserAgent { get; private set; }

    public string? VisitorHash { get; private set; }

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? null : value.Length <= max ? value : value[..max];
}
