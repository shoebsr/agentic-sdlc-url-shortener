using System.ComponentModel.DataAnnotations;

namespace UrlShortener.Application;

public sealed class ShortenerOptions
{
    public const string SectionName = "Shortener";

    [Required]
    [Url]
    public string PublicBaseUrl { get; set; } = "http://localhost:8080";

    [Range(5, 16)]
    public int GeneratedCodeLength { get; set; } = 7;

    [Range(1, 10)]
    public int MaxCodeAllocationAttempts { get; set; } = 5;

    [Range(1, 3650)]
    public int MaxExpiryDays { get; set; } = 365;

    [Range(1, 50)]
    public int TopReferrers { get; set; } = 10;
}
