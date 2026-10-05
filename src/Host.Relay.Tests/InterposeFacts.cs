using System.Diagnostics;
using System.Net;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using SourceSharp.Host.FakeClient;
using SourceSharp.Host.Proto.Relay;

namespace SourceSharp.Host.Relay.Tests;

/// <summary>The switchboard alone: the queue the engine's recvfrom drains and the addresses sendto may reach.</summary>
public class InterposeSwitchboardFacts
{
    static readonly IPEndPoint A = IPEndPoint.Parse("203.0.113.7:27005"), B = IPEndPoint.Parse("198.51.100.9:50000");

    [Fact]
    public void A_clients_datagram_reaches_the_engine_with_its_real_address()
    {
        var sb = new InterposeSwitchboard();
        var (s, _) = sb.Register(A, "s1", null, DateTimeOffset.UnixEpoch, _ => true);
        sb.FromClient(s, [1, 2, 3]);
        Assert.True(sb.TryTakeForEngine(out var from, out var payload));
        Assert.Equal(A, InterposeSwitchboard.EndPointOf(from));
        Assert.Equal([1, 2, 3], payload);
    }

    [Fact]
    public void Datagrams_reach_the_engine_in_arrival_order()
    {
        var sb = new InterposeSwitchboard();
        var (a, _) = sb.Register(A, "s1", null, DateTimeOffset.UnixEpoch, _ => true);
        var (b, _) = sb.Register(B, "s2", null, DateTimeOffset.UnixEpoch, _ => true);
        sb.FromClient(a, [1]); sb.FromClient(b, [2]); sb.FromClient(a, [3]);
        var seen = new List<byte>();
        while (sb.TryTakeForEngine(out _, out var p)) seen.Add(p[0]);
        Assert.Equal([1, 2, 3], seen);
    }

    [Fact]
    public void The_engine_sending_to_a_live_client_goes_down_its_stream()
    {
        var sb = new InterposeSwitchboard();
        var got = new List<byte[]>();
        sb.Register(A, "s1", null, DateTimeOffset.UnixEpoch, b => { got.Add(b); return true; });
        Assert.True(sb.ToClient(InterposeSwitchboard.KeyOf(A), [9, 9]));
        Assert.Equal([9, 9], Assert.Single(got));
    }

    [Fact]
    public void The_engine_sending_to_an_unknown_address_is_not_taken()
    {
        var sb = new InterposeSwitchboard();
        sb.Register(A, "s1", null, DateTimeOffset.UnixEpoch, _ => true);
        Assert.False(sb.ToClient(InterposeSwitchboard.KeyOf(B), [9]));
    }

    [Fact]
    public void After_unregister_the_address_is_unknown()
    {
        var sb = new InterposeSwitchboard();
        var (s, _) = sb.Register(A, "s1", null, DateTimeOffset.UnixEpoch, _ => true);
        sb.Unregister(s);
        Assert.False(sb.ToClient(InterposeSwitchboard.KeyOf(A), [9]));
    }

    [Fact]
    public void A_second_stream_for_a_steamid_supersedes_the_first()
    {
        var sb = new InterposeSwitchboard();
        var (first, _) = sb.Register(A, "s1", 7UL, DateTimeOffset.UnixEpoch, _ => true);
        var (_, superseded) = sb.Register(B, "s2", 7UL, DateTimeOffset.UnixEpoch, _ => true);
        Assert.Same(first, superseded);
        Assert.True(first.Cut.IsCancellationRequested);
        Assert.False(sb.ToClient(InterposeSwitchboard.KeyOf(A), [1]));
    }

    [Fact]
    public void Concurrent_producers_lose_nothing()
    {
        var sb = new InterposeSwitchboard();
        var sessions = Enumerable.Range(0, 8).Select(i => sb.Register(new IPEndPoint(IPAddress.Parse($"10.0.0.{i + 1}"), 27005), $"s{i}", null, DateTimeOffset.UnixEpoch, _ => true).Session).ToList();
        Parallel.ForEach(sessions, s => { for (var n = 0; n < 5000; n++) sb.FromClient(s, [1]); });
        var count = 0;
        while (sb.TryTakeForEngine(out _, out _)) count++;
        Assert.Equal(8 * 5000, count);
    }
}

