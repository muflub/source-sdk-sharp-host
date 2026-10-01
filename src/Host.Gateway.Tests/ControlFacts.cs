using Grpc.Core;
using Microsoft.Extensions.Time.Testing;
using SourceSharp.Host.Gateway.Control;
using SourceSharp.Host.Gateway.Events;
using SourceSharp.Host.Gateway.Relay;
using SourceSharp.Host.Gateway.Tests.Support;
using SourceSharp.Host.Proto.Gateway;

namespace SourceSharp.Host.Gateway.Tests;

/// <summary>GatewayControl over gRPC (plan §8.2).</summary>
public class ControlFacts
{
    static readonly TimeSpan T = Rig.T;

    [Fact]
    public async Task A_fresh_set_route_is_acknowledged_with_its_version()
    {
        await using var rig = new Rig();
        await using var host = new ControlHost(rig.Relay);
        var ack = await host.Client.SetDefaultAsync(new SetDefaultRequest { Version = 3, Backend = "127.0.0.1:27015" });
        Assert.Equal(3UL, ack.TableVersion);
    }

    [Fact]
    public async Task A_stale_set_route_is_failed_precondition()
    {
        await using var rig = new Rig();
        await using var host = new ControlHost(rig.Relay);
        await host.Client.SetDefaultAsync(new SetDefaultRequest { Version = 3, Backend = "127.0.0.1:27015" });
        var e = await Assert.ThrowsAsync<RpcException>(async () =>
            await host.Client.SetRouteAsync(new SetRouteRequest { Version = 3, ClientAddr = "127.0.0.1:5000", Backend = "127.0.0.1:27015" }));
        Assert.Equal(StatusCode.FailedPrecondition, e.StatusCode);
    }

    [Fact]
    public async Task A_stale_close_session_is_failed_precondition()
    {
        await using var rig = new Rig();
        await using var host = new ControlHost(rig.Relay);
        await host.Client.SetDefaultAsync(new SetDefaultRequest { Version = 3, Backend = "127.0.0.1:27015" });
        var e = await Assert.ThrowsAsync<RpcException>(async () =>
            await host.Client.CloseSessionAsync(new CloseSessionRequest { Version = 2, ClientAddr = "127.0.0.1:5000" }));
        Assert.Equal(StatusCode.FailedPrecondition, e.StatusCode);
    }

    [Fact]
    public async Task A_level_route_for_an_unidentified_session_is_permission_denied()
    {
        await using var rig = new Rig();
        await using var host = new ControlHost(rig.Relay);
        await host.Client.SetDefaultAsync(new SetDefaultRequest { Version = 1, Backend = "127.0.0.1:27015" });
        var e = await Assert.ThrowsAsync<RpcException>(async () =>
            await host.Client.SetRouteAsync(new SetRouteRequest { Version = 2, ClientAddr = "127.0.0.1:5000", Backend = "127.0.0.1:27016" }));
        Assert.Equal(StatusCode.PermissionDenied, e.StatusCode);
    }

    [Fact]
    public async Task Snapshot_lists_the_live_sessions()
    {
        await using var rig = new Rig();
        await using var host = new ControlHost(rig.Relay);
        var hub = rig.Backend("hub");
        rig.SetDefault(hub);
        var c = rig.Client(1);
        await c.ConnectAsync(T);
        var snap = await host.Client.SnapshotAsync(new SnapshotRequest());
        var s = Assert.Single(snap.Sessions);
        Assert.Equal((c.LocalEndPoint.ToString(), hub.EndPoint.ToString()), (s.ClientAddr, s.Backend));
    }
}

/// <summary>GatewayEvents: request ids, retries, order, stats (plan §8.2).</summary>
public class EventsFacts
{
    static readonly TimeSpan T = Rig.T;

    static async Task<bool> AdvanceUntil(FakeTimeProvider time, TimeSpan step, Func<bool> condition) =>
        await Rig.Until(() => { if (condition()) return true; time.Advance(step); return condition(); });

