using System.Net;
using Microsoft.Extensions.Time.Testing;

namespace SourceSharp.Host.Relay.Tests;

/// <summary>The per-instance address pool (the peer a session reaches the engine from).</summary>
public class PeerTableFacts
{
    static (PeerTable Table, FakeTimeProvider Time) New(string pool = "127.1.0.0/16")
    {
        var time = new FakeTimeProvider();
        return (new PeerTable(new RelayOptions { AddressPool = pool, PeerPort = 0 }, time), time);
    }

    [Fact]
    public void Six_concurrent_sessions_get_six_distinct_addresses()
    {
        var (t, _) = New();
        // Three identified, three not yet (a first connect to the hub): none may share.
        var leases = Enumerable.Range(1, 6).Select(i => t.Acquire($"s{i}", $"10.0.0.{i}:27005", i <= 3 ? (ulong)i : null, null).Lease).ToList();
        try { Assert.Equal(6, leases.Select(l => l.Peer.Address).Distinct().Count()); }
        finally { leases.ForEach(t.Release); }
    }

    [Fact]
    public void A_full_pool_refuses_a_new_session_rather_than_share_an_address()
    {
        var (t, _) = New("127.9.1.0/30"); // .1 .2 .3
        var live = Enumerable.Range(1, 3).Select(i => t.Acquire($"s{i}", $"10.0.0.{i}:1", null, null).Lease).ToList();
        try
        {
            Assert.Equal(3, live.Select(l => l.Peer.Address).Distinct().Count());
            Assert.Throws<InvalidOperationException>(() => t.Acquire("s4", "10.0.0.4:1", null, null));
        }
        finally { live.ForEach(t.Release); }
    }

    [Fact]
    public void Addresses_are_handed_out_in_order_from_the_pool()
    {
        var (t, _) = New("127.1.0.0/16");
        var a = t.Acquire("s1", "10.0.0.1:1", null, null).Lease;
        var b = t.Acquire("s2", "10.0.0.2:1", null, null).Lease;
        try { Assert.Equal(("127.1.0.1", "127.1.0.2"), (a.Peer.Address.ToString(), b.Peer.Address.ToString())); }
        finally { t.Release(a); t.Release(b); }
    }

    [Fact]
    public void A_steamid_reconnecting_gets_its_previous_address()
    {
        var (t, _) = New();
        var first = t.Acquire("s1", "10.0.0.1:1", 7UL, null).Lease;
        var address = first.Peer.Address;
        t.Release(first);
        var other = t.Acquire("s2", "10.0.0.2:1", 8UL, null).Lease; // someone else joins meanwhile
        var again = t.Acquire("s3", "10.0.0.1:2", 7UL, null).Lease;
        try
        {
            Assert.Equal(address, again.Peer.Address);
            Assert.NotEqual(address, other.Peer.Address);
        }
        finally { t.Release(other); t.Release(again); }
    }

    [Fact]
    public void A_players_address_is_kept_for_them_past_the_quarantine()
    {
        var (t, time) = New();
        var first = t.Acquire("s1", "10.0.0.1:1", 7UL, null).Lease;
        var address = first.Peer.Address;
        t.Release(first);
        time.Advance(TimeSpan.FromMinutes(10));
        var others = Enumerable.Range(0, 5).Select(i => t.Acquire($"o{i}", "10.0.0.9:1", null, null).Lease).ToList();
        try { Assert.DoesNotContain(address, others.Select(l => l.Peer.Address)); }
        finally { others.ForEach(t.Release); }
    }

    [Fact]
    public void A_session_without_a_steamid_gets_a_fresh_address()
    {
        var (t, _) = New();
        var a = t.Acquire("s1", "10.0.0.1:1", null, null).Lease;
        var address = a.Peer.Address;
        t.Release(a);
        var b = t.Acquire("s2", "10.0.0.1:1", null, null).Lease;
        try { Assert.NotEqual(address, b.Peer.Address); }
        finally { t.Release(b); }
    }