/// <summary>PeerRelay and PeerInfo in Interpose mode (no loopback socket).</summary>
public class InterposeRelayFacts
{
    static readonly TimeSpan T = TimeSpan.FromSeconds(5);

    /// <summary>Interpose with the engine seeing real addresses (InterposeAddresses = Real): what these facts prove.
    /// The default, synthetic addresses, is <see cref="InterposeSyntheticFacts"/>.</summary>
    static RelayHost Host() => new(IPEndPoint.Parse("127.0.0.1:9"), configure: o => { o.Mode = "Interpose"; o.InterposeAddresses = "Real"; });

    static InterposeSwitchboard Board(RelayHost h) => h.Services.GetRequiredService<InterposeSwitchboard>();

    static async Task<RelayDown> Next(AsyncDuplexStreamingCall<RelayUp, RelayDown> call)
    {
        using var cts = new CancellationTokenSource(T);
        Assert.True(await call.ResponseStream.MoveNext(cts.Token), "stream ended");
        return call.ResponseStream.Current;
    }

    static RelayUp Open(string session, string client, string steamid = "") =>
        new() { Open = new Open { SessionId = session, ClientAddr = client, Steamid = steamid } };

    static async Task<bool> Until(Func<bool> c)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < T) { if (c()) return true; await Task.Delay(5); }
        return c();
    }

    [Fact]
    public async Task Opened_reports_the_clients_real_address_as_the_peer()
    {
        await using var h = Host();
        using var call = h.Relay.Relay();
        await call.RequestStream.WriteAsync(Open("s1", "203.0.113.7:27005"));
        Assert.Equal("203.0.113.7:27005", (await Next(call)).Opened.Peer);
    }

    [Fact]
    public async Task A_datagram_up_waits_for_the_engine_with_the_real_address()
    {
        await using var h = Host();
        using var call = h.Relay.Relay();
        await call.RequestStream.WriteAsync(Open("s1", "203.0.113.7:27005"));
        await Next(call);
        await call.RequestStream.WriteAsync(new RelayUp { Datagram = new Datagram { Payload = ByteString.CopyFrom(ToyWire.GetChallengePacket(1)) } });
        var board = Board(h);
        Assert.True(await Until(() => board.Queued == 1));
        Assert.True(board.TryTakeForEngine(out var from, out _));
        Assert.Equal("203.0.113.7:27005", InterposeSwitchboard.EndPointOf(from).ToString());
    }

    [Fact]
    public async Task What_the_engine_sends_to_the_client_comes_down_the_stream()
    {
        await using var h = Host();
        using var call = h.Relay.Relay();
        await call.RequestStream.WriteAsync(Open("s1", "203.0.113.7:27005"));
        await Next(call);
        Assert.True(Board(h).ToClient(InterposeSwitchboard.KeyOf(IPEndPoint.Parse("203.0.113.7:27005")), [4, 2]));
        Assert.Equal([4, 2], (await Next(call)).Datagram.Payload.ToByteArray());
    }

    [Fact]
    public async Task PeerInfo_resolves_the_real_address_while_the_stream_lives_and_not_after()
    {
        await using var h = Host();
        var call = h.Relay.Relay();
        await call.RequestStream.WriteAsync(Open("s1", "203.0.113.7:27005", "76561198000000001"));
        await Next(call);
        var e = await h.Info.ResolveAsync(new ResolveRequest { Peer = "203.0.113.7:27005" });
        Assert.Equal((true, "203.0.113.7:27005", "76561198000000001"), (e.Found, e.ClientAddr, e.Steamid));
        await call.RequestStream.CompleteAsync();
        call.Dispose();
        Assert.True(await Until(() => Board(h).LiveCount == 0));
        Assert.False((await h.Info.ResolveAsync(new ResolveRequest { Peer = "203.0.113.7:27005" })).Found);
    }

    [Fact]
    public async Task No_loopback_socket_is_taken_in_interpose_mode()
    {
        await using var h = Host();
        using var call = h.Relay.Relay();
        await call.RequestStream.WriteAsync(Open("s1", "203.0.113.7:27005"));
        await Next(call);
        Assert.Equal((0, 1), (h.Table.LiveCount, Board(h).LiveCount));
    }
}