    [Fact]
    public async Task A_new_session_reaches_the_service_with_a_request_id()
    {
        await using var svc = new FakeService();
        GrpcGatewayEvents? ev = null;
        await using var rig = new Rig(events: t => ev = new GrpcGatewayEvents(svc.EventsClient, t));
        var hub = rig.Backend("hub");
        rig.SetDefault(hub);
        await rig.Client(1).ConnectAsync(T);
        Assert.True(await Rig.Until(() => !svc.Sessions.IsEmpty));
        var (kind, e) = Assert.Single(svc.Sessions);
        Assert.Equal("opened", kind);
        Assert.Equal(32, e.RequestId.Length);
        await ev!.DisposeAsync();
    }

    [Fact]
    public async Task A_failed_event_is_retried_with_the_same_request_id()
    {
        await using var svc = new FakeService { FailNext = 2 };
        GrpcGatewayEvents? ev = null;
        await using var rig = new Rig(events: t => ev = new GrpcGatewayEvents(svc.EventsClient, t));
        var hub = rig.Backend("hub");
        rig.SetDefault(hub);
        await rig.Client(1).ConnectAsync(T);
        // Retry delays run on the gateway's clock; the sweep's expiry is far past these steps.
        Assert.True(await AdvanceUntil(rig.Time, TimeSpan.FromMilliseconds(500), () => !svc.Sessions.IsEmpty));
        Assert.Equal(3, svc.Attempts.Count);
        Assert.Single(svc.Attempts.Select(a => a.RequestId).Distinct());
        Assert.Equal(2, ev!.Retries);
        await ev.DisposeAsync();
    }

    [Fact]
    public async Task Events_arrive_in_the_order_they_happened()
    {
        await using var svc = new FakeService();
        GrpcGatewayEvents? ev = null;
        await using var rig = new Rig(events: t => ev = new GrpcGatewayEvents(svc.EventsClient, t));
        var hub = rig.Backend("hub");
        rig.SetDefault(hub);
        var c = rig.Client(1);
        await c.ConnectAsync(T);
        rig.Relay.CloseSession(rig.Next(), c.LocalEndPoint.ToString(), "kicked");
        Assert.True(await Rig.Until(() => svc.Sessions.Count == 2));
        Assert.Equal(["opened", "closed"], svc.Sessions.Select(s => s.Kind));
        await ev!.DisposeAsync();
    }

    [Fact]
    public async Task Stats_are_reported_every_stats_interval()
    {
        await using var svc = new FakeService();
        GrpcGatewayEvents? ev = null;
        await using var rig = new Rig(events: t => ev = new GrpcGatewayEvents(svc.EventsClient, t));
        var hub = rig.Backend("hub");
        rig.SetDefault(hub);
        await rig.Client(1).ConnectAsync(T);
        Assert.Empty(svc.Stats);
        rig.Time.Advance(rig.Options.StatsInterval);
        Assert.True(await Rig.Until(() => !svc.Stats.IsEmpty));
        var s = Assert.Single(svc.Stats);
        Assert.Equal(1, s.Sessions);
        Assert.True(s.PacketsIn >= 3);
        await ev!.DisposeAsync();
    }
}

/// <summary>GatewayHealth.Attach: the one stream (plan §8.2, Q5).</summary>
public class HealthFacts
{
    static readonly TimeSpan Ping = TimeSpan.FromSeconds(2);

    sealed class Harness : IAsyncDisposable
    {
        public FakeService Svc { get; } = new();
        public FakeTimeProvider Time { get; } = new();
        public ulong Version = 5;
        public int Sessions = 2;
        public GatewayHealthClient Client { get; }
        readonly CancellationTokenSource _stop = new();
        readonly Task _run;

        public Harness()
        {
            Client = new GatewayHealthClient(Svc.HealthClient, () => (Version, Sessions), "gw-test", Ping, Time);
            _run = Task.Run(() => Client.RunAsync(_stop.Token));
        }

