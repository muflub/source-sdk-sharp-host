using System.Net;
using Grpc.Core;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Proto.Relay;
using SourceSharp.Host.Relay;
using P = SourceSharp.Host.Proto;

namespace SourceSharp.Host.Sdk.Tests;

/// <summary>IPeers: the pod's sidecar first (live peers), the service second (history), cached per session.</summary>
public class PeerFacts
{
    static readonly IPEndPoint Engine = new(IPAddress.Loopback, 27999);

    static async Task<string> OpenSession(RelayHost sc, AsyncDuplexStreamingCall<RelayUp, RelayDown> call, string steamid)
    {
        await call.RequestStream.WriteAsync(new RelayUp { Open = new Open { SessionId = "sess-live", ClientAddr = "198.51.100.4:27005", Steamid = steamid, TableVersion = 1 } });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.True(await call.ResponseStream.MoveNext(cts.Token));
        return call.ResponseStream.Current.Opened.Peer;
    }

    [Fact]
    public async Task A_live_peer_is_resolved_by_the_sidecar()
    {
        await using var h = await SdkHarness.Start();
        await h.Instance("lvl-1", InstanceKind.Level, 3);
        await using var sc = new RelayHost(Engine);
        using var call = sc.Relay.Relay();
        var peer = await OpenSession(sc, call, "76561198000000009");
        var sdk = h.Sdk("lvl-1", sidecar: new Uri($"http://{sc.EndPoint}"));
        var r = await sdk.Pumped(sdk.Peers.Resolve(peer));
        Assert.Equal(new PeerIdentity(peer, "198.51.100.4:27005", "sess-live", "76561198000000009", PeerSource.Sidecar), r.Value);
        Assert.Equal(0, h.Sessions.Resolves);
    }

    [Fact]
    public async Task A_peer_the_sidecar_does_not_know_is_asked_of_the_service()
    {
        await using var h = await SdkHarness.Start();
        await h.Instance("lvl-1", InstanceKind.Level, 3);
        await using var sc = new RelayHost(Engine);
        h.Sessions.Known["127.9.9.9:27005"] = new P.ResolvePeerResponse { Found = true, SessionId = "old", ClientAddr = "203.0.113.9:1", Steamid = "7656" };
        var sdk = h.Sdk("lvl-1", sidecar: new Uri($"http://{sc.EndPoint}"));
        var r = await sdk.Pumped(sdk.Peers.Resolve("127.9.9.9:27005"));
        Assert.Equal((PeerSource.Service, "203.0.113.9:1", "old"), (r.Value.Source, r.Value.ClientAddr, r.Value.SessionId));
        Assert.Equal(1, h.Sessions.Resolves);
    }

    [Fact]
    public async Task An_unreachable_sidecar_falls_back_to_the_service()
    {
        await using var h = await SdkHarness.Start();
        await h.Instance("lvl-1", InstanceKind.Level, 3);
        h.Sessions.Known["127.9.9.9:27005"] = new P.ResolvePeerResponse { Found = true, SessionId = "old", ClientAddr = "203.0.113.9:1", Steamid = "7656" };
        var sdk = h.Sdk("lvl-1", sidecar: new Uri("http://127.0.0.1:1"));
        var r = await sdk.Pumped(sdk.Peers.Resolve("127.9.9.9:27005"));
        Assert.Equal(PeerSource.Service, r.Value.Source);
    }

    [Fact]
    public async Task A_resolved_peer_is_cached_for_its_session()
    {
        await using var h = await SdkHarness.Start();
        await h.Instance("lvl-1", InstanceKind.Level, 3);
        h.Sessions.Known["127.9.9.9:27005"] = new P.ResolvePeerResponse { Found = true, SessionId = "old", ClientAddr = "203.0.113.9:1", Steamid = "7656" };
        var sdk = h.Sdk("lvl-1");
        await sdk.Pumped(sdk.Peers.Resolve("127.9.9.9:27005"));
        await sdk.Pumped(sdk.Peers.Resolve("127.9.9.9:27005"));
        Assert.Equal(1, h.Sessions.Resolves);
        sdk.Peers.Forget("127.9.9.9:27005");
        await sdk.Pumped(sdk.Peers.Resolve("127.9.9.9:27005"));
        Assert.Equal(2, h.Sessions.Resolves);
    }

    [Fact]
    public async Task PlayerJoined_primes_the_cache_and_PlayerLeft_clears_it()
    {
        await using var h = await SdkHarness.Start();
        await h.Instance("hub-1", InstanceKind.Hub);
        var sdk = h.Sdk("hub-1");
        await sdk.Pumped(sdk.Session.PlayerJoined("127.0.4.2:27005", "7656"));
        var r = await sdk.Pumped(sdk.Peers.Resolve("127.0.4.2:27005"));
        Assert.Equal(("203.0.113.7:27005", 0), (r.Value.ClientAddr, h.Sessions.Resolves));
        await sdk.Pumped(sdk.Session.PlayerLeft("7656", "127.0.4.2:27005"));
        var after = await sdk.Pumped(sdk.Peers.Resolve("127.0.4.2:27005"));
        Assert.Equal(("unknown_peer", 1), (after.Refusal!.Reason, h.Sessions.Resolves));
    }
}
