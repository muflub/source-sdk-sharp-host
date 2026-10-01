using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace SourceSharp.Host.Relay.Tests;

/// <summary>Each relay endpoint answers on its own listener, judged by the connection's local port.</summary>
public class ListenerFacts
{
    static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    sealed class Host : IAsyncDisposable
    {
        public readonly RelayOptions O;
        readonly WebApplication _app;

        public Host()
        {
            O = new RelayOptions
            {
                Listen = $"127.0.0.1:{FreePort()}", InfoListen = $"127.0.0.1:{FreePort()}", Health = $"127.0.0.1:{FreePort()}",
                EngineEndpoint = "127.0.0.1:9", PeerPort = 0,
            };
            var b = WebApplication.CreateSlimBuilder();
            b.Logging.ClearProviders();
            b.Services.AddPeerRelay(O, metrics: new RelayMetrics(Prometheus.Metrics.NewCustomRegistry()));
            b.WebHost.ConfigureKestrel(k => k.ListenRelay(O));
            _app = b.Build();
            _app.MapPeerRelay(O);
            _app.StartAsync().GetAwaiter().GetResult();
        }

        public async ValueTask DisposeAsync() { await _app.StopAsync(); await _app.DisposeAsync(); }
    }

    static async Task<HttpStatusCode> Get(string listener, string? hostHeader = null, bool http2 = false)
    {
        using var http = new HttpClient();
        var req = new HttpRequestMessage(HttpMethod.Get, $"http://{listener}/healthz");
        if (http2) { req.Version = HttpVersion.Version20; req.VersionPolicy = HttpVersionPolicy.RequestVersionExact; }
        if (hostHeader is not null) req.Headers.Host = hostHeader;
        return (await http.SendAsync(req)).StatusCode;
    }

    [Fact]
    public async Task Healthz_answers_on_the_health_listener_whatever_the_host_header_says()
    {
        await using var h = new Host();
        Assert.Equal(HttpStatusCode.OK, await Get(h.O.Health, hostHeader: "127.0.0.1:15012"));
    }

    [Fact]
    public async Task Healthz_is_not_served_on_the_relay_listener()
    {
        await using var h = new Host();
        Assert.Equal(HttpStatusCode.NotFound, await Get(h.O.Listen, http2: true));
    }
}
