using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UrlShortener.Application.Abstractions;

namespace UrlShortener.Infrastructure.Security;

internal sealed partial class HmacVisitorFingerprint : IVisitorFingerprint
{
    private readonly byte[] _key;

    public HmacVisitorFingerprint(IOptions<AnalyticsOptions> options, ILogger<HmacVisitorFingerprint> logger)
    {
        var configuredKey = options.Value.FingerprintKey;
        if (string.IsNullOrWhiteSpace(configuredKey))
        {
            _key = RandomNumberGenerator.GetBytes(32);
            LogEphemeralKey(logger);
        }
        else
        {
            _key = Encoding.UTF8.GetBytes(configuredKey);
        }
    }

    public string? Compute(string? clientAddress, string? userAgent)
    {
        if (string.IsNullOrEmpty(clientAddress))
        {
            return null;
        }

        var material = Encoding.UTF8.GetBytes($"{clientAddress}|{userAgent}");
        return Convert.ToHexStringLower(HMACSHA256.HashData(_key, material));
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Analytics:FingerprintKey is not configured; using a per-process key, unique-visitor counts will not be stable across instances")]
    private static partial void LogEphemeralKey(ILogger logger);
}
