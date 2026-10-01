using System.Buffers.Binary;
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
    readonly Dictionary<ulong, IPAddress> _remembered = [];
    readonly Dictionary<IPAddress, ulong> _owner = [];
    readonly Dictionary<IPAddress, (DateTimeOffset Until, ulong? SteamId, string SessionId)> _quarantine = [];
    readonly uint _first, _size;
    uint _cursor;

    public PeerTable(RelayOptions options, TimeProvider time)
    {
        options.Validate();
        _o = options;
        _time = time;
        var n = IPNetwork.Parse(options.AddressPool);
        _first = ToUInt(n.BaseAddress);
        _size = 1u << (32 - n.PrefixLength);
    }

    static uint ToUInt(IPAddress a)
    {
        Span<byte> b = stackalloc byte[4];
        a.TryWriteBytes(b, out _);
        return BinaryPrimitives.ReadUInt32BigEndian(b);
    }

    static IPAddress ToAddress(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        return new IPAddress(b);
    }

    bool InPool(IPAddress a) => a.AddressFamily == AddressFamily.InterNetwork && ToUInt(a) - _first < _size;

    public int LiveCount { get { lock (_sync) return _live.Count; } }

    /// <summary>The address a SteamID had on this instance, if it has had one.</summary>
    public IPAddress? RememberedFor(ulong steamId) { lock (_sync) return _remembered.GetValueOrDefault(steamId); }

    bool Free(IPAddress a, ulong? steamId, string sessionId)
    {
        if (_live.ContainsKey(a)) return false;
        if (_owner.TryGetValue(a, out var owner) && owner != steamId) return false; // another player's address here
        if (!_quarantine.TryGetValue(a, out var q)) return true;
        if (q.Until <= _time.GetUtcNow()) { _quarantine.Remove(a); return true; }
        return (steamId is not null && q.SteamId == steamId) || q.SessionId == sessionId;
    }

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

            // A returning player's address first, then a restarted gateway's hint, then the pool in order.
            if (steamId is { } s1 && _remembered.TryGetValue(s1, out var mine) && TryTake(mine, _o.PeerPort, sessionId, clientAddr, steamId) is { } back)
                return (back, old);
            if (hint is not null && InPool(hint.Address) && TryTake(hint.Address, hint.Port, sessionId, clientAddr, steamId) is { } hinted)
                return (hinted, old);
            for (uint i = 0; i < _size; i++)
            {
                var v = _first + (_cursor + i) % _size;
                if ((v & 0xFF) is 0 or 255) continue;
                var a = ToAddress(v);
                if (TryTake(a, _o.PeerPort, sessionId, clientAddr, steamId) is { } lease)
                {
                    _cursor = (v - _first + 1) % _size;
                    return (lease, old);
                }
            }
            throw new InvalidOperationException($"no free address in {_o.AddressPool}");
        }
    }

    PeerLease? TryTake(IPAddress a, int port, string sessionId, string clientAddr, ulong? steamId)
    {
        if (!Free(a, steamId, sessionId)) return null;
        var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try { sock.Bind(new IPEndPoint(a, port)); }
        catch (SocketException) { sock.Dispose(); return null; } // taken outside this table
        var lease = new PeerLease(sock, (IPEndPoint)sock.LocalEndPoint!, sessionId, clientAddr, steamId, _time.GetUtcNow());
        _live[a] = lease;
        _quarantine.Remove(a);
        if (steamId is { } sid)
        {
            _liveBySteam[sid] = lease;
            if (_remembered.TryGetValue(sid, out var prev) && !prev.Equals(a)) _owner.Remove(prev);
            _remembered[sid] = a;
            _owner[a] = sid;
        }
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
            _quarantine[a] = (_time.GetUtcNow() + _o.Quarantine, lease.SteamId, lease.SessionId);
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
