using SourceSharp.Host.Steam;

namespace SourceSharp.Host.Steam.Tests;

public class SteamIdFacts
{
    [Fact]
    public void A_decimal_steamid_parses() =>
        Assert.True(SteamId.TryParse("76561197960287930", out var id) && id.Value == 76561197960287930UL);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("+76561197960287930")]
    [InlineData(" 76561197960287930")]
    [InlineData("STEAM_0:0:11101")]
    [InlineData("18446744073709551616")]
    public void Anything_but_plain_digits_is_refused(string? text)
    {
        Assert.False(SteamId.TryParse(text, out var id));
        Assert.Equal(SteamId.Invalid, id);
    }

    [Fact]
    public void Zero_is_invalid_and_says_so() =>
        Assert.Equal(("steamid:invalid", false), (new SteamId(0).ToString(), new SteamId(0).IsValid));
}
