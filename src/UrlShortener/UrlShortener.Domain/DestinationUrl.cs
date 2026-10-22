using System.Net;
using System.Net.Sockets;
using BuildingBlocks;

namespace UrlShortener.Domain;

public sealed record DestinationUrl
{
    public const int MaxLength = 2048;

    private static readonly string[] InternalHostSuffixes = [".localhost", ".internal", ".local"];

    private DestinationUrl(Uri value) => Value = value;

    public Uri Value { get; }

    public static Result<DestinationUrl> Create(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return DomainErrors.Url.Empty;
        }

        raw = raw.Trim();
        if (raw.Length > MaxLength)
        {
            return DomainErrors.Url.TooLong;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            return DomainErrors.Url.Malformed;
        }

        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            return DomainErrors.Url.Scheme;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return DomainErrors.Url.Credentials;
        }

        if (IsInternalHost(uri))
        {
            return DomainErrors.Url.InternalHost;
        }

        return new DestinationUrl(uri);
    }

    private static bool IsInternalHost(Uri uri)
    {
        var host = uri.IdnHost;
        if (uri.IsLoopback
            || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || InternalHostSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IsPrivateAddress(ip);
    }

    private static bool IsPrivateAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
        {
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal;
        }

        var octets = ip.GetAddressBytes();
        return octets[0] == 10
            || (octets[0] == 172 && octets[1] is >= 16 and <= 31)
            || (octets[0] == 192 && octets[1] == 168)
            || (octets[0] == 169 && octets[1] == 254)
            || (octets[0] == 100 && octets[1] is >= 64 and <= 127)
            || octets[0] == 0;
    }

    public override string ToString() => Value.AbsoluteUri;
}
