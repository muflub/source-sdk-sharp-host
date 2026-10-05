using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Testing;

namespace SourceSharp.Host.Data.Tests;

/// <summary>plan_steam_auth S-10, S-14: the steam_auth and bans stores on SQLite :memory:.</summary>
public class SteamAuthStoreFacts
{
    static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    static SteamAuthRecord Row(string session, string steamId = "76561198000000001", string epoch = "e1") =>
        new(session, steamId, new string('a', 64), 90071992547409921UL, epoch, "valid", Now);

    [Fact]
    public async Task A_verdict_round_trips_and_is_found_by_steamid()
    {
        await using var d = new TestData();
        await d.Write(tx => tx.SteamAuth.Upsert(Row("s1")));
        var got = await d.Read(tx => tx.SteamAuth.Get("s1"));
        Assert.Equal(Row("s1"), got);
        Assert.Single(await d.Read(tx => tx.SteamAuth.ForSteamId("76561198000000001")));
    }

    [Fact]
    public async Task Upsert_replaces_the_sessions_row()
    {
        await using var d = new TestData();
        await d.Write(tx => tx.SteamAuth.Upsert(Row("s1", epoch: "e1")));
        await d.Write(tx => tx.SteamAuth.Upsert(Row("s1", epoch: "e2")));
        Assert.Equal("e2", (await d.Read(tx => tx.SteamAuth.Get("s1")))!.Epoch);
    }

    [Fact]
    public async Task Delete_removes_only_that_session()
    {
        await using var d = new TestData();
        await d.Write(tx => tx.SteamAuth.Upsert(Row("s1")));
        await d.Write(tx => tx.SteamAuth.Upsert(Row("s2")));
        Assert.True(await d.Write(tx => tx.SteamAuth.Delete("s1")));
        Assert.False(await d.Write(tx => tx.SteamAuth.Delete("s1")));
        Assert.Equal(["s2"], (await d.Read(tx => tx.SteamAuth.ForSteamId("76561198000000001"))).Select(r => r.SessionId));
    }

    [Fact]
    public async Task A_ban_on_the_same_value_replaces_the_older_one()
    {
        await using var d = new TestData();
        await d.Write(tx => tx.Bans.Set(new BanRecord("address", "203.0.113.7", Now.AddMinutes(10), "auto", "strikes", Now)));
        await d.Write(tx => tx.Bans.Set(new BanRecord("address", "203.0.113.7", null, "admin", "abuse", Now)));
        var b = Assert.Single(await d.Read(tx => tx.Bans.List()));
        Assert.Equal((null, "admin"), (b.Until, b.Source));
    }

    [Fact]
    public async Task List_at_a_time_leaves_out_expired_bans()
    {
        await using var d = new TestData();
        await d.Write(tx => tx.Bans.Set(new BanRecord("steamid", "1", Now.AddMinutes(10), "auto", "r", Now)));
        await d.Write(tx => tx.Bans.Set(new BanRecord("steamid", "2", null, "admin", "r", Now)));
        Assert.Equal(2, (await d.Read(tx => tx.Bans.List())).Count);
        Assert.Equal(["2"], (await d.Read(tx => tx.Bans.List(Now.AddMinutes(10)))).Select(b => b.Value));
        Assert.True(await d.Write(tx => tx.Bans.Remove("steamid", "2")));
        Assert.Single(await d.Read(tx => tx.Bans.List()));
    }
}
