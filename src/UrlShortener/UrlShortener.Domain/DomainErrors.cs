using BuildingBlocks;

namespace UrlShortener.Domain;

public static class DomainErrors
{
    public static class ShortCode
    {
        public static readonly Error Empty = Error.Validation("code.empty", "Short code is required.");
        public static readonly Error Length = Error.Validation(
            "code.length", $"Short code must be {Domain.ShortCode.MinLength}-{Domain.ShortCode.MaxLength} characters.");
        public static readonly Error Characters = Error.Validation(
            "code.characters", "Short code may contain only letters, digits, '-' and '_'.");
        public static readonly Error Reserved = Error.Validation("code.reserved", "Short code is reserved.");
        public static readonly Error Taken = Error.Conflict("code.taken", "Short code is already in use.");
        public static readonly Error Exhausted = Error.Unavailable(
            "code.exhausted", "Could not allocate a unique short code; retry the request.");
    }

    public static class Url
    {
        public static readonly Error Empty = Error.Validation("url.empty", "URL is required.");
        public static readonly Error TooLong = Error.Validation(
            "url.too_long", $"URL must be at most {DestinationUrl.MaxLength} characters.");
        public static readonly Error Malformed = Error.Validation("url.malformed", "URL must be absolute and well-formed.");
        public static readonly Error Scheme = Error.Validation("url.scheme", "Only http and https URLs are allowed.");
        public static readonly Error Credentials = Error.Validation(
            "url.credentials", "URLs containing user credentials are not allowed.");
        public static readonly Error InternalHost = Error.Validation(
            "url.internal_host", "URLs pointing to private or internal hosts are not allowed.");
        public static readonly Error SelfReference = Error.Validation(
            "url.self_reference", "URLs pointing back to this shortener are not allowed.");
    }

    public static class Link
    {
        public static readonly Error NotFound = Error.NotFound("link.not_found", "Short link was not found.");
        public static readonly Error Expired = Error.Gone("link.expired", "Short link has expired.");
        public static readonly Error Disabled = Error.Gone("link.disabled", "Short link has been disabled.");
        public static readonly Error ExpiryInPast = Error.Validation("link.expiry_past", "Expiry must be in the future.");
        public static readonly Error ExpiryTooFar = Error.Validation("link.expiry_too_far", "Expiry exceeds the allowed maximum.");
        public static readonly Error Unauthorized = Error.Forbidden("link.forbidden", "A valid management token is required.");
    }

    public static class Analytics
    {
        public static readonly Error Window = Error.Validation("analytics.window", "Window must be between 1 and 90 days.");
    }
}
