using System.Buffers.Binary;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SourceSharp.Host.Steam;

namespace SourceSharp.Host.Gateway.Steam;

/// <summary>
/// The part of <see cref="SteamGameServerHost"/> the gateway uses: the seam its facts replace (plan_steam_auth
/// S2 step 2). The real one is <see cref="SteamGameServerHostAdapter"/>; its events reach the
/// <see cref="ISteamGameServerEvents"/> given to the factory.
/// </summary>
public interface IGameServerHost : IAsyncDisposable
{
    Task StartAsync(GameServerConfiguration config, CancellationToken ct);
    Task<PlayerAuthResult> AuthPlayerAsync(SteamId player, byte[] ticket, CancellationToken ct);
    Task EndAuthSessionAsync(SteamId player);
    Task<bool> HandleIncomingPacketAsync(ReadOnlyMemory<byte> packet, uint srcIp, ushort srcPort);
    Task<(byte[] Packet, uint Ip, ushort Port)?> TryGetNextOutgoingPacketAsync();
    Task ApplyListingAsync(SteamListing listing, bool advertise);
}

/// <summary>The real <see cref="IGameServerHost"/>: a <see cref="SteamGameServerHost"/> over libsteam_api.</summary>
public sealed class SteamGameServerHostAdapter(ISteamGameServerEvents events) : IGameServerHost
{
    readonly SteamGameServerHost _host = new(events);
    public Task StartAsync(GameServerConfiguration config, CancellationToken ct) => _host.StartAsync(config, ct);
    public Task<PlayerAuthResult> AuthPlayerAsync(SteamId player, byte[] ticket, CancellationToken ct) => _host.AuthPlayerAsync(player, ticket, ct);
    public Task EndAuthSessionAsync(SteamId player) => _host.EndAuthSessionAsync(player);
    public Task<bool> HandleIncomingPacketAsync(ReadOnlyMemory<byte> packet, uint srcIp, ushort srcPort) => _host.HandleIncomingPacketAsync(packet, srcIp, srcPort);
    public Task<(byte[] Packet, uint Ip, ushort Port)?> TryGetNextOutgoingPacketAsync() => _host.TryGetNextOutgoingPacketAsync();

    /// <summary>The listing, in Valve's order (identity, then metadata, then the advertise flag), on the pump.</summary>
    public async Task ApplyListingAsync(SteamListing l, bool advertise)
    {
        await _host.SetServerNameAsync(l.Name);
        await _host.SetMapNameAsync(l.Map);
        await _host.SetMaxPlayerCountAsync(l.MaxPlayers);
        await _host.SetBotPlayerCountAsync(0);
        await _host.SetGameTagsAsync(l.Tags);
        await _host.SetRegionAsync(l.Region);
        await _host.ClearKeyValuesAsync();
        foreach (var (k, v) in l.Rules) await _host.SetKeyValueAsync(k, v);
        await _host.SetAdvertiseActiveAsync(advertise);
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();
}

/// <summary>
/// <see cref="ISteamAuthServer"/> over the Steamworks game-server API (plan_steam_auth S-1, S-2). Steam keys auth
/// sessions by SteamID; the relay keys them by session. This class keeps the two maps, so that
/// <see cref="End"/> for a session that no longer owns its SteamID's auth (a late leave after a rejoin) ends
/// nothing (S-13), and a revocation reaches the session that holds the auth now (S-12).
/// <para>Logon: anonymous unless a login token is set (S-9), <c>Authentication</c> mode (no VAC: the
/// SourceSharp client is insecure, S-4), and the shared query socket (S-19), so the library opens no game
/// socket of its own. A failed logon is retried in the background; until it succeeds the server is
/// unavailable and new connects are refused with a reason (S-8).</para>
/// </summary>
public sealed class SteamworksAuthServer : ISteamAuthServer, ISteamGameServerEvents
{
    enum State { Pending, Valid }
    sealed record Auth(string SessionId, ulong SteamId, State State);

    readonly Func<ISteamGameServerEvents, IGameServerHost> _factory;
    readonly Func<GameServerConfiguration> _config;
    readonly TimeSpan _retry;
    readonly ILogger _log;
    readonly Lock _sync = new();
    readonly Dictionary<string, Auth> _bySession = [];
    readonly Dictionary<ulong, string> _bySteamId = [];
    readonly CancellationTokenSource _stop = new();
    IGameServerHost? _host;
    SteamAuthStatus _status = SteamAuthStatus.Off;
    Task? _logon;

    public SteamworksAuthServer(Func<ISteamGameServerEvents, IGameServerHost> factory, Func<GameServerConfiguration> config,
                                TimeSpan? logonRetry = null, ILogger? log = null)
    {
        _factory = factory;
        _config = config;
        _retry = logonRetry ?? TimeSpan.FromSeconds(30);
        _log = log ?? NullLogger.Instance;
    }

