using UrlShortener.Domain;

namespace UrlShortener.UnitTests.Domain;

public class ShortCodeTests
{
    [Theory]
    [InlineData("abcd")]
    [InlineData("Promo_2026-Q4")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ012345")]
    public void Create_accepts_valid_codes(string value)
    {
        var result = ShortCode.Create(value);

        Assert.True(result.IsSuccess);
        Assert.Equal(value, result.Value.Value);
    }

    [Theory]
    [InlineData(null, "code.empty")]
    [InlineData("  ", "code.empty")]
    [InlineData("abc", "code.length")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456", "code.length")]
    [InlineData("has space", "code.characters")]
    [InlineData("emoji😀x", "code.characters")]
    [InlineData("../etc", "code.characters")]
    [InlineData("swagger", "code.reserved")]
    [InlineData("HEALTH", "code.reserved")]
    public void Create_rejects_invalid_codes(string? value, string expectedError)
    {
        var result = ShortCode.Create(value);

        Assert.False(result.IsSuccess);
        Assert.Equal(expectedError, result.Error!.Code);
    }

    [Fact]
    public void Codes_are_case_sensitive()
    {
        Assert.NotEqual(ShortCode.Create("Abcd").Value, ShortCode.Create("abcd").Value);
    }
}
