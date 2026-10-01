using Prometheus;

namespace SourceSharp.Host.Gateway.Relay;

/// <summary>
/// The relay's Prometheus series (plan Q25), served on /metrics. Each relay may be given its own
/// registry (facts do); the process uses the default one.
/// </summary>
public sealed class GatewayMetrics
{
    public CollectorRegistry Registry { get; }
    public Gauge Sessions { get; }
    public Counter PacketsIn { get; }
    public Counter PacketsOut { get; }
    public Counter BytesIn { get; }
    public Counter BytesOut { get; }
    public Gauge HeldPackets { get; }
    public Counter HoldEvicted { get; }
    public Gauge AdmissionQueue { get; }
    public Counter Flips { get; }
    public Counter Expiries { get; }
    public Counter Closes { get; }
    public Counter A2sAnswered { get; }
    public Counter HandshakesRateLimited { get; }

    public GatewayMetrics(CollectorRegistry? registry = null)
    {
        Registry = registry ?? Metrics.DefaultRegistry;
        var f = Metrics.WithCustomRegistry(Registry);
        Sessions = f.CreateGauge("gateway_sessions", "Live sessions.");
        PacketsIn = f.CreateCounter("gateway_packets_in_total", "Datagrams received from clients.");
        PacketsOut = f.CreateCounter("gateway_packets_out_total", "Datagrams relayed from backends to clients.");
        BytesIn = f.CreateCounter("gateway_bytes_in_total", "Bytes received from clients.");
        BytesOut = f.CreateCounter("gateway_bytes_out_total", "Bytes relayed from backends to clients.");
        HeldPackets = f.CreateGauge("gateway_held_packets", "Client datagrams held (admission, route flip, identity-first).");
        HoldEvicted = f.CreateCounter("gateway_hold_evicted_total", "Held datagrams evicted, oldest first, by the hold bounds.");
        AdmissionQueue = f.CreateGauge("gateway_admission_queue", "Sessions waiting for a backend to become ready or admit them.");
        Flips = f.CreateCounter("gateway_route_flips_total", "Hard cuts to a new backend.");
        Expiries = f.CreateCounter("gateway_session_expiries_total", "Sessions closed by silence.");
        Closes = f.CreateCounter("gateway_session_closes_total", "Sessions closed, any reason.");
        A2sAnswered = f.CreateCounter("gateway_a2s_answered_total", "Server queries answered by the gateway.");
        HandshakesRateLimited = f.CreateCounter("gateway_handshakes_rate_limited_total", "Handshake packets dropped by the per-IP rate.");
    }

    /// <summary>The exposition text, for facts.</summary>
    public async Task<string> TextAsync()
    {
        using var m = new MemoryStream();
        await Registry.CollectAndExportAsTextAsync(m);
        return System.Text.Encoding.UTF8.GetString(m.ToArray());
    }
}
