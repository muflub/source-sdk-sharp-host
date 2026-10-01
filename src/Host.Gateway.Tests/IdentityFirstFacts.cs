using SourceSharp.Host.FakeClient;
using SourceSharp.Host.Gateway.Events;
using SourceSharp.Host.Gateway.Tests.Support;
using SourceSharp.Host.Proto.Gateway;

namespace SourceSharp.Host.Gateway.Tests;

/// <summary>The identity-first handshake over the toy wire (plan §8.1b).</summary>
public class IdentityFirstFacts
{
    static readonly TimeSpan T = Rig.T;
    const ulong Sid = 76561198000000001UL;

    static Rig IdentityFirstRig(Func<Microsoft.Extensions.Time.Testing.FakeTimeProvider, IGatewayEvents>? events = null) =>
        new(o => { o.IdentityFirst = true; o.Wire = "Toy"; }, events);

    [Fact]
    public async Task Identify_is_called_before_any_backend_is_touched()
    {
        await using var rig = IdentityFirstRig();
        var hub = rig.Backend("hub");
        rig.SetDefault(hub);
        var gate = new TaskCompletionSource<IdentifyResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Events.OnIdentify = _ => gate.Task;
        var c = rig.Client(Sid);
        c.StartHandshake();
        Assert.True(await Rig.Until(() => !rig.Events.Identifies.IsEmpty));
        Assert.Equal(Sid.ToString(), Assert.Single(rig.Events.Identifies).Steamid);
        Assert.Empty(hub.Log);

        gate.SetResult(new IdentifyResponse { Allowed = true });
        Assert.Equal("hub", await c.WaitConnectedAsync(T));
        Assert.NotEmpty(hub.Log); // the stimulus did reach the hub once identified
    }

    [Fact]
    public async Task The_first_packet_a_backend_sees_is_the_clients_own_getchallenge_and_never_the_gateway_challenged_connect()
    {
        await using var rig = IdentityFirstRig();
        var hub = rig.Backend("hub");
        rig.SetDefault(hub);
        var c = rig.Client(Sid);
        Assert.Equal("hub", await c.ConnectAsync(T));
        var log = hub.Log;
        Assert.Equal(ToyWire.GetChallenge, log[0].Type);
        Assert.Single(log, r => r.Type == ToyWire.Connect);
        Assert.Equal(Sid, hub.Accepted.Single().Value);
        Assert.Equal(["q", "A", "k", "A", "k", "B hub"], c.Steps); // re-challenged once, by the backend
    }

    [Fact]
    public async Task Identify_routes_a_returning_player_to_its_instance()
    {
        await using var rig = IdentityFirstRig();
        var hub = rig.Backend("hub");
        var level = rig.Backend("level");
        rig.SetDefault(hub);
        rig.Events.OnIdentify = _ => Task.FromResult(new IdentifyResponse { Allowed = true, Backend = level.EndPoint.ToString() });
        var c = rig.Client(Sid);
        Assert.Equal("level", await c.ConnectAsync(T));
        Assert.Empty(hub.Log);
    }

    [Fact]
    public async Task A_refused_identify_rejects_the_client_and_touches_no_backend()
    {
        await using var rig = IdentityFirstRig();
        var hub = rig.Backend("hub");
        rig.SetDefault(hub);
        rig.Events.OnIdentify = _ => Task.FromResult(new IdentifyResponse { Allowed = false, Reason = "already playing" });
        var c = rig.Client(Sid);
        c.StartHandshake();
        Assert.True(await c.WaitForAsync(x => x.RejectReason is not null, T));
        Assert.Equal("already playing", c.RejectReason);
        Assert.Empty(hub.Log);
        Assert.Equal(0, rig.Relay.SessionCount);
    }

    [Fact]
    public async Task A_silent_session_named_by_identify_is_taken_over()
    {
        await using var rig = IdentityFirstRig();
        var hub = rig.Backend("hub");
        rig.SetDefault(hub);
        var old = rig.Client(Sid);
        await old.ConnectAsync(T);
        var oldId = rig.Relay.SessionOf(old.LocalEndPoint)!.SessionId;
        rig.Events.OnIdentify = _ => Task.FromResult(new IdentifyResponse { Allowed = true, CloseSessions = { oldId } });
        var c = rig.Client(Sid);
        Assert.Equal("hub", await c.ConnectAsync(T));
        Assert.Null(rig.Relay.SessionOf(old.LocalEndPoint));
        Assert.Equal("taken over", Assert.Single(rig.Events.Of("closed")).Reason);
    }

    [Fact]
    public async Task A_second_live_session_for_a_steamid_is_refused()
    {
        await using var rig = IdentityFirstRig();
        var hub = rig.Backend("hub");
        rig.SetDefault(hub);
        await rig.Client(Sid).ConnectAsync(T);
        var c = rig.Client(Sid);
        c.StartHandshake();
        Assert.True(await c.WaitForAsync(x => x.RejectReason is not null, T));
        Assert.Equal("already connected", c.RejectReason);
        Assert.Single(hub.Accepted);
    }

    [Fact]
    public async Task An_unreadable_ticket_falls_back_to_the_default_backend_unidentified()
    {
        await using var rig = IdentityFirstRig();
        var hub = rig.Backend("hub");
        var level = rig.Backend("level");
        rig.SetDefault(hub);
        rig.Events.OnIdentify = _ => Task.FromResult(new IdentifyResponse { Allowed = true, Backend = level.EndPoint.ToString() });
        var c = new FakeClient.FakeClient(rig.Relay.PublicEndPoint, Sid) { UnreadableTicket = true };
        try
        {
            Assert.Equal("hub", await c.ConnectAsync(T));
            Assert.Empty(rig.Events.Identifies);
            Assert.Equal("", rig.Relay.SessionOf(c.LocalEndPoint)!.Steamid);
        }
        finally { c.Dispose(); }
    }

    [Fact]
    public async Task Identify_goes_to_the_service_over_grpc()
    {
        await using var svc = new FakeService();
        GrpcGatewayEvents? ev = null;
        await using var rig = IdentityFirstRig(t => ev = new GrpcGatewayEvents(svc.EventsClient, t));
        var hub = rig.Backend("hub");
        rig.SetDefault(hub);
        Assert.Equal("hub", await rig.Client(Sid).ConnectAsync(T));
        var req = Assert.Single(svc.Identifies);
        Assert.Equal((Sid.ToString(), 32), (req.Steamid, req.RequestId.Length));
        await ev!.DisposeAsync();
    }

    [Fact]
    public async Task With_identity_first_off_the_getchallenge_goes_straight_to_the_backend()
    {
        await using var rig = new Rig(o => o.Wire = "Toy");
        var hub = rig.Backend("hub");
        rig.SetDefault(hub);
        var c = rig.Client(Sid);
        Assert.Equal("hub", await c.ConnectAsync(T));
        Assert.Empty(rig.Events.Identifies);
        Assert.Equal(["q", "A", "k", "B hub"], c.Steps);
    }
}
