using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace SourceSharp.Host.Relay;

/// <summary>One relayed client in Interpose mode: the engine sees its real address.</summary>
public sealed class InterposeSession(IPEndPoint client, string sessionId, ulong? steamId, DateTimeOffset opened, Func<byte[], bool> down)
{
    public IPEndPoint Client { get; } = client;
    public ulong Key { get; } = InterposeSwitchboard.KeyOf(client);
    public string SessionId { get; } = sessionId;
    public ulong? SteamId { get; } = steamId;
    public DateTimeOffset Opened { get; } = opened;
    /// <summary>Queues a datagram down this session's stream; false when the stream is gone. Never blocks.</summary>
    public Func<byte[], bool> Down { get; } = down;
    public CancellationTokenSource Cut { get; } = new();
    public string? CutReason { get; set; }
}

/// <summary>
/// Interpose mode (<c>Relay.Mode = Interpose</c>): the meeting point of the PeerRelay streams and the
/// engine's interposed <c>recvfrom</c> / <c>sendto</c> on its game socket. Datagrams from clients wait
/// in one queue the engine drains; datagrams the engine sends to a live client's real address go down
/// that client's stream. Lock-free on the datagram path (the engine's threads call it) and never
/// blocking; addresses are keyed as (IPv4, port) without allocation.
/// </summary>
public sealed class InterposeSwitchboard
{
    readonly ConcurrentDictionary<ulong, InterposeSession> _live = new();
    readonly ConcurrentDictionary<ulong, InterposeSession> _bySteam = new();
    readonly ConcurrentQueue<(ulong From, byte[] Payload)> _toEngine = new();

    /// <summary>(IPv4 in network order, port) packed: address in the high 32 bits, port in the low 16.</summary>
    public static ulong KeyOf(uint addrNetworkOrder, ushort port) => ((ulong)addrNetworkOrder << 16) | port;

    public static ulong KeyOf(IPEndPoint ep)
    {
        if (ep.AddressFamily != AddressFamily.InterNetwork) throw new ArgumentException("IPv4 only (the engine's netadr_t is IPv4)");
        Span<byte> b = stackalloc byte[4];
        ep.Address.TryWriteBytes(b, out _);
        return KeyOf(BinaryPrimitives.ReadUInt32BigEndian(b), (ushort)ep.Port);
    }

    public static IPEndPoint EndPointOf(ulong key)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)(key >> 16));
        return new IPEndPoint(new IPAddress(b), (int)(key & 0xFFFF));
    }

    public int LiveCount => _live.Count;
    public int Queued => _toEngine.Count;

    /// <summary>
    /// Registers a stream. A live session with the same client address or SteamID is cut first
    /// (single presence per instance, as in Loopback mode) and returned.
    /// </summary>
    public (InterposeSession Session, InterposeSession? Superseded) Register(IPEndPoint client, string sessionId, ulong? steamId, DateTimeOffset now, Func<byte[], bool> down)
    {
        var s = new InterposeSession(client, sessionId, steamId, now, down);
        InterposeSession? old = null;
        if (steamId is { } sid && _bySteam.TryGetValue(sid, out var bySteam)) old = bySteam;
        else if (_live.TryGetValue(s.Key, out var byAddr)) old = byAddr;
        if (old is not null)
        {
            old.CutReason = "superseded";
            Unregister(old);
            old.Cut.Cancel();
        }
        _live[s.Key] = s;
        if (steamId is { } k) _bySteam[k] = s;
        return (s, old);
    }

    public void Unregister(InterposeSession s)
    {
        _live.TryRemove(new KeyValuePair<ulong, InterposeSession>(s.Key, s));
        if (s.SteamId is { } sid) _bySteam.TryRemove(new KeyValuePair<ulong, InterposeSession>(sid, s));
    }

    /// <summary>A datagram a client sent (up its stream): queued for the engine's next recvfrom.</summary>
    public void FromClient(InterposeSession s, ReadOnlySpan<byte> payload) => _toEngine.Enqueue((s.Key, payload.ToArray()));

    /// <summary>The engine's next datagram and the real client address it came from.</summary>
    public bool TryTakeForEngine(out ulong from, out byte[] payload)
    {
        if (_toEngine.TryDequeue(out var d)) { from = d.From; payload = d.Payload; return true; }
        from = 0; payload = [];
        return false;
    }

    /// <summary>The engine sent to (addr, port): down that client's stream if it is a live session.</summary>
    public bool ToClient(ulong key, ReadOnlySpan<byte> data) =>
        _live.TryGetValue(key, out var s) && s.Down(data.ToArray());

    public InterposeSession? Resolve(IPEndPoint peer) =>
        peer.AddressFamily == AddressFamily.InterNetwork && _live.TryGetValue(KeyOf(peer), out var s) ? s
        : peer.Port == 0 ? _live.Values.FirstOrDefault(x => x.Client.Address.Equals(peer.Address)) : null;

    public IReadOnlyList<InterposeSession> Live => [.. _live.Values];

    public void CutAll(string reason)
    {
        foreach (var s in Live)
        {
            s.CutReason ??= reason;
            try { s.Cut.Cancel(); } catch (ObjectDisposedException) { }
        }
    }
}
