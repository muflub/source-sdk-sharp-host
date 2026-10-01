using System.Diagnostics;
using System.Net;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Time.Testing;
using SourceSharp.Host.FakeClient;
using SourceSharp.Host.Proto.Relay;

namespace SourceSharp.Host.Relay.Tests;

/// <summary>PeerRelay and PeerInfo in-process, against a ToyBackend as the engine.</summary>
public class PeerRelayFacts
{
    static readonly TimeSpan T = TimeSpan.FromSeconds(5);

    static async Task<bool> Until(Func<bool> c)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < T) { if (c()) return true; await Task.Delay(5); }
        return c();
    }

    static RelayUp Open(string session, string steamid = "", string client = "10.0.0.1:27005") =>
        new() { Open = new Open { SessionId = session, ClientAddr = client, Steamid = steamid, TableVersion = 1 } };

    static RelayUp Data(byte[] b) => new() { Datagram = new Datagram { Payload = ByteString.CopyFrom(b) } };

    static async Task<RelayDown> Next(AsyncDuplexStreamingCall<RelayUp, RelayDown> call)
    {
        using var cts = new CancellationTokenSource(T);
        Assert.True(await call.ResponseStream.MoveNext(cts.Token), "stream ended");
        return call.ResponseStream.Current;
    }

    static async Task<IPEndPoint> OpenAsync(AsyncDuplexStreamingCall<RelayUp, RelayDown> call, RelayUp open)
    {
        await call.RequestStream.WriteAsync(open);
        var m = await Next(call);
        Assert.Equal(RelayDown.KindOneofCase.Opened, m.KindCase);
        return IPEndPoint.Parse(m.Opened.Peer);
    }

    [Fact]
    public async Task Open_answers_with_a_peer_from_the_pool()
    {
        using var engine = new ToyBackend("pod");
        await using var sc = new RelayHost(engine.EndPoint);
        using var call = sc.Relay.Relay();
        var peer = await OpenAsync(call, Open("s1"));
        Assert.True(IPNetwork.Parse(sc.Options.AddressPool).Contains(peer.Address));
    }

    [Fact]
    public async Task A_datagram_reaches_the_engine_from_the_peer_and_the_answer_comes_down()
    {
        using var engine = new ToyBackend("pod");
        await using var sc = new RelayHost(engine.EndPoint);
        using var call = sc.Relay.Relay();
        var peer = await OpenAsync(call, Open("s1"));
        await call.RequestStream.WriteAsync(Data(ToyWire.GetChallengePacket(42)));
        var down = await Next(call);
        Assert.True(ToyWire.TryReadChallenge(down.Datagram.Payload.ToByteArray(), out _, out var cc));
        Assert.Equal(42, cc);
        Assert.Equal(peer, Assert.Single(engine.Senders));
    }

    [Fact]
    public async Task The_first_message_must_be_open()
    {
        using var engine = new ToyBackend("pod");
        await using var sc = new RelayHost(engine.EndPoint);
        using var call = sc.Relay.Relay();
        await call.RequestStream.WriteAsync(Data([1, 2, 3]));
        var e = await Assert.ThrowsAsync<RpcException>(() => Next(call));
        Assert.Equal(StatusCode.InvalidArgument, e.StatusCode);
        Assert.Empty(engine.Log);
    }

    [Fact]
    public async Task Resolve_answers_who_is_behind_a_live_peer()
    {
        using var engine = new ToyBackend("pod");
        await using var sc = new RelayHost(engine.EndPoint);
        using var call = sc.Relay.Relay();
        var peer = await OpenAsync(call, Open("s1", "76561198000000001", "203.0.113.7:27005"));
        var e = await sc.Info.ResolveAsync(new ResolveRequest { Peer = peer.ToString() });
        Assert.Equal((true, "203.0.113.7:27005", "s1", "76561198000000001"), (e.Found, e.ClientAddr, e.SessionId, e.Steamid));
    }

    [Fact]
    public async Task Resolve_finds_nothing_after_the_stream_ends()
    {
        using var engine = new ToyBackend("pod");
        await using var sc = new RelayHost(engine.EndPoint);
        var call = sc.Relay.Relay();
        var peer = await OpenAsync(call, Open("s1", "76561198000000001"));
        Assert.True((await sc.Info.ResolveAsync(new ResolveRequest { Peer = peer.ToString() })).Found);
        await call.RequestStream.CompleteAsync();
        call.Dispose();
        Assert.True(await Until(() => sc.Table.LiveCount == 0));
        Assert.False((await sc.Info.ResolveAsync(new ResolveRequest { Peer = peer.ToString() })).Found);
    }

    [Fact]
    public async Task List_returns_every_live_peer()
    {
        using var engine = new ToyBackend("pod");
        await using var sc = new RelayHost(engine.EndPoint);
        using var a = sc.Relay.Relay();
        using var b = sc.Relay.Relay();
        await OpenAsync(a, Open("s1", "1"));
        await OpenAsync(b, Open("s2"));
        var list = await sc.Info.ListAsync(new ListRequest());
        Assert.Equal(["s1", "s2"], list.Peers.Select(p => p.SessionId).Order());
    }

    [Fact]
    public async Task A_second_stream_for_a_steamid_closes_the_first_as_superseded()
    {
        using var engine = new ToyBackend("pod");
        await using var sc = new RelayHost(engine.EndPoint);
        using var first = sc.Relay.Relay();
        using var second = sc.Relay.Relay();
        await OpenAsync(first, Open("s1", "76561198000000001"));
        await OpenAsync(second, Open("s2", "76561198000000001"));
        var m = await Next(first);
        Assert.Equal("superseded", m.Closed.Reason);
        Assert.Equal("s2", Assert.Single((await sc.Info.ListAsync(new ListRequest())).Peers).SessionId);
    }

    [Fact]
    public async Task An_idle_stream_is_closed()
    {
        using var engine = new ToyBackend("pod");
        var time = new FakeTimeProvider();
        await using var sc = new RelayHost(engine.EndPoint, time, o => o.IdleTimeout = TimeSpan.FromSeconds(40));
        using var call = sc.Relay.Relay();
        await OpenAsync(call, Open("s1"));
        time.Advance(TimeSpan.FromSeconds(50));
        Assert.Equal("idle", (await Next(call)).Closed.Reason);
        Assert.True(await Until(() => sc.Table.LiveCount == 0));
    }

    [Fact]
    public async Task Six_streams_reach_one_engine_from_six_addresses()
    {
        using var engine = new ToyBackend("pod");
        await using var sc = new RelayHost(engine.EndPoint);
        var calls = Enumerable.Range(1, 6).Select(_ => sc.Relay.Relay()).ToList();
        try
        {
            for (var i = 0; i < 6; i++)
            {
                await OpenAsync(calls[i], Open($"s{i}", i % 2 == 0 ? $"{76561198000000001UL + (ulong)i}" : ""));
                await calls[i].RequestStream.WriteAsync(Data(ToyWire.GetChallengePacket(i)));
            }
            Assert.True(await engine.WaitForAsync(l => l.Count == 6, T));
            Assert.Equal(6, engine.Senders.Select(p => p.Address).Distinct().Count());
            foreach (var c in calls) Assert.Equal(RelayDown.KindOneofCase.Datagram, (await Next(c)).KindCase); // none refused
        }
        finally { calls.ForEach(c => c.Dispose()); }
    }
}
