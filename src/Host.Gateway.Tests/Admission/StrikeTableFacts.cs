using SourceSharp.Host.Gateway.Admission;

namespace SourceSharp.Host.Gateway.Tests.Admission;

public class StrikeTableFacts
{
    static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    readonly TempBanOptions _rules = new();
    StrikeTable Table() => new(() => _rules);

    static BanEntry? StrikeN(StrikeTable t, int n, DateTimeOffset at, BanKind kind = BanKind.Address, string key = "203.0.113.7")
    {
        BanEntry? last = null;
        for (var i = 0; i < n; i++) last = t.Strike(kind, key, at, "deny");
        return last;
    }

    [Fact]
    public void One_under_the_threshold_does_not_ban_and_the_threshold_does()
    {
        var t = Table();
        Assert.Null(StrikeN(t, 4, Now));
        var ban = t.Strike(BanKind.Address, "203.0.113.7", Now, "deny");
        Assert.NotNull(ban);
        Assert.Equal((BanKind.Address, "203.0.113.7", Now.AddMinutes(10)), (ban.Kind, ban.Value, ban.Until));
    }

    [Fact]
    public void A_strike_older_than_the_window_does_not_count()
    {
        var t = Table();
        t.Strike(BanKind.Address, "203.0.113.7", Now, "deny");
        Assert.Null(StrikeN(t, 4, Now.AddSeconds(61)));
        Assert.Equal(4, t.Pending(BanKind.Address, "203.0.113.7", Now.AddSeconds(61)));
    }

    [Fact]
    public void Each_ban_within_the_repeat_window_doubles_up_to_the_cap()
    {
        var t = Table();
        var at = Now;
        var durations = new List<TimeSpan>();
        for (var i = 0; i < 10; i++)
        {
            var ban = StrikeN(t, 5, at)!;
            durations.Add(ban.Until!.Value - at);
            at = ban.Until!.Value; // strikes again the moment the ban ends
        }
        Assert.Equal([10, 20, 40, 80, 160, 320, 640, 1280, 1440, 1440], durations.Select(d => (int)d.TotalMinutes));
    }

    [Fact]
    public void A_ban_after_the_repeat_window_starts_over_at_the_base()
    {
        var t = Table();
        StrikeN(t, 5, Now);
        var later = Now.AddHours(25);
        Assert.Equal(later.AddMinutes(10), StrikeN(t, 5, later)!.Until);
    }

    [Fact]
    public void Addresses_and_steamids_are_counted_apart()
    {
        var t = Table();
        StrikeN(t, 4, Now, BanKind.Address, "1");
        Assert.Null(t.Strike(BanKind.SteamId, "1", Now, "deny"));
        Assert.Equal(1, t.Pending(BanKind.SteamId, "1", Now));
    }

    [Fact]
    public void A_live_change_to_the_threshold_applies_to_the_next_strike()
    {
        var t = Table();
        StrikeN(t, 2, Now);
        _rules.Ip.Failures = 3;
        Assert.NotNull(t.Strike(BanKind.Address, "203.0.113.7", Now, "deny"));
    }

    [Fact]
    public void Prune_forgets_quiet_keys_and_keeps_recent_ones()
    {
        var t = Table();
        t.Strike(BanKind.Address, "old", Now, "deny");
        t.Strike(BanKind.Address, "new", Now.AddSeconds(59), "deny");
        t.Prune(Now.AddSeconds(61));
        Assert.Equal((0, 1), (t.Pending(BanKind.Address, "old", Now.AddSeconds(61)), t.Pending(BanKind.Address, "new", Now.AddSeconds(61))));
    }
}
