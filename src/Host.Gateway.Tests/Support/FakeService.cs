using System.Collections.Concurrent;
using System.Net;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SourceSharp.Host.Gateway.Control;
using SourceSharp.Host.Gateway.Relay;
using SourceSharp.Host.Proto.Gateway;

namespace SourceSharp.Host.Gateway.Tests.Support;

/// <summary>
/// The service's side of the gateway API, in-process: GatewayHealth and GatewayEvents on a loopback
/// h2c port, recording every call. Tests script failures and the table version it pongs with.
/// </summary>
public sealed class FakeService : IAsyncDisposable
{
    readonly WebApplication _app;
    public string Address { get; }
    public GrpcChannel Channel { get; }

    public ConcurrentQueue<Ping> Pings { get; } = new();
    public ConcurrentQueue<(string Kind, SessionEvent Event)> Sessions { get; } = new();
    public ConcurrentQueue<PlayerPin> Pins { get; } = new();
    public ConcurrentQueue<GatewayStats> Stats { get; } = new();
    public ConcurrentQueue<IdentifyRequest> Identifies { get; } = new();
    /// <summary>Every session-event call as it arrived, failed or not: (kind, request_id).</summary>
    public ConcurrentQueue<(string Kind, string RequestId)> Attempts { get; } = new();

    /// <summary>The table version pongs carry.</summary>
    public ulong TableVersion { get; set; }
    /// <summary>While true, pings are read and never answered.</summary>
    public bool Mute { get; set; }
    /// <summary>The next N event calls fail with UNAVAILABLE.</summary>
    public int FailNext;
    /// <summary>Cancelled to end every open health stream from the service side.</summary>
    public CancellationTokenSource DropStreams { get; private set; } = new();
    public Func<IdentifyRequest, Task<IdentifyResponse>> OnIdentify { get; set; } =
        _ => Task.FromResult(new IdentifyResponse { Allowed = true });
    /// <summary>Awaited before a Stats call is recorded, with the call's token (a call held in flight).</summary>
    public Func<CancellationToken, Task>? BeforeStats { get; set; }

    public FakeService()
    {
        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.Services.AddGrpc();
        b.Services.AddSingleton(this);
        b.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http2));
        _app = b.Build();
        _app.MapGrpcService<Health>();
        _app.MapGrpcService<Events>();
        _app.StartAsync().GetAwaiter().GetResult();
        Address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        Channel = GrpcChannel.ForAddress(Address);
    }

    public void Drop()
    {
        var old = DropStreams;
        DropStreams = new CancellationTokenSource();
        old.Cancel();
    }

    public GatewayEvents.GatewayEventsClient EventsClient => new(Channel);
    public GatewayHealth.GatewayHealthClient HealthClient => new(Channel);

    bool ShouldFail() => Interlocked.Decrement(ref FailNext) >= 0;

    sealed class Health(FakeService f) : GatewayHealth.GatewayHealthBase
    {
        public override async Task Attach(IAsyncStreamReader<Ping> requestStream, IServerStreamWriter<Pong> responseStream, ServerCallContext context)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, f.DropStreams.Token);
            try
            {
                while (await requestStream.MoveNext(linked.Token))
                {
                    var ping = requestStream.Current;
                    f.Pings.Enqueue(ping);
                    if (!f.Mute) await responseStream.WriteAsync(new Pong { Seq = ping.Seq, TableVersion = f.TableVersion }, linked.Token);
                }
            }
            catch (OperationCanceledException) when (f.DropStreams.IsCancellationRequested || linked.IsCancellationRequested)
            {
                throw new RpcException(new Status(StatusCode.Unavailable, "dropped by the fake service"));
            }
        }
    }

    sealed class Events(FakeService f) : GatewayEvents.GatewayEventsBase
    {
        Task<EventAck> Record(string kind, SessionEvent e)
        {
            f.Attempts.Enqueue((kind, e.RequestId));
            if (f.ShouldFail()) throw new RpcException(new Status(StatusCode.Unavailable, "scripted failure"));
            f.Sessions.Enqueue((kind, e));
            return Task.FromResult(new EventAck { TableVersion = f.TableVersion });
        }

        public override Task<EventAck> SessionOpened(SessionEvent request, ServerCallContext context) => Record("opened", request);
        public override Task<EventAck> SessionClosed(SessionEvent request, ServerCallContext context) => Record("closed", request);
        public override Task<EventAck> SessionMoved(SessionEvent request, ServerCallContext context) => Record("moved", request);

        public override Task<EventAck> PinAssigned(PlayerPin request, ServerCallContext context)
        {
            if (f.ShouldFail()) throw new RpcException(new Status(StatusCode.Unavailable, "scripted failure"));
            f.Pins.Enqueue(request);
            return Task.FromResult(new EventAck());
        }

        public override async Task<EventAck> Stats(GatewayStats request, ServerCallContext context)
        {
            if (f.BeforeStats is { } before) await before(context.CancellationToken);
            f.Stats.Enqueue(request);
            return new EventAck();
        }

        public override Task<IdentifyResponse> Identify(IdentifyRequest request, ServerCallContext context)
        {
            f.Identifies.Enqueue(request);
            return f.OnIdentify(request);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Channel.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>GatewayControl for one relay on a loopback h2c port, and a client for it.</summary>
public sealed class ControlHost : IAsyncDisposable
{
    readonly WebApplication _app;
    readonly GrpcChannel _channel;
    public GatewayControl.GatewayControlClient Client { get; }

    public ControlHost(GatewayRelay relay)
    {
        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.Services.AddGrpc();
        b.Services.AddSingleton(relay);
        b.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http2));
        _app = b.Build();
        _app.MapGrpcService<GatewayControlService>();
        _app.StartAsync().GetAwaiter().GetResult();
        _channel = GrpcChannel.ForAddress(_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
        Client = new GatewayControl.GatewayControlClient(_channel);
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
