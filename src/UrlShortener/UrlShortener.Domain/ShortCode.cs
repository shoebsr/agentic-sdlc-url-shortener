using BuildingBlocks;

namespace UrlShortener.Domain;

public sealed record ShortCode
{
    public const int MinLength = 4;
    public const int MaxLength = 32;

    private static readonly HashSet<string> ReservedRoutes = new(StringComparer.OrdinalIgnoreCase)
    {
        "api", "health", "swagger", "openapi", "metrics", "admin", "static", "favicon.ico",
    };

    private ShortCode(string value) => Value = value;

    public string Value { get; }

    public static Result<ShortCode> Create(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DomainErrors.ShortCode.Empty;
        }

        if (value.Length is < MinLength or > MaxLength)
        {
            return DomainErrors.ShortCode.Length;
        }

        if (!value.All(IsAllowed))
        {
            return DomainErrors.ShortCode.Characters;
        }

        if (ReservedRoutes.Contains(value))
        {
            return DomainErrors.ShortCode.Reserved;
        }

        return new ShortCode(value);
    }

    private static bool IsAllowed(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_';

    public override string ToString() => Value;
}
