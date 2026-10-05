using System.Net;
using SourceSharp.Host.Gateway.Admission;

namespace SourceSharp.Host.Gateway.Tests.Admission;

public class BanListFacts
{
    static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    static BanEntry Addr(string v, DateTimeOffset? until = null) => new(BanKind.Address, v, until, "test");
    static BanEntry Sid(ulong v, DateTimeOffset? until = null) => new(BanKind.SteamId, v.ToString(), until, "test");

    static BanList With(params BanEntry[] entries)
    {
        var b = new BanList();
        b.Replace(entries, Now);
        return b;
    }

    [Fact]
    public void An_empty_list_bans_nothing()
    {
        var b = new BanList();
        Assert.Null(b.Address(IPAddress.Parse("203.0.113.7"), Now));
        Assert.Null(b.SteamId(76561197960287930UL, Now));
    }

    [Fact]
    public void A_single_address_bans_exactly_that_address()
    {
        var b = With(Addr("203.0.113.7"));
        Assert.NotNull(b.Address(IPAddress.Parse("203.0.113.7"), Now));
        Assert.Null(b.Address(IPAddress.Parse("203.0.113.8"), Now));
    }

    [Theory]
    [InlineData("203.0.113.0/24", "203.0.113.0", true)]
    [InlineData("203.0.113.0/24", "203.0.113.255", true)]
    [InlineData("203.0.113.0/24", "203.0.114.0", false)]
    [InlineData("203.0.113.0/24", "203.0.112.255", false)]
    [InlineData("203.0.113.77/24", "203.0.113.1", true)]  // host bits in the network are ignored
    [InlineData("0.0.0.0/0", "198.51.100.1", true)]
    [InlineData("10.0.0.0/8", "11.0.0.0", false)]
    public void A_cidr_bans_its_range_and_nothing_beside_it(string cidr, string ip, bool banned) =>
        Assert.Equal(banned, With(Addr(cidr)).Address(IPAddress.Parse(ip), Now) is not null);

    [Fact]
    public void An_ipv4_mapped_address_matches_its_ipv4_ban() =>
        Assert.NotNull(With(Addr("203.0.113.7")).Address(IPAddress.Parse("203.0.113.7").MapToIPv6(), Now));

    [Theory]
    [InlineData("203.0.113.0/33")]
    [InlineData("203.0.113.0/-1")]
    [InlineData("not-an-ip")]
    [InlineData("2001:db8::1")]
    public void An_unparseable_address_ban_is_ignored_not_a_wildcard(string value)
    {
        Assert.False(BanList.TryParseCidr(value, out _, out _));
        Assert.Null(With(Addr(value)).Address(IPAddress.Parse("203.0.113.7"), Now));
    }

    [Fact]
    public void A_steamid_ban_bans_that_steamid_only()
    {
        var b = With(Sid(76561197960287930UL));
        Assert.NotNull(b.SteamId(76561197960287930UL, Now));
        Assert.Null(b.SteamId(76561197960287931UL, Now));
    }

    [Fact]
    public void A_ban_ends_at_its_expiry()
    {
        var b = With(Addr("203.0.113.7", Now.AddMinutes(10)), Sid(1, Now.AddMinutes(10)));
        Assert.NotNull(b.Address(IPAddress.Parse("203.0.113.7"), Now.AddMinutes(10).AddTicks(-1)));
        Assert.Null(b.Address(IPAddress.Parse("203.0.113.7"), Now.AddMinutes(10)));
        Assert.Null(b.SteamId(1, Now.AddMinutes(10)));
    }

    [Fact]
    public void Of_two_bans_on_one_steamid_the_longer_holds()
    {
        var b = With(Sid(1, Now.AddMinutes(1)), Sid(1));
        Assert.NotNull(b.SteamId(1, Now.AddYears(1)));
    }

    [Fact]
    public void A_local_temp_ban_holds_through_a_service_list_that_does_not_carry_it_yet()
    {
        var b = With(Addr("198.51.100.1"));
        b.AddLocal(Addr("203.0.113.7", Now.AddMinutes(10)));
        Assert.NotNull(b.Address(IPAddress.Parse("203.0.113.7"), Now));

        b.Replace([Addr("198.51.100.1")], Now.AddMinutes(1)); // SyncTable before the service wrote it

        Assert.NotNull(b.Address(IPAddress.Parse("203.0.113.7"), Now.AddMinutes(1)));
        Assert.NotNull(b.Address(IPAddress.Parse("198.51.100.1"), Now.AddMinutes(1)));
    }

    [Fact]
    public void A_service_list_without_an_admin_ban_lifts_it()
    {
        var b = With(Addr("203.0.113.7"));
        b.Replace([], Now);
        Assert.Null(b.Address(IPAddress.Parse("203.0.113.7"), Now));
    }
}