/// <summary>
/// Interpose with synthetic addresses, the default (plan_steam_auth S-6 B): the engine sees a per-player loopback
/// address it accepts under <c>sv_lan 1</c>, never the client's public one; PeerInfo maps it back to the real
/// address and the SteamID the gateway validated (S-7).
/// </summary>
public class InterposeSyntheticFacts
{
    static readonly TimeSpan T = TimeSpan.FromSeconds(5);
    static RelayHost Host() => new(IPEndPoint.Parse("127.0.0.1:9"), configure: o => { o.Mode = "Interpose"; o.PeerPort = 29005; });
    static InterposeSwitchboard Board(RelayHost h) => h.Services.GetRequiredService<InterposeSwitchboard>();

    static async Task<RelayDown> Next(AsyncDuplexStreamingCall<RelayUp, RelayDown> call)
    {
        using var cts = new CancellationTokenSource(T);
        Assert.True(await call.ResponseStream.MoveNext(cts.Token), "stream ended");
        return call.ResponseStream.Current;
    }

    static RelayUp Open(string session, string client, string steamid = "", string hint = "") =>
        new() { Open = new Open { SessionId = session, ClientAddr = client, Steamid = steamid, PeerHint = hint } };

    /// <summary>Opens a stream, returns the peer it was given, and ends it (waiting until the board lets it go).</summary>
    static async Task<string> OpenAndEnd(RelayHost h, RelayUp open)
    {
        using var call = h.Relay.Relay();
        await call.RequestStream.WriteAsync(open);
        var peer = (await Next(call)).Opened.Peer;
        await call.RequestStream.CompleteAsync();
        var sw = Stopwatch.StartNew();
        while (Board(h).LiveCount > 0 && sw.Elapsed < T) await Task.Delay(5);
        Assert.Equal(0, Board(h).LiveCount);
        return peer;
    }

    [Fact]
    public async Task A_resumed_session_gets_the_peer_its_hint_names()
    {
        // Live at fd92fd3 (real client): the stream opened at the getchallenge, before Steam, so without a SteamID;
        // after a gateway restart the carried session reopened with its SteamID and the hint, and was given the next
        // address, a stranger to the engine.
        await using var h = Host();
        var first = await OpenAndEnd(h, Open("s1", "203.0.113.7:27005"));
        using var again = h.Relay.Relay();
        await again.RequestStream.WriteAsync(Open("s1", "203.0.113.7:27005", "76561198000000001", hint: first));
        Assert.Equal(first, (await Next(again)).Opened.Peer);
    }

    [Fact]
    public async Task The_hint_wins_over_the_address_the_steamid_had_before()
    {
        await using var h = Host();
        var older = await OpenAndEnd(h, Open("s0", "203.0.113.7:27005", "76561198000000001"));
        var current = await OpenAndEnd(h, Open("s1", "203.0.113.7:40000")); // its rejoin, opened before Steam
        Assert.NotEqual(older, current);
        using var again = h.Relay.Relay();
        await again.RequestStream.WriteAsync(Open("s1", "203.0.113.7:40000", "76561198000000001", hint: current));
        Assert.Equal(current, (await Next(again)).Opened.Peer);
    }

