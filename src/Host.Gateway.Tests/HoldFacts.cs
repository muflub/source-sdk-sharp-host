using System.Buffers.Binary;
using SourceSharp.Host.FakeClient;
using SourceSharp.Host.Gateway.Relay;
using SourceSharp.Host.Gateway.Tests.Support;

namespace SourceSharp.Host.Gateway.Tests;

/// <summary>Holds: hold, never drop, bounded, released in order (plan §8.1b).</summary>
public class HoldFacts
{
    static readonly TimeSpan T = Rig.T;
    const string Sid = "76561198000000001";

    static byte[] Numbered(int n)
    {
        var b = ToyWire.DataPacket((uint)n, new byte[8]);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(5), n);
        return b;
    }

    [Fact]
    public async Task With_no_default_backend_a_new_client_is_held_and_released_to_the_hub()
    {
        await using var rig = new Rig();
        var hub = rig.Backend("hub");
        var c = rig.Client(1);
        c.StartHandshake();
        Assert.True(await Rig.Until(() => rig.Relay.HeldPackets == 1));
        rig.SetDefault(hub);
        Assert.Equal("hub", await c.WaitConnectedAsync(T));
    }

    [Fact]
    public async Task Packets_during_a_flip_reach_the_new_backend_once_in_order_after_ready_and_none_the_old()
    {
        await using var rig = new Rig();
        var a = rig.Backend("a");
        var b = rig.Backend("b");
        rig.SetDefault(a);
        var c = rig.Client(1);
        await rig.ConnectSettled(c, a); // else the post-accept keepalive lands on A after the snapshot
        Assert.Equal(ControlStatus.Ok, rig.Relay.SetRoute(rig.Next(), c.LocalEndPoint.ToString(), b.EndPoint.ToString(), Sid, holdUntilReady: true));
        var aBefore = a.Log.Count;
        a.SendRetry(rig.PeerOf(c));
        Assert.True(await Rig.Until(() => rig.Relay.HeldPackets == 1)); // the retry's getchallenge
        var seqs = new[] { c.SendData([1]), c.SendData([2]), c.SendData([3]) };
        Assert.True(await Rig.Until(() => rig.Relay.HeldPackets == 4));
        Assert.Empty(b.Log);

        Assert.Equal(ControlStatus.Ok, rig.Relay.BackendReady(rig.Next(), b.EndPoint.ToString(), true));
        Assert.True(await c.WaitForAsync(x => x.Connected && x.InstanceId == "b", T));
        var atB = b.Log;
        Assert.Equal(ToyWire.GetChallenge, atB[0].Type);
        Assert.Equal(seqs, atB.Skip(1).Take(3).Select(r => ToyWire.SeqOf(r.Bytes)));
        Assert.Equal(3, atB.Count(r => r.Kind == ToyWire.Data));
        Assert.Equal(aBefore, a.Log.Count); // nothing more reached A after the cut
    }

    [Fact]
    public async Task A_backend_already_marked_ready_is_not_held_for()
    {
        await using var rig = new Rig();
        var a = rig.Backend("a");
        var b = rig.Backend("b");
        rig.SetDefault(a);
        rig.Relay.BackendReady(rig.Next(), b.EndPoint.ToString(), true);
        var c = rig.Client(1);
        await c.ConnectAsync(T);
        rig.Relay.SetRoute(rig.Next(), c.LocalEndPoint.ToString(), b.EndPoint.ToString(), Sid, holdUntilReady: true);
        a.SendRetry(rig.PeerOf(c));
        Assert.True(await c.WaitForAsync(x => x.Connected && x.InstanceId == "b", T));
        Assert.Equal(0, rig.Relay.HeldPackets);
    }

    [Fact]
    public async Task The_65th_held_packet_evicts_the_oldest()
    {
        await using var rig = new Rig();
        var hub = rig.Backend("hub");
        var c = rig.Client(1);
        for (var i = 1; i <= 65; i++) c.SendRaw(Numbered(i));
        Assert.True(await Rig.Until(() => rig.Relay.Snapshot().SingleOrDefault()?.BytesIn == 65 * 13));
        Assert.Equal(64, rig.Relay.HeldPackets);
        rig.SetDefault(hub);
        Assert.True(await hub.WaitForAsync(l => l.Count == 64, T));
        Assert.Equal(Enumerable.Range(2, 64), hub.Log.Select(r => BinaryPrimitives.ReadInt32LittleEndian(r.Bytes.AsSpan(5))));
    }

    [Fact]
    public async Task Holding_past_the_byte_bound_evicts_the_oldest()
    {
        await using var rig = new Rig(o => o.HoldBytes = 13 * 10);
        var hub = rig.Backend("hub");
        var c = rig.Client(1);
        for (var i = 1; i <= 12; i++) c.SendRaw(Numbered(i));
        Assert.True(await Rig.Until(() => rig.Relay.Snapshot().SingleOrDefault()?.BytesIn == 12 * 13));
        Assert.Equal(10, rig.Relay.HeldPackets);
        rig.SetDefault(hub);
        Assert.True(await hub.WaitForAsync(l => l.Count == 10, T));
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(hub.Log[0].Bytes.AsSpan(5)));
    }

    [Fact]
    public async Task A_hold_past_the_hold_time_closes_the_session_with_the_reason()
    {
        await using var rig = new Rig();
        var c = rig.Client(1);
        c.StartHandshake();
        Assert.True(await Rig.Until(() => rig.Relay.HeldPackets == 1));
        rig.Time.Advance(rig.Options.HoldTime);
        Assert.Equal(0, rig.Relay.SessionCount);
        Assert.Equal("hold expired: no default backend", Assert.Single(rig.Events.Of("closed")).Reason);
    }

    [Fact]
    public async Task A_hold_within_the_hold_time_is_kept()
    {
        await using var rig = new Rig();
        var c = rig.Client(1);
        c.StartHandshake();
        Assert.True(await Rig.Until(() => rig.Relay.HeldPackets == 1));
        rig.Time.Advance(rig.Options.HoldTime - TimeSpan.FromSeconds(1));
        Assert.Equal(1, rig.Relay.SessionCount);
        Assert.Equal("holding", rig.Relay.SessionOf(c.LocalEndPoint)!.State);
    }
}