    [Fact]
    public void A_released_address_is_quarantined_before_another_player_gets_it()
    {
        // A /30 pool has three usable addresses (.1 .2 .3): the fourth player waits for the quarantine.
        var (t, time) = New("127.9.0.0/30");
        var a = t.Acquire("s1", "10.0.0.1:1", null, null).Lease;
        var b = t.Acquire("s2", "10.0.0.2:1", null, null).Lease;
        var c = t.Acquire("s3", "10.0.0.3:1", null, null).Lease;
        t.Release(a);
        Assert.Throws<InvalidOperationException>(() => t.Acquire("s4", "10.0.0.4:1", null, null));
        time.Advance(TimeSpan.FromSeconds(60));
        var d = t.Acquire("s4", "10.0.0.4:1", null, null).Lease;
        try { Assert.Equal(a.Peer.Address, d.Peer.Address); }
        finally { t.Release(b); t.Release(c); t.Release(d); }
    }

    [Fact]
    public void A_second_stream_for_a_steamid_cuts_the_first()
    {
        var (t, _) = New();
        var first = t.Acquire("s1", "10.0.0.1:1", 7UL, null).Lease;
        var (second, superseded) = t.Acquire("s2", "10.0.0.1:2", 7UL, null);
        try
        {
            Assert.Same(first, superseded);
            Assert.True(first.Cut.IsCancellationRequested);
            Assert.Equal("superseded", first.CutReason);
            Assert.Equal(first.Peer.Address, second.Peer.Address);
            Assert.Equal(1, t.LiveCount);
        }
        finally { t.Release(second); }
    }

    [Fact]
    public void A_hint_is_honoured_for_the_same_session_inside_the_quarantine()
    {
        var (t, _) = New();
        var a = t.Acquire("s1", "10.0.0.1:1", null, null).Lease;
        var peer = a.Peer;
        t.Release(a);
        var b = t.Acquire("s1", "10.0.0.1:1", null, peer).Lease;
        try { Assert.Equal(peer, b.Peer); }
        finally { t.Release(b); }
    }

    [Fact]
    public void A_hint_for_another_sessions_quarantined_address_is_ignored()
    {
        var (t, _) = New();
        var a = t.Acquire("s1", "10.0.0.1:1", null, null).Lease;
        var peer = a.Peer;
        t.Release(a);
        var b = t.Acquire("s2", "10.0.0.2:1", null, peer).Lease;
        try { Assert.NotEqual(peer.Address, b.Peer.Address); }
        finally { t.Release(b); }
    }

    [Fact]
    public void Resolve_with_port_zero_matches_the_address()
    {
        var (t, _) = New();
        var a = t.Acquire("s1", "10.0.0.1:1", 7UL, null).Lease;
        try { Assert.Same(a, t.Resolve(new IPEndPoint(a.Peer.Address, 0))); }
        finally { t.Release(a); }
    }

    [Fact]
    public void With_a_fixed_peer_port_a_returning_player_gets_the_same_ip_and_port()
    {
        var time = new FakeTimeProvider();
        var t = new PeerTable(new RelayOptions { AddressPool = "127.77.0.0/16", PeerPort = 29005 }, time);
        var a = t.Acquire("s1", "10.0.0.1:1", 7UL, null).Lease;
        var peer = a.Peer;
        t.Release(a);
        var b = t.Acquire("s2", "10.0.0.1:9", 7UL, null).Lease;
        try { Assert.Equal((peer.ToString(), 29005), (b.Peer.ToString(), b.Peer.Port)); }
        finally { t.Release(b); }
    }
}

public class RelayOptionsFacts
{
    [Fact]
    public void A_non_loopback_info_listen_is_refused()
    {
        var e = Assert.Throws<InvalidOperationException>(() => new RelayOptions { InfoListen = "0.0.0.0:5011" }.Validate());
        Assert.Contains("InfoListen", e.Message);
    }

    [Fact]
    public void The_default_options_are_accepted()
    {
        Assert.Empty(new RelayOptions().Problems());
    }

    [Fact]
    public void A_pool_containing_the_engines_own_address_is_refused()
    {
        Assert.Contains(new RelayOptions { AddressPool = "127.0.0.0/16" }.Problems(), p => p.Contains("127.0.0.1"));
    }

    [Fact]
    public void Hosting_the_relay_refuses_a_non_loopback_info_listen()
    {
        Assert.Throws<InvalidOperationException>(() => new Microsoft.Extensions.DependencyInjection.ServiceCollection().AddPeerRelay(new RelayOptions { InfoListen = "0.0.0.0:5011" }));
    }
}
