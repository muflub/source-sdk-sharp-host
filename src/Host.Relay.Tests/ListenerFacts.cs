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

        /// <summary>
        /// The relay tells its listeners apart by the configured port, so each is fixed before the
        /// start: picked free and released, and something else can take one before Kestrel binds it.
        /// A bind that fails with "address already in use" starts again on fresh ports.
        /// </summary>
        /// <param name="pickPort">Picks each listener's port (the facts' seam for a port taken before the bind).</param>
        public Host(Func<int>? pickPort = null)
        {
            pickPort ??= FreePort;
            for (var attempt = 1; ; attempt++)
            {
                var o = new RelayOptions
                {
                    Listen = $"127.0.0.1:{pickPort()}", InfoListen = $"127.0.0.1:{pickPort()}", Health = $"127.0.0.1:{pickPort()}",
                    EngineEndpoint = "127.0.0.1:9", PeerPort = 0,
                };
                var b = WebApplication.CreateSlimBuilder();
                b.Logging.ClearProviders();
                b.Services.AddPeerRelay(o, metrics: new RelayMetrics(Prometheus.Metrics.NewCustomRegistry()));
                b.WebHost.ConfigureKestrel(k => k.ListenRelay(o));
                var app = b.Build();
                app.MapPeerRelay(o);
                try { app.StartAsync().GetAwaiter().GetResult(); }
                catch (IOException e) when (attempt < 5 && e.InnerException is Microsoft.AspNetCore.Connections.AddressInUseException)
                {
                    app.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    continue;
                }
                (O, _app) = (o, app);
                return;
            }
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
    public async Task The_host_starts_when_a_port_it_picked_is_taken_before_the_bind()
    {
        // FreePort releases the port it found; another fact's outgoing connection or listener may
        // take it before Kestrel binds. Here a listener takes the first pick in that window.
        TcpListener? squatter = null;
        var picks = 0;
        int Pick()
        {
            var port = FreePort();
            if (++picks == 1)
            {
                squatter = new TcpListener(IPAddress.Loopback, port);
                squatter.Start();
            }
            return port;
        }
        try
        {
            await using var h = new Host(Pick);
            Assert.True(picks > 3, $"{picks} picks: the taken port was never replaced");
            Assert.Equal(HttpStatusCode.OK, await Get(h.O.Health));
        }
        finally { squatter?.Stop(); }
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
