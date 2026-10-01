using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Time.Testing;
using SourceSharp.Host.FakeClient;
using SourceSharp.Host.Gateway.Events;
using SourceSharp.Host.Gateway.Relay;
using SourceSharp.Host.Proto.Gateway;
using Client = SourceSharp.Host.FakeClient.FakeClient;

namespace SourceSharp.Host.Gateway.Tests.Support;

/// <summary>Records every event the relay reports; Identify answers with <see cref="OnIdentify"/>.</summary>
public sealed class RecordingEvents : IGatewayEvents
{
    public ConcurrentQueue<(string Kind, SessionEvent Event)> Sessions { get; } = new();
    public ConcurrentQueue<PlayerPin> Pins { get; } = new();
    public ConcurrentQueue<GatewayStats> StatsSeen { get; } = new();
    public ConcurrentQueue<IdentifyRequest> Identifies { get; } = new();
    public Func<IdentifyRequest, Task<IdentifyResponse>> OnIdentify { get; set; } =
        _ => Task.FromResult(new IdentifyResponse { Allowed = true });

    public void SessionOpened(SessionEvent e) => Sessions.Enqueue(("opened", e));
    public void SessionClosed(SessionEvent e) => Sessions.Enqueue(("closed", e));
    public void SessionMoved(SessionEvent e) => Sessions.Enqueue(("moved", e));
    public void PinAssigned(PlayerPin pin) => Pins.Enqueue(pin);
    public void Stats(GatewayStats stats) => StatsSeen.Enqueue(stats);
    public Task<IdentifyResponse> IdentifyAsync(IdentifyRequest request, CancellationToken ct)
    {
        Identifies.Enqueue(request);
        return OnIdentify(request);
    }

    public IReadOnlyList<SessionEvent> Of(string kind) => Sessions.Where(x => x.Kind == kind).Select(x => x.Event).ToList();
}

/// <summary>A relay on loopback with a fake clock, some pods (sidecar + toy engine) and clients; disposes all of it.</summary>
public sealed class Rig : IAsyncDisposable
{
    public static readonly TimeSpan T = TimeSpan.FromSeconds(5);
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero));
    public RecordingEvents Events { get; } = new();
    public GatewayOptions Options { get; }
    public GatewayRelay Relay { get; }
    readonly List<IDisposable> _owned = [];
    readonly List<Pod> _pods = [];
    ulong _version;

    public Rig(Action<GatewayOptions>? configure = null, Func<FakeTimeProvider, IGatewayEvents>? events = null)
    {
        Options = new GatewayOptions
        {
            GatewayId = "gw-test",
            Public = "127.0.0.1:0",
            HandshakesPerSecond = 1000,
        };
        configure?.Invoke(Options);
        Relay = new GatewayRelay(Options, Time, events?.Invoke(Time) ?? Events, metrics: new GatewayMetrics(Prometheus.Metrics.NewCustomRegistry()));
        Relay.Start();
    }

    /// <summary>A game pod: the route table names its sidecar; its engine is a ToyBackend.</summary>
    public Pod Backend(string id, Action<SourceSharp.Host.Relay.RelayOptions>? configure = null)
    {
        var b = new Pod(id, configure);
        _pods.Add(b);
        return b;
    }

    public Client Client(ulong steamId, string name = "player")
    {
        var c = new Client(Relay.PublicEndPoint, steamId, name);
        _owned.Add(c);
        return c;
    }

    /// <summary>
    /// ConnectAsync, then waits until the client's post-accept keepalive has reached
    /// <paramref name="pod"/>'s engine. ConnectAsync returns as soon as the accept arrives, while that
    /// keepalive may still sit in the gateway's socket: a fact that closes the session, flips the
    /// route, restarts the relay or snapshots the engine's log in that window races it (a late
    /// keepalive reopens a closed session as a new, unidentified one).
    /// </summary>
    public async Task<string> ConnectSettled(Client c, Pod pod)
    {
        var id = await c.ConnectAsync(T);
        Assert.True(await pod.WaitForAsync(l => l.Any(r => r.Kind == ToyWire.Keepalive), T), "the post-accept keepalive never reached the engine");
        return id;
    }

    /// <summary>The next table version, as the service would produce it.</summary>
    public ulong Next() => ++_version;

    public void SetDefault(Pod b) => Assert.Equal(ControlStatus.Ok, Relay.SetDefault(Next(), b.EndPoint.ToString()));

    public static async Task<bool> Until(Func<bool> condition, TimeSpan? timeout = null)
    {
        var sw = Stopwatch.StartNew();
        var limit = timeout ?? T;
        while (sw.Elapsed < limit)
        {
            if (condition()) return true;
            await Task.Delay(5);
        }
        return condition();
    }

    /// <summary>The peer a backend sees for a client, from the relay's table.</summary>
    public IPEndPoint PeerOf(Client c) => IPEndPoint.Parse(Relay.SessionOf(c.LocalEndPoint)!.Peer);

    bool _relayDisposed;

    /// <summary>Stops the relay (a gateway restart) and keeps the backends and clients.</summary>
    public async Task DisposeRelayOnlyAsync()
    {
        if (_relayDisposed) return;
        _relayDisposed = true;
        await Relay.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeRelayOnlyAsync();
        foreach (var d in _owned) d.Dispose();
        foreach (var p in _pods) await p.DisposeAsync();
    }
}
