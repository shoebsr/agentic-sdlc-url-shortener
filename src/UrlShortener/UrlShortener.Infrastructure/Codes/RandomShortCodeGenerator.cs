using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using UrlShortener.Application;
using UrlShortener.Application.Abstractions;
using UrlShortener.Domain;

namespace UrlShortener.Infrastructure.Codes;

internal sealed class RandomShortCodeGenerator(IOptions<ShortenerOptions> options) : IShortCodeGenerator
{
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    private readonly int _length = options.Value.GeneratedCodeLength;

    public ShortCode Next() => ShortCode.Create(RandomNumberGenerator.GetString(Alphabet, _length)).Value;
}