    public SteamAuthStatus Status { get { lock (_sync) return _status; } }
    public event Action<string, string>? Revoked;

    public Task StartAsync(CancellationToken ct)
    {
        _logon = Task.Run(() => LogonLoop(_stop.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    async Task LogonLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var host = _factory(this);
            try
            {
                lock (_sync) _status = SteamAuthStatus.Off with { Epoch = Guid.NewGuid().ToString("N")[..12] };
                await host.StartAsync(_config(), ct);
                (SteamListing, bool)? pending;
                lock (_sync) { _host = host; pending = _listing; }
                if (pending is { } p) await ApplyAsync(host, p.Item1, p.Item2);
                return;
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                _log.LogWarning("Steam logon failed ({Error}); new connects are refused until it succeeds, retrying in {Retry}", e.Message, _retry);
                await host.DisposeAsync();
                try { await Task.Delay(_retry, ct); } catch (OperationCanceledException) { return; }
            }
            catch (OperationCanceledException) { await host.DisposeAsync(); return; }
        }
    }

    public async Task<AuthVerdict> BeginAsync(string sessionId, ulong steamId, ReadOnlyMemory<byte> ticket, CancellationToken ct)
    {
        IGameServerHost host;
        bool releaseOlder;
        lock (_sync)
        {
            if (_host is null || !_status.LoggedOn) return new AuthVerdict(AuthOutcome.Unavailable, "not logged on to Steam");
            host = _host;
            // One auth per SteamID, by construction: the older session's entry goes in the same step as this
            // one's arrives, so the two maps never disagree. Steam would answer DuplicateRequest otherwise.
            releaseOlder = _bySteamId.Remove(steamId, out var older) && _bySession.Remove(older);
            if (_bySession.Remove(sessionId, out var mine)) _bySteamId.Remove(mine.SteamId);
            _bySession[sessionId] = new Auth(sessionId, steamId, State.Pending);
            _bySteamId[steamId] = sessionId;
        }
        // Queued on the pump before the Begin below, so Steam sees End then Begin.
        if (releaseOlder) await host.EndAuthSessionAsync(new SteamId(steamId));

        PlayerAuthResult r;
        try { r = await host.AuthPlayerAsync(new SteamId(steamId), ticket.ToArray(), ct); }
        catch
        {
            Forget(sessionId); // the SDK ended the native session when ct fired
            throw;
        }

        var verdict = Map(r);
        lock (_sync)
        {
            var current = _bySession.TryGetValue(sessionId, out var a) && a.SteamId == steamId;
            if (verdict.Ok && current) { _bySession[sessionId] = a! with { State = State.Valid }; return verdict; }
        }
        // Denied, or validated for a session that was ended meanwhile: hold nothing at Steam.
        if (Forget(sessionId) || verdict.Ok) _ = host.EndAuthSessionAsync(new SteamId(steamId));
        return verdict;
    }

    static AuthVerdict Map(PlayerAuthResult r)
    {
        if (r.Immediate != BeginAuthSessionResult.Ok)
            return new AuthVerdict(AuthOutcome.Denied, r.Immediate switch
            {
                BeginAuthSessionResult.InvalidTicket => "invalid ticket",
                BeginAuthSessionResult.DuplicateRequest => "duplicate auth request",
                BeginAuthSessionResult.InvalidVersion => "ticket version not supported",
                BeginAuthSessionResult.GameMismatch => "ticket for another game",
                BeginAuthSessionResult.ExpiredTicket => "ticket expired",
                _ => r.Immediate.ToString(),
            });
        var v = r.Validation;
        if (v is null) return new AuthVerdict(AuthOutcome.Denied, "no verdict");
        return v.Response switch
        {
            AuthSessionResponse.Ok => AuthVerdict.Valid(v.OwnerSteamId.Value),
            AuthSessionResponse.AuthTicketInvalidAlreadyUsed => new AuthVerdict(AuthOutcome.AlreadyClaimed, "ticket already used"),
            _ => new AuthVerdict(AuthOutcome.Denied, Describe(v.Response)),
        };
    }

    static string Describe(AuthSessionResponse r) => r switch
    {
        AuthSessionResponse.UserNotConnectedToSteam => "not connected to Steam",
        AuthSessionResponse.NoLicenseOrExpired => "no license",
        AuthSessionResponse.VacBanned => "VAC banned",
        AuthSessionResponse.LoggedInElseWhere => "logged in elsewhere",
        AuthSessionResponse.VacCheckTimedOut => "VAC check timed out",
        AuthSessionResponse.AuthTicketCanceled => "ticket canceled",
        AuthSessionResponse.AuthTicketInvalid => "ticket invalid",
        AuthSessionResponse.PublisherIssuedBan => "banned by the publisher",
        _ => r.ToString(),
    };

    /// <summary>Drops a session's bookkeeping; true when it held an auth. The maps agree by construction
    /// (<see cref="BeginAsync"/>), so the session's SteamID entry is its own.</summary>
    bool Forget(string sessionId)
    {
        lock (_sync)
        {
            if (!_bySession.Remove(sessionId, out var a)) return false;
            _bySteamId.Remove(a.SteamId);
            return true;
        }
    }

    public void End(string sessionId)
    {
        ulong steamId;
        IGameServerHost? host;
        lock (_sync)
        {
            if (!_bySession.TryGetValue(sessionId, out var a)) return;
            steamId = a.SteamId;
            host = _host;
        }
        if (Forget(sessionId)) _ = host?.EndAuthSessionAsync(new SteamId(steamId));
    }

    void Revoke(SteamId player, string reason)
    {
        string? session;
        lock (_sync)
            session = _bySteamId.TryGetValue(player.Value, out var s) && _bySession[s].State == State.Valid ? s : null;
        if (session is null) return; // a verdict still pending is the Begin's to report
        End(session);
        Revoked?.Invoke(session, reason);
    }

    // ---------------------------------------------------------------- Steam's callbacks (pump thread)

    void ISteamGameServerLifecycleEvents.OnLoggedOn(SteamId serverSteamId, bool secure)
    {
        lock (_sync) _status = _status with { LoggedOn = true, GameServerSteamId = serverSteamId.Value };
        _log.LogInformation("Steam game server logged on as {SteamId} (epoch {Epoch})", serverSteamId, Status.Epoch);
    }

    void ISteamGameServerLifecycleEvents.OnLoggedOff()
    {
        lock (_sync) _status = _status with { LoggedOn = false };
        _log.LogWarning("Steam game server logged off; new connects are refused until it is back");
    }

    void ISteamGameServerAuthEvents.OnAuthValidation(AuthValidation validation)
    {
        if (validation.Response != AuthSessionResponse.Ok) Revoke(validation.Player, Describe(validation.Response));
    }

    void ISteamGameServerClientEvents.OnClientDenied(SteamId player, DenyReason reason, string optionalText) =>
        Revoke(player, string.IsNullOrEmpty(optionalText) ? reason.ToString() : optionalText);

    void ISteamGameServerClientEvents.OnClientKickRequested(SteamId player, DenyReason reason) => Revoke(player, $"kicked by Steam: {reason}");

    void ISteamGameServerDiagnosticsEvents.OnHostError(Exception error) => _log.LogWarning("Steam: {Error}", error.Message);

    // ---------------------------------------------------------------- the shared query socket (S-19)

    /// <summary>The SDK's addresses are IPv4 in host byte order.</summary>
    public static uint HostOrder(IPAddress a) => BinaryPrimitives.ReadUInt32BigEndian(a.MapToIPv4().GetAddressBytes());

    public static IPAddress FromHostOrder(uint ip)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, ip);
        return new IPAddress(b);
    }