    [Fact]
    public async Task A_hint_naming_another_players_address_is_not_taken()
    {
        await using var h = Host();
        var theirs = await OpenAndEnd(h, Open("s0", "198.51.100.9:50000", "76561198000000002"));
        using var call = h.Relay.Relay();
        await call.RequestStream.WriteAsync(Open("s1", "203.0.113.7:27005", "76561198000000001", hint: theirs));
        Assert.NotEqual(theirs, (await Next(call)).Opened.Peer);
    }

    [Fact]
    public void Synthetic_is_the_default_in_interpose_mode() =>
        Assert.True(new RelayOptions { Mode = "Interpose" }.SyntheticAddresses);

    [Fact]
    public async Task The_engine_sees_a_loopback_peer_never_the_public_address()
    {
        await using var h = Host();
        using var call = h.Relay.Relay();
        await call.RequestStream.WriteAsync(Open("s1", "203.0.113.7:27005"));
        var peer = IPEndPoint.Parse((await Next(call)).Opened.Peer);
        Assert.True(IPAddress.IsLoopback(peer.Address), $"peer {peer}");
        Assert.NotEqual(IPAddress.Loopback, peer.Address);
        Assert.Equal(29005, peer.Port);

        await call.RequestStream.WriteAsync(new RelayUp { Datagram = new Datagram { Payload = ByteString.CopyFrom([1]) } });
        var board = Board(h);
        var sw = Stopwatch.StartNew();
        while (board.Queued == 0 && sw.Elapsed < T) await Task.Delay(5);
        Assert.True(board.TryTakeForEngine(out var from, out _));
        Assert.Equal(peer, InterposeSwitchboard.EndPointOf(from)); // what recvfrom writes: the synthetic peer
    }

    [Fact]
    public async Task The_engine_replying_to_the_synthetic_peer_reaches_that_clients_stream_only()
    {
        await using var h = Host();
        using var a = h.Relay.Relay();
        using var b = h.Relay.Relay();
        await a.RequestStream.WriteAsync(Open("s1", "203.0.113.7:27005"));
        await b.RequestStream.WriteAsync(Open("s2", "198.51.100.9:50000"));
        var pa = IPEndPoint.Parse((await Next(a)).Opened.Peer);
        var pb = IPEndPoint.Parse((await Next(b)).Opened.Peer);
        Assert.NotEqual(pa, pb);

        Assert.True(Board(h).ToClient(InterposeSwitchboard.KeyOf(pb), [7]));
        Assert.False(Board(h).ToClient(InterposeSwitchboard.KeyOf(IPEndPoint.Parse("198.51.100.9:50000")), [8])); // not the real address
        Assert.Equal([7], (await Next(b)).Datagram.Payload.ToByteArray());
    }

    [Fact]
    public async Task PeerInfo_maps_the_synthetic_peer_to_the_real_address_and_the_validated_steamid()
    {
        await using var h = Host();
        using var call = h.Relay.Relay();
        await call.RequestStream.WriteAsync(Open("s1", "203.0.113.7:27005", "76561198000000001"));
        var peer = (await Next(call)).Opened.Peer;
        var e = await h.Info.ResolveAsync(new ResolveRequest { Peer = peer });
        Assert.Equal((true, peer, "203.0.113.7:27005", "76561198000000001"), (e.Found, e.Peer, e.ClientAddr, e.Steamid));
    }

    [Fact]
    public async Task A_steamid_back_on_this_instance_gets_its_synthetic_peer_again()
    {
        await using var h = Host();
        string first;
        using (var call = h.Relay.Relay())
        {
            await call.RequestStream.WriteAsync(Open("s1", "203.0.113.7:27005", "76561198000000001"));
            first = (await Next(call)).Opened.Peer;
            await call.RequestStream.CompleteAsync();
        }
        var sw = Stopwatch.StartNew();
        while (Board(h).LiveCount > 0 && sw.Elapsed < T) await Task.Delay(5);
        using var again = h.Relay.Relay();
        await again.RequestStream.WriteAsync(Open("s2", "203.0.113.7:40000", "76561198000000001"));  // a new client port
        Assert.Equal(first, (await Next(again)).Opened.Peer);
    }
}
