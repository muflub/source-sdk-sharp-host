using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace SourceSharp.Host.Relay;

/// <summary>
/// The per-player loopback addresses of one instance (D-H10), shared by both relay modes: <see cref="PeerTable"/>
/// binds a socket on each (Loopback), <see cref="InterposeSwitchboard"/> only presents it to the engine
/// (Interpose with synthetic addresses, plan_steam_auth S-6 B). In pool order; unique within this instance;
/// a SteamID that returns gets its address back while the instance lives; a session with no SteamID gets a fresh
/// one; a released address goes to another player only after the quarantine. Not thread-safe: callers lock.
/// </summary>
public sealed class AddressPool
{
    readonly TimeSpan _quarantineFor;
    readonly TimeProvider _time;
    readonly HashSet<IPAddress> _live = [];
    readonly Dictionary<ulong, IPAddress> _remembered = [];
    readonly Dictionary<IPAddress, ulong> _owner = [];
    readonly Dictionary<IPAddress, (DateTimeOffset Until, ulong? SteamId, string SessionId)> _quarantine = [];
    readonly uint _first, _size;
    uint _cursor;

    public AddressPool(string cidr, TimeSpan quarantine, TimeProvider time)
    {
        var n = IPNetwork.Parse(cidr);
        _first = ToUInt(n.BaseAddress);
        _size = 1u << (32 - n.PrefixLength);
        _quarantineFor = quarantine;
        _time = time;
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

    public bool Contains(IPAddress a) => a.AddressFamily == AddressFamily.InterNetwork && ToUInt(a) - _first < _size;

    public IPAddress? RememberedFor(ulong steamId) => _remembered.GetValueOrDefault(steamId);

    bool Free(IPAddress a, ulong? steamId, string sessionId)
    {
        if (_live.Contains(a)) return false;
        if (_owner.TryGetValue(a, out var owner) && owner != steamId) return false; // another player's address here
        if (!_quarantine.TryGetValue(a, out var q)) return true;
        if (q.Until <= _time.GetUtcNow()) { _quarantine.Remove(a); return true; }
        return (steamId is not null && q.SteamId == steamId) || q.SessionId == sessionId;
    }

    /// <summary>
    /// Takes an address: <paramref name="hint"/> first (the address a resumed session already had, which the
    /// engine knows it by), then a returning SteamID's own, then the pool in order;
    /// <paramref name="use"/> claims it (binds a socket, say) and may refuse it. Null when the pool is exhausted.
    /// </summary>
    public T? Take<T>(string sessionId, ulong? steamId, IPAddress? hint, Func<IPAddress, T?> use) where T : class
    {
        if (hint is not null && Contains(hint) && TryUse(hint, sessionId, steamId, use) is { } hinted) return hinted;
        if (steamId is { } s1 && _remembered.TryGetValue(s1, out var mine) && TryUse(mine, sessionId, steamId, use) is { } back) return back;
        for (uint i = 0; i < _size; i++)
        {
            var v = _first + (_cursor + i) % _size;
            if ((v & 0xFF) is 0 or 255) continue;
            if (TryUse(ToAddress(v), sessionId, steamId, use) is { } taken)
            {
                _cursor = (v - _first + 1) % _size;
                return taken;
            }
        }
        return null;
    }

    T? TryUse<T>(IPAddress a, string sessionId, ulong? steamId, Func<IPAddress, T?> use) where T : class
    {
        if (!Free(a, steamId, sessionId)) return null;
        var taken = use(a);
        if (taken is null) return null;
        _live.Add(a);
        _quarantine.Remove(a);
        if (steamId is { } sid)
        {
            if (_remembered.TryGetValue(sid, out var prev) && !prev.Equals(a)) _owner.Remove(prev);
            _remembered[sid] = a;
            _owner[a] = sid;
        }
        return taken;
    }

    /// <summary>Gives an address back; it is quarantined for every player but its own.</summary>
    public void Release(IPAddress a, ulong? steamId, string sessionId)
    {
        if (!_live.Remove(a)) return;
        _quarantine[a] = (_time.GetUtcNow() + _quarantineFor, steamId, sessionId);
    }
}
