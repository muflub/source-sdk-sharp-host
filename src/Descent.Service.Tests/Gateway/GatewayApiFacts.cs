using Descent.Service.Gateway;
using Descent.Service.Tests.Api;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.DependencyInjection;
using G = SourceSharp.Host.Proto.Gateway;

namespace Descent.Service.Tests.Gateway;

/// <summary>Plan §8.2 over real gRPC: the gateway's side of the service on the gateway listener.</summary>
public class GatewayApiFacts
{
    [Fact]
    public async Task Attaching_the_health_stream_syncs_the_table_and_pongs()
    {
        var control = new RecordingGatewayControl();
        await using var h = await ApiHarness.Start(b => b.Services.AddSingleton<IGatewayControl>(control));
        using var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{h.Service.Bound.Port(ListenerRole.GatewayApi)}");
        using var call = new G.GatewayHealth.GatewayHealthClient(channel).Attach();
        await call.RequestStream.WriteAsync(new G.Ping { GatewayId = "gw", TableVersion = (ulong)control.TableVersion, Seq = 1 });
        Assert.True(await call.ResponseStream.MoveNext());
        Assert.Equal((1UL, 1), (call.ResponseStream.Current.Seq, control.Syncs));
        await call.RequestStream.CompleteAsync();
    }

    [Fact]
    public async Task A_repeated_session_event_is_applied_once()
    {
        var control = new RecordingGatewayControl();
        await using var h = await ApiHarness.Start(b => b.Services.AddSingleton<IGatewayControl>(control));
        using var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{h.Service.Bound.Port(ListenerRole.GatewayApi)}");
        var events = new G.GatewayEvents.GatewayEventsClient(channel);
        var ev = new G.SessionEvent { RequestId = "same", SessionId = "s1", ClientAddr = "1.2.3.4:5", Backend = "10.0.0.1:5010", Peer = "127.1.0.1:29005" };
        await events.SessionOpenedAsync(ev);
        var first = await h.Data.ReadAsync((tx, _) => tx.Sessions.Get("s1"));
        await events.SessionOpenedAsync(Changed(ev));
        var second = await h.Data.ReadAsync((tx, _) => tx.Sessions.Get("s1"));
        Assert.Equal(first!.Peer, second!.Peer);
    }

    /// <summary>The same request id with a different peer: a replay must not apply it.</summary>
    static G.SessionEvent Changed(G.SessionEvent ev)
    {
        var copy = ev.Clone();
        copy.Peer = "127.1.0.99:29005";
        return copy;
    }

    [Fact]
    public async Task The_game_api_is_not_served_on_the_gateway_listener()
    {
        await using var h = await ApiHarness.Start();
        using var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{h.Service.Bound.Port(ListenerRole.GatewayApi)}");
        var e = await Assert.ThrowsAsync<RpcException>(() => new SourceSharp.Host.Proto.CharacterService.CharacterServiceClient(channel)
            .GetSheetAsync(new SourceSharp.Host.Proto.GetSheetRequest { CharacterId = "x" }).ResponseAsync);
        Assert.NotEqual(StatusCode.OK, e.StatusCode);
        Assert.NotEqual(StatusCode.Unauthenticated, e.StatusCode); // 404 → Unimplemented, never reached the auth interceptor
    }
}
