using System.Net;
using System.Net.Sockets;

namespace SourceSharp.Host.Relay;

/// <summary>One live peer: the loopback socket a session reaches the engine from.</summary>
public sealed class PeerLease(Socket socket, IPEndPoint peer, string sessionId, string clientAddr, ulong? steamId, DateTimeOffset opened)
{
    public Socket Socket { get; } = socket;
    public IPEndPoint Peer { get; } = peer;
    public string SessionId { get; } = sessionId;
    public string ClientAddr { get; } = clientAddr;
    public ulong? SteamId { get; } = steamId;
    public DateTimeOffset Opened { get; } = opened;
    /// <summary>Cancelled when the lease is taken away (a second stream for the SteamID, idle, shutdown).</summary>
    public CancellationTokenSource Cut { get; } = new();
    public string? CutReason { get; set; }
}

/// <summary>
/// Which loopback address each session reaches the engine from, and who is behind each one — the
/// per-player peer (plan §8.1a) moved into the pod. Addresses come from <c>AddressPool</c> in order
/// and are unique within this instance only: two live clients never share one; a SteamID that
/// reconnects gets the address it had while this instance lives; a session with no SteamID gets a
/// fresh one; a released address goes to another player only after <c>Quarantine</c>. Thread-safe.
/// </summary>
public sealed class PeerTable
{
    readonly RelayOptions _o;
    readonly TimeProvider _time;
    readonly Lock _sync = new();
    readonly Dictionary<IPAddress, PeerLease> _live = [];
    readonly Dictionary<ulong, PeerLease> _liveBySteam = [];
    readonly AddressPool _pool;

    public PeerTable(RelayOptions options, TimeProvider time)
    {
        options.Validate();
        _o = options;
        _time = time;
        _pool = new AddressPool(options.AddressPool, options.Quarantine, time);
    }

    public int LiveCount { get { lock (_sync) return _live.Count; } }

    /// <summary>The address a SteamID had on this instance, if it has had one.</summary>
    public IPAddress? RememberedFor(ulong steamId) { lock (_sync) return _pool.RememberedFor(steamId); }

    /// <summary>
    /// Takes an address for a new stream and binds a socket there. A live lease for the same SteamID
    /// is cut first (its socket closed before the new one binds). Returns the new lease and the one it
    /// superseded, if any.
    /// </summary>
    public (PeerLease Lease, PeerLease? Superseded) Acquire(string sessionId, string clientAddr, ulong? steamId, IPEndPoint? hint)
    {
        lock (_sync)
        {
            PeerLease? old = null;
            if (steamId is { } sid && _liveBySteam.TryGetValue(sid, out old))
            {
                old.CutReason = "superseded";
                ReleaseLocked(old);
                old.Cut.Cancel();
            }
            // A returning player's address first, then a restarted gateway's hint (its port too), then the pool in order.
            var lease = _pool.Take(sessionId, steamId, hint?.Address,
                a => Bind(a, hint is not null && a.Equals(hint.Address) ? hint.Port : _o.PeerPort, sessionId, clientAddr, steamId))
                ?? throw new InvalidOperationException($"no free address in {_o.AddressPool}");
            return (lease, old);
        }
    }

    PeerLease? Bind(IPAddress a, int port, string sessionId, string clientAddr, ulong? steamId)
    {
        var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try { sock.Bind(new IPEndPoint(a, port)); }
        catch (SocketException) { sock.Dispose(); return null; } // taken outside this table
        var lease = new PeerLease(sock, (IPEndPoint)sock.LocalEndPoint!, sessionId, clientAddr, steamId, _time.GetUtcNow());
        _live[a] = lease;
        if (steamId is { } sid) _liveBySteam[sid] = lease;
        return lease;
    }

    /// <summary>Ends a lease: its socket closes and its address is quarantined for other players.</summary>
    public void Release(PeerLease lease)
    {
        lock (_sync) ReleaseLocked(lease);
    }

    void ReleaseLocked(PeerLease lease)
    {
        var a = lease.Peer.Address;
        if (_live.TryGetValue(a, out var cur) && cur == lease)
        {
            _live.Remove(a);
            if (lease.SteamId is { } sid && _liveBySteam.TryGetValue(sid, out var s) && s == lease) _liveBySteam.Remove(sid);
            _pool.Release(a, lease.SteamId, lease.SessionId);
        }
        lease.Socket.Dispose();
    }

    /// <summary>The lease behind a peer; port 0 matches any port on the address.</summary>
    public PeerLease? Resolve(IPEndPoint peer)
    {
        lock (_sync)
            return _live.TryGetValue(peer.Address, out var l) && (peer.Port == 0 || l.Peer.Port == peer.Port) ? l : null;
    }

    public IReadOnlyList<PeerLease> Live { get { lock (_sync) return [.. _live.Values]; } }

    /// <summary>Ends every live stream (the pod is stopping): each gets Closed with the reason.</summary>
    public void CutAll(string reason)
    {
        foreach (var l in Live)
        {
            l.CutReason ??= reason;
            try { l.Cut.Cancel(); } catch (ObjectDisposedException) { }
        }
    }
}
