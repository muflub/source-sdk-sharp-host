using SourceSharp.Host.Gateway.Relay;
using SourceSharp.Host.Gateway.Tests.Support;
using SourceSharp.Host.Proto.Gateway;

namespace SourceSharp.Host.Gateway.Tests.Steam;

/// <summary>The live-tunable options (plan_steam_auth S-21): applied to the next decision, refused whole when invalid.</summary>
public class LiveOptionsFacts
{
    const ulong Alice = 76561198000000001UL;

    static LiveOptions Defaults(Action<LiveOptions>? change = null)
    {
        var d = new GatewayOptions();
        Proto.Gateway.StrikeRule Rule(Gateway.StrikeRule r) => new()
        {
            Failures = r.Failures, WindowMs = (long)r.Window.TotalMilliseconds, BaseMs = (long)r.Base.TotalMilliseconds,
            CapMs = (long)r.Cap.TotalMilliseconds, RepeatWindowMs = (long)r.RepeatWindow.TotalMilliseconds,
        };
        var o = new LiveOptions
        {
            PresenceTimeoutMs = (long)d.PresenceTimeout.TotalMilliseconds,
            TempBanIp = Rule(d.TempBan.Ip),
            TempBanSteamid = Rule(d.TempBan.SteamId),
            AuthTimeoutMs = (long)d.Steam.AuthTimeout.TotalMilliseconds,
            ReauthTimeoutMs = (long)d.Steam.ReauthTimeout.TotalMilliseconds,
            ReauthAttempts = d.Steam.ReauthAttempts,
            ReauthUnanswered = d.Steam.ReauthUnanswered.ToString(),
            ClaimedTrustMs = (long)d.Steam.ClaimedTrust.TotalMilliseconds,
            KickGraceMs = (long)d.Steam.KickGrace.TotalMilliseconds,
            UnavailableReason = d.Steam.UnavailableReason,
        };
        change?.Invoke(o);
        return o;
    }

    [Fact]
    public async Task A_shorter_presence_timeout_frees_a_steamid_sooner_without_a_restart()
    {
        await using var rig = Rig.WithSteam();
        rig.SetDefault(rig.Backend("hub"));
        var first = rig.Client(Alice);
        await first.ConnectAsync(Rig.T);

        Assert.Equal(ControlStatus.Ok, rig.Relay.SetOptions(rig.Next(), Defaults(o => o.PresenceTimeoutMs = 5000)));
        rig.Time.Advance(TimeSpan.FromSeconds(5));
        var second = rig.Client(Alice);
        second.TicketTail = [1];

        Assert.Equal("hub", await second.ConnectAsync(Rig.T)); // the default (15 s) would have refused it
        Assert.Equal(TimeSpan.FromSeconds(5), rig.Options.PresenceTimeout);
    }

    [Fact]
    public async Task A_changed_unavailable_reason_is_what_the_next_client_reads()
    {
        await using var rig = Rig.WithSteam();
        rig.SetDefault(rig.Backend("hub"));
        rig.Steam!.SetLoggedOn(false);
        rig.Relay.SetOptions(rig.Next(), Defaults(o => o.UnavailableReason = "maintenance"));
        var c = rig.Client(Alice);
        c.StartHandshake();
        Assert.True(await Rig.Until(() => c.RejectReason is not null));
        Assert.Equal("maintenance", c.RejectReason);
    }

    [Theory]
    [InlineData("presence 0")]
    [InlineData("failures 0")]
    [InlineData("unanswered bogus")]
    [InlineData("reason empty")]
    public async Task An_invalid_set_is_refused_whole_and_changes_nothing(string what)
    {
        await using var rig = Rig.WithSteam();
        var before = (rig.Options.PresenceTimeout, rig.Options.Steam.AuthTimeout);
        var bad = Defaults(o =>
        {
            o.AuthTimeoutMs = 1234; // a valid change riding along: must not apply either
            switch (what)
            {
                case "presence 0": o.PresenceTimeoutMs = 0; break;
                case "failures 0": o.TempBanIp.Failures = 0; break;
                case "unanswered bogus": o.ReauthUnanswered = "Bogus"; break;
                default: o.UnavailableReason = ""; break;
            }
        });
        Assert.Equal(ControlStatus.Invalid, rig.Relay.SetOptions(rig.Next(), bad));
        Assert.Equal(before, (rig.Options.PresenceTimeout, rig.Options.Steam.AuthTimeout));
        // The control: the same set made valid applies.
        Assert.Equal(ControlStatus.Ok, rig.Relay.SetOptions(rig.Next(), Defaults(o => o.AuthTimeoutMs = 1234)));
        Assert.Equal(TimeSpan.FromMilliseconds(1234), rig.Options.Steam.AuthTimeout);
    }

    [Fact]
    public async Task A_stale_set_is_refused()
    {
        await using var rig = Rig.WithSteam();
        rig.Relay.SetOptions(5, Defaults());
        Assert.Equal(ControlStatus.Stale, rig.Relay.SetOptions(4, Defaults(o => o.PresenceTimeoutMs = 1000)));
        Assert.Equal(TimeSpan.FromSeconds(15), rig.Options.PresenceTimeout);
    }

    [Fact]
    public async Task SyncTable_restores_the_options_and_the_bans()
    {
        await using var rig = Rig.WithSteam();
        var table = new RouteTable { Version = rig.Next(), Options = Defaults(o => o.KickGraceMs = 9000) };
        table.Bans.Add(new Ban { Kind = "steamid", Value = Alice.ToString(), Reason = "r" });

        Assert.Equal(ControlStatus.Ok, rig.Relay.SyncTable(table));

        Assert.Equal(TimeSpan.FromSeconds(9), rig.Options.Steam.KickGrace);
        Assert.True(rig.Relay.IsBanned(steamId: Alice));
    }
}
