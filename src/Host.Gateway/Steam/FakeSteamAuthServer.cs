using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace SourceSharp.Host.Gateway.Steam;

/// <summary>
/// What Steam's backend remembers across game-server restarts, for the fakes: every ticket ever begun and
/// the logon (epoch) that claimed it. A ticket begun by one epoch is "already claimed" for every other,
/// as the real backend answers a replay (plan_steam_auth S-11). Shared by the fake servers of one test,
/// or one gateway process in the fake tier.
/// </summary>
public sealed class FakeSteamBackend
{
    readonly ConcurrentDictionary<string, string> _claims = new();
    readonly ConcurrentDictionary<string, FakeSteamAuthServer> _servers = new();

    /// <summary>The verdict for a ticket; null never answers (an auth that times out). The default reads the
    /// ticket: ASCII <c>deny:&lt;reason&gt;</c> anywhere in it is a deny, anything else is valid.</summary>
    public Func<ulong, byte[], AuthVerdict?> Script { get; set; } = DefaultScript;

    /// <summary>Delay before a verdict, on the server's clock.</summary>
    public TimeSpan VerdictDelay { get; set; }

    public static AuthVerdict? DefaultScript(ulong steamId, byte[] ticket)
    {
        var text = Encoding.ASCII.GetString(ticket);
        var at = text.IndexOf("deny:", StringComparison.Ordinal);
        if (at < 0) return AuthVerdict.Valid(steamId);
        var reason = new string(text[(at + 5)..].TakeWhile(c => c is >= ' ' and <= '~').ToArray());
        return new AuthVerdict(AuthOutcome.Denied, reason.Length > 0 ? reason : "denied");
    }

    internal static string Hash(ReadOnlySpan<byte> ticket) => Convert.ToHexString(SHA256.HashData(ticket));

    /// <summary>Claims the ticket for <paramref name="epoch"/>; false when another epoch claimed it first.</summary>
    internal bool Claim(string hash, string epoch) => _claims.GetOrAdd(hash, epoch) == epoch;

    internal void Register(FakeSteamAuthServer server) => _servers[server.Status.Epoch] = server;

    /// <summary>Steam withdraws a validated auth (VAC, a cancelled ticket, a kick): every server holding one for the SteamID hears it.</summary>
    public void Revoke(ulong steamId, string reason)
    {
        foreach (var s in _servers.Values) s.RevokeSteamId(steamId, reason);
    }
}

/// <summary>
/// A scriptable Steam game server (plan_steam_auth S-16) for facts and the fake tier
/// (<c>Gateway:Steam:Mode = Fake</c>). It logs every call in <see cref="Calls"/>, in order
/// (<c>begin:&lt;session&gt;</c>, <c>end:&lt;session&gt;</c>), so facts can assert that an End came before a Begin.
/// </summary>
public sealed class FakeSteamAuthServer : ISteamAuthServer
{
    readonly FakeSteamBackend _backend;
    readonly TimeProvider _time;
    readonly Lock _sync = new();
    readonly Dictionary<string, ulong> _active = [];
    readonly ConcurrentQueue<SteamPacket> _outgoing = new();
    SteamAuthStatus _status;

    public FakeSteamAuthServer(FakeSteamBackend backend, TimeProvider time, ulong gameServerSteamId = 90071992547409920UL)
    {
        _backend = backend;
        _time = time;
        _status = new SteamAuthStatus(false, gameServerSteamId, Guid.NewGuid().ToString("N")[..12]);
        backend.Register(this);
    }

    public SteamAuthStatus Status { get { lock (_sync) return _status; } }
    public ConcurrentQueue<string> Calls { get; } = new();
    public ConcurrentQueue<(byte[] Packet, IPEndPoint From)> Incoming { get; } = new();
    public event Action<string, string>? Revoked;

    /// <summary>The sessions holding an auth now.</summary>
    public IReadOnlyDictionary<string, ulong> Active { get { lock (_sync) return new Dictionary<string, ulong>(_active); } }

    public Task StartAsync(CancellationToken ct)
    {
        SetLoggedOn(true);
        return Task.CompletedTask;
    }

    /// <summary>Steam goes away or comes back (S-8).</summary>
    public void SetLoggedOn(bool on)
    {
        lock (_sync) _status = _status with { LoggedOn = on };
    }

    public async Task<AuthVerdict> BeginAsync(string sessionId, ulong steamId, ReadOnlyMemory<byte> ticket, CancellationToken ct)
    {
        string? older;
        lock (_sync)
        {
            if (!_status.LoggedOn) return new AuthVerdict(AuthOutcome.Unavailable, "not logged on");
            older = _active.FirstOrDefault(a => a.Value == steamId && a.Key != sessionId).Key;
        }
        if (older is not null) End(older); // one auth per SteamID, the older ended first
        Calls.Enqueue($"begin:{sessionId}");
        if (!_backend.Claim(FakeSteamBackend.Hash(ticket.Span), Status.Epoch))
            return new AuthVerdict(AuthOutcome.AlreadyClaimed, "ticket already used");
        lock (_sync) _active[sessionId] = steamId;
        if (_backend.VerdictDelay > TimeSpan.Zero) await Task.Delay(_backend.VerdictDelay, _time, ct);
        var verdict = _backend.Script(steamId, ticket.ToArray());
        if (verdict is null) await Task.Delay(Timeout.InfiniteTimeSpan, _time, ct); // never answers; ct ends it
        if (verdict is not { Ok: true }) lock (_sync) _active.Remove(sessionId);
        return verdict!;
    }

    public void End(string sessionId)
    {
        lock (_sync)
            if (!_active.Remove(sessionId)) return;
        Calls.Enqueue($"end:{sessionId}");
    }

    internal void RevokeSteamId(ulong steamId, string reason)
    {
        List<string> hit;
        lock (_sync)
        {
            hit = _active.Where(a => a.Value == steamId).Select(a => a.Key).ToList();
            foreach (var s in hit) _active.Remove(s);
        }
        foreach (var s in hit) Revoked?.Invoke(s, reason);
    }

    public void HandleIncoming(ReadOnlySpan<byte> packet, IPEndPoint from) => Incoming.Enqueue((packet.ToArray(), from));

    /// <summary>Queues a packet the "library" wants sent (a heartbeat, a query reply).</summary>
    public void Send(byte[] data, IPEndPoint to) => _outgoing.Enqueue(new SteamPacket(data, to));

    public Task<IReadOnlyList<SteamPacket>> DrainOutgoingAsync()
    {
        var all = new List<SteamPacket>();
        while (_outgoing.TryDequeue(out var p)) all.Add(p);
        return Task.FromResult<IReadOnlyList<SteamPacket>>(all);
    }

    /// <summary>The last listing applied, and whether it was advertised.</summary>
    public (SteamListing Listing, bool Advertise)? Listing { get; private set; }

    public void ApplyListing(SteamListing listing, bool advertise) => Listing = (listing, advertise);

    public ValueTask DisposeAsync()
    {
        SetLoggedOn(false);
        return ValueTask.CompletedTask;
    }
}