        /// <summary>Advances the health clock a ping at a time until the condition holds.</summary>
        public Task<bool> Until(Func<bool> condition) =>
            Rig.Until(() => { if (condition()) return true; Time.Advance(Ping); return condition(); });

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            try { await _run; } catch (OperationCanceledException) { }
            await Svc.DisposeAsync();
        }
    }

    [Fact]
    public async Task Pings_carry_the_table_version_and_session_count()
    {
        await using var h = new Harness();
        Assert.True(await h.Until(() => h.Svc.Pings.Count >= 1));
        var p = h.Svc.Pings.First();
        Assert.Equal((5UL, 2, "gw-test"), (p.TableVersion, p.Sessions, p.GatewayId));
    }

    [Fact]
    public async Task The_first_ping_after_attach_requests_a_sync_and_later_ones_do_not()
    {
        await using var h = new Harness();
        h.Svc.TableVersion = 5;
        Assert.True(await h.Until(() => h.Svc.Pings.Count >= 3));
        Assert.Equal([true, false, false], h.Svc.Pings.Take(3).Select(p => p.RequestSync));
    }

    [Fact]
    public async Task A_pong_with_another_version_makes_the_next_ping_request_a_sync()
    {
        await using var h = new Harness();
        h.Svc.TableVersion = 5;
        Assert.True(await h.Until(() => h.Svc.Pings.Count >= 2));
        h.Svc.TableVersion = 9;
        var before = h.Svc.Pings.Count;
        Assert.True(await h.Until(() => h.Svc.Pings.Skip(before + 1).Any(p => p.RequestSync)));
    }

    [Fact]
    public async Task Three_unanswered_pings_mark_the_health_lost()
    {
        await using var h = new Harness();
        h.Svc.Mute = true;
        Assert.True(await h.Until(() => h.Svc.Pings.Count >= 3));
        Assert.True(h.Client.Lost);
    }

    [Fact]
    public async Task Answered_pings_keep_the_health()
    {
        await using var h = new Harness();
        h.Svc.TableVersion = 5;
        Assert.True(await h.Until(() => h.Client.PongsReceived >= 4));
        Assert.False(h.Client.Lost);
    }

    [Fact]
    public async Task A_dropped_stream_is_reattached_with_a_sync_request()
    {
        await using var h = new Harness();
        h.Svc.TableVersion = 5;
        Assert.True(await h.Until(() => h.Svc.Pings.Count >= 2));
        h.Svc.Drop();
        Assert.True(await h.Until(() => h.Client.Attaches >= 2 && h.Svc.Pings.Count(p => p.RequestSync) >= 2));
    }

    [Fact]
    public async Task Relaying_continues_through_a_lost_health_stream()
    {
        // The composition the process runs: GatewayWorker starts the relay and holds the stream.
        await using var svc = new FakeService();
        var time = new FakeTimeProvider();
        var o = new GatewayOptions { GatewayId = "gw", Public = "127.0.0.1:0" };
        await using var events = new GrpcGatewayEvents(svc.EventsClient, time);
        var relay = new GatewayRelay(o, time, events);
        var health = new GatewayHealthClient(svc.HealthClient, () => (relay.TableVersion, relay.SessionCount), o.GatewayId, Ping, time);
        var worker = new GatewayWorker(relay, health, events);
        await worker.StartAsync(CancellationToken.None);
        await using var hub = new Pod("hub");
        Assert.Equal(ControlStatus.Ok, relay.SetDefault(1, hub.EndPoint.ToString()));
        Assert.True(await Rig.Until(() => { try { _ = relay.PublicEndPoint; return true; } catch (InvalidOperationException) { return false; } }));
        using var c = new FakeClient.FakeClient(relay.PublicEndPoint, 1);
        await c.ConnectAsync(Rig.T);

        svc.Mute = true;
        Assert.True(await Rig.Until(() => { time.Advance(Ping); return health.Lost; }));
        svc.Drop();
        time.Advance(Ping);
        Assert.Equal("hub", await c.KeepaliveAsync(Rig.T));
        Assert.Equal(1UL, relay.TableVersion);
        await worker.StopAsync(CancellationToken.None);
    }
}
