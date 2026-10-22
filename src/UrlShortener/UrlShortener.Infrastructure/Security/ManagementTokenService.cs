using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using UrlShortener.Application.Abstractions;

namespace UrlShortener.Infrastructure.Security;

internal sealed class ManagementTokenService : IManagementTokenService
{
    private const int TokenBytes = 32;
    private const int MaxPresentedTokenLength = 128;

    public IssuedToken Issue()
    {
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes));
        return new IssuedToken(token, Hash(token));
    }

    public bool Verify(string? presentedToken, string storedHash)
    {
        if (string.IsNullOrEmpty(presentedToken) || presentedToken.Length > MaxPresentedTokenLength)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Hash(presentedToken)),
            Encoding.ASCII.GetBytes(storedHash));
    }

    private static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