    public void HandleIncoming(ReadOnlySpan<byte> packet, IPEndPoint from)
    {
        IGameServerHost? host;
        lock (_sync) host = _host;
        if (host is null) return;
        _ = host.HandleIncomingPacketAsync(packet.ToArray(), HostOrder(from.Address), (ushort)from.Port);
    }

    public async Task<IReadOnlyList<SteamPacket>> DrainOutgoingAsync()
    {
        IGameServerHost? host;
        lock (_sync) host = _host;
        var all = new List<SteamPacket>();
        if (host is null) return all;
        while (await host.TryGetNextOutgoingPacketAsync() is { } p)
            all.Add(new SteamPacket(p.Packet, new IPEndPoint(FromHostOrder(p.Ip), p.Port)));
        return all;
    }

    /// <summary>The listing goes to the logged-on host; one applied before logon waits for it.</summary>
    public void ApplyListing(SteamListing listing, bool advertise)
    {
        IGameServerHost? host;
        lock (_sync) { _listing = (listing, advertise); host = _host; }
        if (host is not null) _ = ApplyAsync(host, listing, advertise);
    }

    (SteamListing, bool)? _listing;

    async Task ApplyAsync(IGameServerHost host, SteamListing listing, bool advertise)
    {
        try { await host.ApplyListingAsync(listing, advertise); }
        catch (Exception e) { _log.LogWarning("Steam listing not applied: {Error}", e.Message); }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_logon is not null) await _logon;
        IGameServerHost? host;
        lock (_sync) { host = _host; _host = null; _status = _status with { LoggedOn = false }; }
        if (host is not null) await host.DisposeAsync();
    }
}
