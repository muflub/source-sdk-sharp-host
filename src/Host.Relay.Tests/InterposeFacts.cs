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

    static RelayHost Host() => new(IPEndPoint.Parse("127.0.0.1:9"), configure: o => o.Mode = "Interpose");

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
