using System.Net;

namespace SourceSharp.Host.Relay;

/// <summary>The sidecar's configuration: appsettings + SIDECAR_* environment, bound at the root.</summary>
public sealed class RelayOptions
{
    /// <summary>PeerRelay (h2c), reached by the gateway.</summary>
    public string Listen { get; set; } = "0.0.0.0:5010";
    /// <summary>PeerInfo (h2c), reached by the game server in the same pod. Loopback only.</summary>
    public string InfoListen { get; set; } = "127.0.0.1:5011";
    /// <summary>/healthz and /metrics for the kubelet.</summary>
    public string Health { get; set; } = "0.0.0.0:5012";
    /// <summary>The engine's UDP endpoint inside the pod.</summary>
    public string EngineEndpoint { get; set; } = "127.0.0.1:27015";
    /// <summary>The loopback addresses this instance hands out, one per live client, in order; unique
    /// within this pod only. Must not contain 127.0.0.1 (the engine's own).</summary>
    public string AddressPool { get; set; } = "127.1.0.0/16";
    /// <summary>The port every peer binds. Each peer has its own address, so a fixed port keeps a
    /// returning player's ip:port the same. Not 27005: srcds opens its client socket on clientport
    /// 27005 too, and a wildcard bind there blocks 127.x:27005.
    /// 0 = ephemeral (facts: several sidecars share one host).</summary>
    public int PeerPort { get; set; } = 29005;
    /// <summary>A stream with no datagram either way for this long is closed.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(5);
    /// <summary>
    /// Loopback (default): each client reaches the engine from its own 127.x address (D-H10).
    /// Interpose: the launcher interposes the engine's recvfrom/sendto on its game socket and the
    /// engine sees each client's real ip:port (only inside Host.Launcher; not proven against the real
    /// engine yet, docs/net-protocol.md §12).
    /// </summary>
    public string Mode { get; set; } = "Loopback";
    public bool Interpose => Mode == "Interpose";
    /// <summary>A released address is not given to another player for this long.</summary>
    public TimeSpan Quarantine { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Every reason these options cannot run; empty when they can.</summary>
    public IReadOnlyList<string> Problems()
    {
        var p = new List<string>();
        if (!IPEndPoint.TryParse(InfoListen, out var info)) p.Add($"InfoListen '{InfoListen}' is not ip:port");
        else if (!IPAddress.IsLoopback(info.Address)) p.Add($"InfoListen {InfoListen} is not a loopback address: PeerInfo answers the game server in this pod and nobody else");
        foreach (var (name, v) in new[] { ("Listen", Listen), ("Health", Health), ("EngineEndpoint", EngineEndpoint) })
            if (!IPEndPoint.TryParse(v, out _)) p.Add($"{name} '{v}' is not ip:port");
        if (Mode is not ("Loopback" or "Interpose")) p.Add($"Mode '{Mode}' is not Loopback or Interpose");
        if (!IPNetwork.TryParse(AddressPool, out var pool) || !IPAddress.IsLoopback(pool.BaseAddress) || pool.PrefixLength < 8 || pool.PrefixLength > 30)
            p.Add($"AddressPool '{AddressPool}' is not a loopback network between /8 and /30");
        else if (pool.Contains(IPAddress.Loopback))
            p.Add($"AddressPool {AddressPool} contains 127.0.0.1");
        return p;
    }

    /// <summary>Throws with every problem; the sidecar refuses to start on any.</summary>
    public void Validate()
    {
        var p = Problems();
        if (p.Count > 0) throw new InvalidOperationException("sidecar options refused: " + string.Join("; ", p));
    }
}
