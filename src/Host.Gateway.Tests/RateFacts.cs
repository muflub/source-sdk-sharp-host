using System.Net;
using SourceSharp.Host.FakeClient;
using SourceSharp.Host.Gateway.Tests.Support;

namespace SourceSharp.Host.Gateway.Tests;

/// <summary>Gateway.HandshakesPerSecond per source IP (plan §8.1, Q24): the one limiter the sidecar design keeps.</summary>
public class RateFacts
{
    static readonly TimeSpan T = Rig.T;

    static int Challenges(Pod p) => p.Log.Count(r => r.Type == ToyWire.GetChallenge);

    [Fact]
    public async Task Getchallenges_beyond_the_rate_from_one_ip_are_dropped()
    {
        await using var rig = new Rig(o => { o.HandshakesPerSecond = 2; o.Wire = "Toy"; });
        var hub = rig.Backend("hub");
        rig.SetDefault(hub);
        var c = rig.Client(1);
        for (var i = 0; i < 3; i++) c.SendRaw(ToyWire.GetChallengePacket(i));
        Assert.True(await hub.WaitForAsync(_ => Challenges(hub) == 2, T));
        c.SendData([1]); // a later datagram from the same client does arrive: the third was dropped, not late
        Assert.True(await hub.WaitForAsync(l => l.Any(r => r.Kind == ToyWire.Data), T));
        Assert.Equal(2, Challenges(hub));
    }

    [Fact]
    public async Task The_rate_refills_over_a_second()
    {
        await using var rig = new Rig(o => { o.HandshakesPerSecond = 2; o.Wire = "Toy"; });
        var hub = rig.Backend("hub");
        rig.SetDefault(hub);
        var c = rig.Client(1);
        for (var i = 0; i < 3; i++) c.SendRaw(ToyWire.GetChallengePacket(i));
        Assert.True(await hub.WaitForAsync(_ => Challenges(hub) == 2, T));
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        c.SendRaw(ToyWire.GetChallengePacket(9));
        Assert.True(await hub.WaitForAsync(_ => Challenges(hub) == 3, T));
    }

    [Fact]
    public async Task Another_ip_has_its_own_rate()
    {
        await using var rig = new Rig(o => { o.HandshakesPerSecond = 1; o.Wire = "Toy"; });
        var hub = rig.Backend("hub");
        rig.SetDefault(hub);
        using var a = new FakeClient.FakeClient(rig.Relay.PublicEndPoint, 1, bind: IPAddress.Parse("127.0.0.2"));
        using var b = new FakeClient.FakeClient(rig.Relay.PublicEndPoint, 2, bind: IPAddress.Parse("127.0.0.3"));
        Assert.Equal("hub", await a.ConnectAsync(T));
        Assert.Equal("hub", await b.ConnectAsync(T));
    }
}
