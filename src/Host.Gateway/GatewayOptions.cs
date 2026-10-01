namespace SourceSharp.Host.Gateway;

/// <summary>The gateway's configuration (plan §8): appsettings + GATEWAY_* environment, bound at the root.</summary>
public sealed class GatewayOptions
{
    public string GatewayId { get; set; } = Environment.MachineName;
    /// <summary>The public UDP socket (behind the LoadBalancer, externalTrafficPolicy Local).</summary>
    public string Public { get; set; } = "0.0.0.0:27015";
    /// <summary>GatewayControl gRPC, reachable by the service only.</summary>
    public string Control { get; set; } = "0.0.0.0:5003";
    /// <summary>/healthz and /metrics for the kubelet.</summary>
    public string Health { get; set; } = "0.0.0.0:5006";
    /// <summary>The service's gateway API (GatewayHealth, GatewayEvents).</summary>
    public string Service { get; set; } = "http://descent-service:5002";
    public TimeSpan SessionExpiry { get; set; } = TimeSpan.FromSeconds(60);
    /// <summary>Handshakes (getchallenges) accepted per client IP per second; more are dropped (Q24).</summary>
    public int HandshakesPerSecond { get; set; } = 2;
    public int HoldPackets { get; set; } = 64;
    public int HoldBytes { get; set; } = 64 * 1024;
    public TimeSpan HoldTime { get; set; } = TimeSpan.FromSeconds(15);
    /// <summary>The identity-first handshake (§8.1b). Off until the H0f capture shows the 2013 client
    /// re-sends connect on a second challenge (docs/net-protocol.md, open questions).</summary>
    public bool IdentityFirst { get; set; }
    /// <summary>The connectionless handshake format identity-first parses: Source (default) or Toy (the fake tier).</summary>
    public string Wire { get; set; } = "Source";
    public TimeSpan PingInterval { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan StatsInterval { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>How often expiry, hold and retry deadlines are checked, on the relay's clock.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMilliseconds(250);
    /// <summary>After a PeerRelay stream fails before Opened, the wait before the next attempt (the session holds meanwhile).</summary>
    public TimeSpan StreamRetry { get; set; } = TimeSpan.FromMilliseconds(500);
}
