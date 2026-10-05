using Microsoft.Extensions.Configuration;

namespace SourceSharp.Host.Gateway.Tests.Steam;

/// <summary>The S-21 options: the tabled defaults, and every invalid value refused by name.</summary>
public class OptionsFacts
{
    [Fact]
    public void The_defaults_are_the_plans_table()
    {
        var o = new GatewayOptions();
        Assert.Equal(TimeSpan.FromSeconds(15), o.PresenceTimeout);
        foreach (var r in new[] { o.TempBan.Ip, o.TempBan.SteamId })
            Assert.Equal((5, TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(10), TimeSpan.FromHours(24), TimeSpan.FromHours(24)),
                         (r.Failures, r.Window, r.Base, r.Cap, r.RepeatWindow));
        var s = o.Steam;
        Assert.Equal((TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), 2, ReauthUnanswered.Kick, TimeSpan.FromHours(6), TimeSpan.FromSeconds(2)),
                     (s.AuthTimeout, s.ReauthTimeout, s.ReauthAttempts, s.ReauthUnanswered, s.ClaimedTrust, s.KickGrace));
        Assert.Equal((243750u, "", A2sAnswerer.Gateway, TimeSpan.FromMilliseconds(50)), (s.AppId, s.LoginToken, s.A2s, s.OutgoingTick));
        Assert.Equal((SteamMode.Steamworks, true), (s.Mode, s.Advertise));
        Assert.Equal("Steam is having issues; try again in a few minutes", s.UnavailableReason);
    }

    [Fact]
    public void The_defaults_validate() => Assert.Empty(GatewayOptionsValidator.Validate(new GatewayOptions()));

    public static TheoryData<Action<GatewayOptions>, string> Invalid => new()
    {
        { o => o.PresenceTimeout = TimeSpan.Zero, "PresenceTimeout" },
        { o => o.TempBan.Ip.Failures = 0, "TempBan.Ip.Failures" },
        { o => o.TempBan.SteamId.Window = TimeSpan.FromSeconds(-1), "TempBan.SteamId.Window" },
        { o => o.TempBan.Ip.Cap = TimeSpan.FromMinutes(1), "TempBan.Ip.Cap" },
        { o => o.Steam.AuthTimeout = TimeSpan.Zero, "Steam.AuthTimeout" },
        { o => o.Steam.ReauthTimeout = TimeSpan.Zero, "Steam.ReauthTimeout" },
        { o => o.Steam.ReauthAttempts = 0, "Steam.ReauthAttempts" },
        { o => o.Steam.ClaimedTrust = TimeSpan.Zero, "Steam.ClaimedTrust" },
        { o => o.Steam.KickGrace = TimeSpan.FromSeconds(-1), "Steam.KickGrace" },
        { o => o.Steam.UnavailableReason = " ", "Steam.UnavailableReason" },
        { o => o.Steam.OutgoingTick = TimeSpan.Zero, "Steam.OutgoingTick" },
        { o => o.Steam.AppId = 0, "Steam.AppId" },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void An_invalid_value_is_refused_by_name(Action<GatewayOptions> breakIt, string option)
    {
        var o = new GatewayOptions();
        breakIt(o);
        Assert.Contains(GatewayOptionsValidator.Validate(o), e => e.StartsWith(option + " ", StringComparison.Ordinal) || e.StartsWith(option + ".", StringComparison.Ordinal) || e.StartsWith(option + " (", StringComparison.Ordinal));
    }

    [Fact]
    public void Steam_options_bind_from_the_gateway_environment_shape()
    {
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Steam:Mode"] = "Fake",
            ["Steam:AuthTimeout"] = "00:00:07",
            ["TempBan:SteamId:Failures"] = "3",
            ["PresenceTimeout"] = "00:00:20",
        }).Build();
        var o = new GatewayOptions();
        Microsoft.Extensions.Configuration.ConfigurationBinder.Bind(config, o);
        Assert.Equal((SteamMode.Fake, TimeSpan.FromSeconds(7), 3, TimeSpan.FromSeconds(20)),
                     (o.Steam.Mode, o.Steam.AuthTimeout, o.TempBan.SteamId.Failures, o.PresenceTimeout));
    }
}
