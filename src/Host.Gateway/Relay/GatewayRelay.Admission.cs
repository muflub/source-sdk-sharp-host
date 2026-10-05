using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using SourceSharp.Host.Gateway.Admission;
using SourceSharp.Host.Gateway.Steam;
using SourceSharp.Host.Proto.Gateway;

namespace SourceSharp.Host.Gateway.Relay;

/// <summary>
/// Admission (plan_steam_auth): address and SteamID bans enforced here (S-14), temp bans from repeated
/// failures (S-15), one connection per SteamID with the oldest winning until it times out (S-17), and
/// validate-then-forward: a client's connect is held until Steam has validated its ticket, and nothing past
/// getchallenge reaches a backend before that (S-3, S-5). With Steam off (<c>_steam</c> null) none of it runs
/// except the bans, and the relay behaves as before.
/// </summary>
public sealed partial class GatewayRelay
{
    readonly ISteamAuthServer? _steam;
    readonly BanList _bans = new();
    readonly StrikeTable _strikes;

    /// <summary>The Steam game server's identity, stamped into every challenge the client sees (S-4).</summary>
    ulong GameServerSteamId => _steam?.Status.GameServerSteamId ?? 0;

    static string Hash(ReadOnlySpan<byte> ticket) => Convert.ToHexString(SHA256.HashData(ticket));

    /// <summary>Called first for every client datagram: true when the source address is banned (dropped silently, S-14).</summary>
    bool AddressBanned(IPEndPoint client)
    {
        if (_bans.Address(client.Address, _time.GetUtcNow()) is null) return false;
        Metrics.BannedDropped.Inc();
        return true;
    }

    /// <summary>
    /// The Steam gate for a session's datagram; true when it was consumed here. Runs before identity-first and
    /// the plain relay path, and only when Steam is on.
    /// </summary>
    bool SteamGate(Session s, ReadOnlySpan<byte> p)
    {
        if (s.Auth == AuthState.Revoked) return true; // upstream stopped (S-12)
        var relaying = s.Auth is AuthState.Valid or AuthState.Reauthing;
        if (!IsConnectionless(p)) return !relaying; // netchannel traffic only after validation
        if (_codec.IsGetChallenge(p)) return false; // the backend (or identity-first) answers it
        if (!_codec.TryReadConnect(p, out var challenge, out _)) return !relaying;

        var identityFirstConnect = _o.IdentityFirst && s.Phase == IdentityPhase.Challenged && challenge == s.GatewayChallenge;
        if (_o.IdentityFirst && s.Phase == IdentityPhase.Identifying) return true; // a resend while identifying
        if (s.Auth == AuthState.Validating) return true;                             // a resend while Steam decides
        if (!identityFirstConnect && relaying && _codec.TryReadTicket(p, out _, out var r) && Hash(p[r]) == s.TicketHash)
            return false; // the validated ticket again: a hop, or identity-first's second connect (S-13: no Steam call)

        Admit(s, p.ToArray(), identityFirstConnect);
        return true;
    }

    void Refuse(Session s, byte[] connect, string reason, string metricReason)
    {
        Metrics.ConnectsRefused.WithLabels(metricReason).Inc();
        ReplyToClient(s, _codec.Reject(connect, reason));
    }

    /// <summary>A failure that counts toward temp bans (S-15). A SteamID only collects strikes from an unvalidated
    /// connect when its address is failing too, so a forged SteamID cannot get a stranger banned.</summary>
    void Strike(Session s, ulong? steamId, bool validated, string reason)
    {
        var now = _time.GetUtcNow();
        var ip = s.Client.Address.ToString();
        var ipFailing = _strikes.Pending(BanKind.Address, ip, now) > 0;
        Issue(_strikes.Strike(BanKind.Address, ip, now, reason));
        if (steamId is { } sid && (validated || ipFailing))
            Issue(_strikes.Strike(BanKind.SteamId, sid.ToString(), now, reason));
    }

    void Issue(BanEntry? ban)
    {
        if (ban is null) return;
        _bans.AddLocal(ban);
        Metrics.TempBans.WithLabels(ban.Kind == BanKind.Address ? "address" : "steamid").Inc();
        _log.LogWarning("temp ban {Kind} {Value} until {Until}: {Reason}", ban.Kind, ban.Value, ban.Until, ban.Reason);
        _events.TempBanned(new TempBannedEvent { Ban = ToProto(ban), Strikes = _o.TempBan.Ip.Failures });
        BanLiveSessions();
    }

    /// <summary>The live session holding <paramref name="steamId"/>, other than <paramref name="except"/>: one being admitted or
    /// holding an auth. A session whose auth ended (the player left for good, EndAuth) or was revoked holds nothing, so a
    /// player who left can rejoin at once from a new address (S-13, S-17).</summary>
    Session? Holder(ulong steamId, Session except) =>
        _byKey.Values.FirstOrDefault(x => x != except && x.PresenceSteamId == steamId
                                          && x.Auth is AuthState.Validating or AuthState.Valid or AuthState.Reauthing);

    /// <summary>Validate-then-forward (S-5): checks that need no Steam call, then the Steam verdict, then Identify.</summary>
    void Admit(Session s, byte[] connect, bool identityFirst)
    {
        var now = _time.GetUtcNow();
        if (!_codec.TryReadTicket(connect, out var sid, out var range))
        {
            Refuse(s, connect, "Steam ticket unreadable", "unreadable");
            Strike(s, null, false, "unreadable ticket");
            return;
        }
        if (_bans.SteamId(sid, now) is { } ban)
        {
            Refuse(s, connect, "banned", "banned");
            Strike(s, sid, false, "banned steamid");
            _log.LogInformation("session {Session}: SteamID {SteamId} is banned ({Reason})", s.Id, sid, ban.Reason);
            return;
        }
        if (!PresenceAllows(s, connect, sid)) return;
        if (_steam is not { Status.LoggedOn: true } steam)
        {
            Refuse(s, connect, _o.Steam.UnavailableReason, "steam_unavailable");
            return;
        }

        var attempt = ++s.AuthAttempt;
        s.Auth = AuthState.Validating;
        s.PendingSteamId = sid;
        if (identityFirst) s.Phase = IdentityPhase.Identifying;
        var ticket = connect[range];
        var hash = Hash(ticket);
        var epoch = steam.Status.Epoch;
        var timeout = new CancellationTokenSource(_o.Steam.AuthTimeout, _time);
        _ = Task.Run(async () =>
        {
            AuthVerdict verdict;
            try { verdict = await steam.BeginAsync(s.Id, sid, ticket, timeout.Token); }
            catch (OperationCanceledException) { verdict = new AuthVerdict(AuthOutcome.Timeout, "Steam did not answer in time"); }
            catch (Exception e) { verdict = new AuthVerdict(AuthOutcome.Unavailable, e.Message); }
            finally { timeout.Dispose(); }
            lock (_sync) OnVerdict(s, attempt, sid, hash, epoch, verdict, connect, identityFirst);
        });
    }

    /// <summary>S-17: the oldest connection wins until it has been silent for <c>PresenceTimeout</c>.</summary>
    bool PresenceAllows(Session s, byte[] connect, ulong sid)
    {
        if (Holder(sid, s) is not { } holder) return true;
        var silent = _time.GetUtcNow() - holder.LastSeen;
        if (silent >= _o.PresenceTimeout)
        {
            Close(holder, "presence timeout: the same SteamID connected again");
            return true;
        }
        var wait = (int)Math.Ceiling((_o.PresenceTimeout - silent).TotalSeconds);
        Refuse(s, connect, $"already connected; try again in {wait} s", "already_connected");
        return false;
    }

    /// <summary>An admission ended without a session: nothing claimed, nothing held at Steam.</summary>
    void Unadmit(Session s)
    {
        s.Auth = AuthState.None;
        s.PendingSteamId = null;
    }

    void OnVerdict(Session s, int attempt, ulong sid, string hash, string epoch, AuthVerdict verdict, byte[] connect, bool identityFirst)
    {
        Metrics.AuthVerdicts.WithLabels(verdict.Outcome.ToString()).Inc();
        if (!_byKey.TryGetValue(s.ClientKey, out var live) || live != s || s.AuthAttempt != attempt)
        {
            if (verdict.Ok) _steam?.End(s.Id); // closed or superseded meanwhile: hold nothing at Steam
            return;
        }
        if (identityFirst && s.Phase == IdentityPhase.Identifying) s.Phase = IdentityPhase.Challenged;
        switch (verdict.Outcome)
        {
            case AuthOutcome.Ok:
                if (_bans.SteamId(sid, _time.GetUtcNow()) is not null)
                {
                    Unadmit(s);
                    _steam?.End(s.Id);
                    Refuse(s, connect, "banned", "banned");
                    Strike(s, sid, true, "banned steamid");
                    return;
                }
                if (Holder(sid, s) is not null && !PresenceAllows(s, connect, sid))
                {
                    Unadmit(s);
                    _steam?.End(s.Id);
                    return;
                }
                IdentifyValidated(s, sid, hash, epoch, connect, identityFirst, claimed: false);
                return;
            case AuthOutcome.AlreadyClaimed:
                IdentifyValidated(s, sid, hash, epoch, connect, identityFirst, claimed: true);
                return;
            case AuthOutcome.Unavailable:
                Unadmit(s);
                Refuse(s, connect, _o.Steam.UnavailableReason, "steam_unavailable");
                return;
            default: // Denied, Timeout
                Unadmit(s);
                _steam?.End(s.Id);
                Refuse(s, connect, verdict.Outcome == AuthOutcome.Timeout ? "Steam auth timed out" : $"Steam auth failed: {verdict.Reason}",
                       verdict.Outcome == AuthOutcome.Timeout ? "steam_timeout" : "steam_denied");
                Strike(s, sid, false, verdict.Reason);
                return;
        }
    }

    /// <summary>The service's say (bans, single presence, the route), with the verdict for its steam_auth row.</summary>
    void IdentifyValidated(Session s, ulong sid, string hash, string epoch, byte[] connect, bool identityFirst, bool claimed)
    {
        s.Phase = identityFirst ? IdentityPhase.Identifying : s.Phase;
        var request = new IdentifyRequest
        {
            RequestId = Guid.NewGuid().ToString("N"),
            SessionId = s.Id,
            ClientAddr = s.ClientKey,
            Steamid = sid.ToString(),
            SteamValidated = !claimed,
            TicketSha256 = hash,
            Epoch = epoch,
            GsSteamid = GameServerSteamId,
            TicketClaimed = claimed,
        };
        var attempt = s.AuthAttempt;
        _ = Task.Run(async () =>
        {
            IdentifyResponse? r = null;
            try { r = await _events.IdentifyAsync(request, _stop.Token); }
            catch (OperationCanceledException) { return; }
            catch (Exception e) { _log.LogWarning("Identify for {Session} failed ({Error})", s.Id, e.Message); }
            lock (_sync) OnValidatedIdentified(s, attempt, sid, hash, epoch, r, connect, identityFirst, claimed);
        });
    }

    void OnValidatedIdentified(Session s, int attempt, ulong sid, string hash, string epoch, IdentifyResponse? r, byte[] connect,
                               bool identityFirst, bool claimed)
    {
        if (!_byKey.TryGetValue(s.ClientKey, out var live) || live != s || s.AuthAttempt != attempt) { _steam?.End(s.Id); return; }
        if (claimed && r is not { Allowed: true, Reauth: true })
        {
            // S-11: a claimed ticket the service does not vouch for. Not a strike: a restart caused it.
            Unadmit(s);
            if (identityFirst) s.Phase = IdentityPhase.Challenged;
            Refuse(s, connect, "please reconnect", "claimed");
            return;
        }
        if (r is { Allowed: false })
        {
            Unadmit(s);
            _steam?.End(s.Id);
            var reason = string.IsNullOrEmpty(r.Reason) ? "refused" : r.Reason;
            Refuse(s, connect, reason, "identify_refused");
            Close(s, $"identify refused: {reason}");
            return;
        }
        if (Holder(sid, s) is { } other && other.SteamId == sid)
        {
            // Admitted elsewhere while this one was identifying: the oldest keeps it (S-17).
            Unadmit(s);
            _steam?.End(s.Id);
            Refuse(s, connect, "already connected", "already_connected");
            return;
        }
        // Steam vouched for the SteamID; a silent service only loses the route, not the identity.
        if (r is not null)
            foreach (var id in r.CloseSessions)
                if (_byKey.Values.FirstOrDefault(x => x.Id == id && x != s) is { } old) Close(old, "taken over");
        s.SteamId = sid;
        s.PendingSteamId = null;
        s.TicketHash = hash;
        s.Epoch = epoch;
        s.Auth = claimed ? AuthState.Reauthing : AuthState.Valid;
        if (r is not null && !string.IsNullOrEmpty(r.Backend) && IPEndPoint.TryParse(r.Backend, out var backend)) s.Route = backend;
        if (r is not null && r.TableVersion > _version) _version = r.TableVersion;
        if (claimed) RequestReauth(s);
        if (identityFirst) ReplayGetChallenge(s);
        else Forward(s, connect);
    }

    /// <summary>Asks the session's client for a fresh ticket through the game (S-20); the service carries it. A new
    /// nonce every time: an answer to an earlier ask is stale.</summary>
    void RequestReauth(Session s)
    {
        s.ReauthNonce = Guid.NewGuid().ToString("N");
        s.ReauthDeadline = _time.GetUtcNow() + _o.Steam.ReauthTimeout;
        s.ReauthAsks++;
        _events.ReauthNeeded(new ReauthNeededEvent
        {
            SessionId = s.Id,
            Steamid = s.SteamId?.ToString() ?? "",
            GsSteamid = GameServerSteamId,
            Nonce = s.ReauthNonce,
            Backend = (s.Link?.Backend ?? TargetOf(s))?.ToString() ?? "",
            Peer = s.Link?.Peer?.ToString() ?? "",
        });
    }

    /// <summary>
    /// Sessions re-authing after a restart (S-11): asked once Steam is logged on, asked again when the answer is
    /// late, and once <c>ReauthAttempts</c> went unanswered either kicked to reconnect or trusted until they leave
    /// (<c>ReauthUnanswered</c>). While Steam is unavailable nobody is asked and nobody is kicked (S-8).
    /// </summary>
    void SweepReauth(DateTimeOffset now)
    {
        if (_steam is not { Status.LoggedOn: true }) return;
        foreach (var s in _byKey.Values.Where(x => x.Auth == AuthState.Reauthing).ToList())
        {
            if (s.ReauthNonce is not null && now < s.ReauthDeadline) continue;
            if (s.ReauthAsks < _o.Steam.ReauthAttempts) { RequestReauth(s); continue; }
            if (_o.Steam.ReauthUnanswered == ReauthUnanswered.Trust)
            {
                _log.LogWarning("session {Session} ({SteamId}) never sent a fresh ticket; trusted until it leaves", s.Id, s.SteamId);
                s.Auth = AuthState.Valid;
                s.ReauthNonce = null;
            }
            else Revoke(s, "please reconnect: no fresh Steam ticket", strike: false);
        }
    }

    /// <summary>The client's fresh ticket for the open ask (S-20), validated as a connect's would be.</summary>
    public async Task<ReauthResponse> ReauthAsync(string sessionId, string nonce, byte[] ticket)
    {
        Session? s;
        ulong sid;
        ISteamAuthServer steam;
        lock (_sync)
        {
            s = _byKey.Values.FirstOrDefault(x => x.Id == sessionId);
            if (s is not { Auth: AuthState.Reauthing, SteamId: { } id } || s.ReauthNonce != nonce || _steam is null)
                return new ReauthResponse { Outcome = "Stale", Reason = "no open request for a ticket with that nonce" };
            sid = id;
            steam = _steam;
        }
        AuthVerdict verdict;
        using (var timeout = new CancellationTokenSource(_o.Steam.AuthTimeout, _time))
        {
            try { verdict = await steam.BeginAsync(sessionId, sid, ticket, timeout.Token); }
            catch (OperationCanceledException) { verdict = new AuthVerdict(AuthOutcome.Timeout, "Steam did not answer in time"); }
        }
        lock (_sync)
        {
            Metrics.AuthVerdicts.WithLabels(verdict.Outcome.ToString()).Inc();
            if (!_byKey.TryGetValue(s.ClientKey, out var live) || live != s || s.ReauthNonce != nonce)
            {
                if (verdict.Ok) steam.End(sessionId);
                return new ReauthResponse { Outcome = "Stale", Reason = "the session moved on while Steam decided" };
            }
            switch (verdict.Outcome)
            {
                case AuthOutcome.Ok:
                    s.Auth = AuthState.Valid;
                    s.TicketHash = Hash(ticket);
                    s.Epoch = steam.Status.Epoch;
                    s.ReauthNonce = null;
                    s.ReauthAsks = 0;
                    return new ReauthResponse { Outcome = "Ok", TicketSha256 = s.TicketHash, Epoch = s.Epoch, GsSteamid = GameServerSteamId };
                case AuthOutcome.Denied or AuthOutcome.AlreadyClaimed:
                    Revoke(s, $"Steam: {verdict.Reason}", strike: verdict.Outcome == AuthOutcome.Denied);
                    return new ReauthResponse { Outcome = verdict.Outcome.ToString(), Reason = verdict.Reason };
                default: // Timeout, Unavailable: the sweep asks again or gives up
                    return new ReauthResponse { Outcome = verdict.Outcome.ToString(), Reason = verdict.Reason };
            }
        }
    }

    /// <summary>Steam withdrew a session's auth, or a ban now covers it: stop upstream now, close after the grace (S-12).</summary>
    void Revoke(Session s, string reason, bool strike)
    {
        if (s.Auth == AuthState.Revoked) return;
        s.Auth = AuthState.Revoked;
        s.RevokeReason = reason;
        s.RevokedAt = _time.GetUtcNow();
        Metrics.Revocations.Inc();
        _steam?.End(s.Id);
        _log.LogWarning("session {Session} ({SteamId}) revoked: {Reason}", s.Id, s.SteamId, reason);
        _events.AuthRevoked(new AuthRevokedEvent
        {
            SessionId = s.Id,
            ClientAddr = s.ClientKey,
            Steamid = s.SteamId?.ToString() ?? "",
            Backend = (s.Link?.Backend ?? TargetOf(s))?.ToString() ?? "",
            Reason = reason,
        });
        if (strike && s.SteamId is { } sid) Issue(_strikes.Strike(BanKind.SteamId, sid.ToString(), _time.GetUtcNow(), reason));
    }

    void OnSteamRevoked(string sessionId, string reason)
    {
        lock (_sync)
            if (_byKey.Values.FirstOrDefault(x => x.Id == sessionId) is { } s) Revoke(s, $"Steam: {reason}", strike: true);
    }

    /// <summary>Every live session a ban now covers is revoked (S-14: a new ban kicks).</summary>
    void BanLiveSessions()
    {
        var now = _time.GetUtcNow();
        foreach (var s in _byKey.Values.ToList())
            if (s.Auth != AuthState.Revoked
                && (_bans.Address(s.Client.Address, now) is not null || (s.SteamId is { } sid && _bans.SteamId(sid, now) is not null)))
                Revoke(s, "banned", strike: false);
    }

    /// <summary>Revoked sessions close once the kick has had its grace to reach the client.</summary>
    void SweepRevoked(DateTimeOffset now)
    {
        SweepReauth(now);
        foreach (var s in _byKey.Values.Where(x => x.Auth == AuthState.Revoked && now - x.RevokedAt >= _o.Steam.KickGrace).ToList())
            Close(s, $"revoked: {s.RevokeReason}");
        _strikes.Prune(now);
    }

    // ---------------------------------------------------------------- Steam's UDP through the public socket (S-19)

    ITimer? _steamTick;
    int _draining;

    /// <summary>Meant for the Steam library: connectionless, not the handshake, and not A2S unless the library answers it.</summary>
    bool SteamBound(ReadOnlySpan<byte> p) =>
        IsConnectionless(p) && p.Length >= 5
        && !_codec.IsGetChallenge(p) && !_codec.IsChallenge(p) && !_codec.TryReadConnect(p, out _, out _)
        && (!SourceSharp.Host.Gateway.A2s.A2sResponder.IsQuery(p[4]) || _o.Steam.A2s == A2sAnswerer.Library);

    /// <summary>Hands a datagram to the library with its sender (after the ban check, under the per-IP rate) and sends its replies.</summary>
    void ToSteam(System.Net.SocketAddress from, ReadOnlySpan<byte> p)
    {
        if (_steam is not { Status.LoggedOn: true } steam) return;
        if (!HandshakeAllowed(from)) { Metrics.HandshakesRateLimited.Inc(); return; }
        steam.HandleIncoming(p, (IPEndPoint)new IPEndPoint(IPAddress.Any, 0).Create(from));
        _ = DrainSteamAsync();
    }

    /// <summary>Everything the library wants sent goes out of the public socket, so Steam sees one address (S-19).</summary>
    async Task DrainSteamAsync()
    {
        if (_steam is null || Interlocked.Exchange(ref _draining, 1) == 1) return;
        try
        {
            foreach (var packet in await _steam.DrainOutgoingAsync())
                try { _public?.SendTo(packet.Data, SocketFlags.None, packet.To); }
                catch (SocketException) { }
                catch (ObjectDisposedException) { return; }
        }
        catch (Exception e) when (e is not OutOfMemoryException) { _log.LogWarning("Steam outgoing packets: {Error}", e.Message); }
        finally { Interlocked.Exchange(ref _draining, 0); }
    }

    // ---------------------------------------------------------------- control

    static Ban ToProto(BanEntry b) => new()
    {
        Kind = b.Kind == BanKind.Address ? "address" : "steamid",
        Value = b.Value,
        UntilUnixMs = b.Until?.ToUnixTimeMilliseconds() ?? 0,
        Reason = b.Reason,
    };

    static BanEntry? FromProto(Ban b) => b.Kind switch
    {
        "address" => new BanEntry(BanKind.Address, b.Value, b.UntilUnixMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(b.UntilUnixMs) : null, b.Reason),
        "steamid" => new BanEntry(BanKind.SteamId, b.Value, b.UntilUnixMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(b.UntilUnixMs) : null, b.Reason),
        _ => null,
    };

    void ReplaceBans(IEnumerable<Ban> bans, IEnumerable<string> legacyAddresses)
    {
        var entries = bans.Select(FromProto).OfType<BanEntry>().ToList();
        if (entries.Count == 0) entries = legacyAddresses.Select(a => new BanEntry(BanKind.Address, a, null, "admin")).ToList();
        _bans.Replace(entries, _time.GetUtcNow());
        BanLiveSessions();
    }

    /// <summary>The whole ban list (S-14); sessions it now covers are kicked.</summary>
    public ControlStatus SetBans(ulong version, IEnumerable<Ban> bans)
    {
        lock (_sync)
        {
            if (!Fresh(version)) return ControlStatus.Stale;
            _version = version;
            ReplaceBans(bans, []);
            return ControlStatus.Ok;
        }
    }

    /// <summary>The live options (S-21), applied to the next decision; refused whole when any value is invalid.</summary>
    public ControlStatus SetOptions(ulong version, LiveOptions options)
    {
        lock (_sync)
        {
            if (!Fresh(version)) return ControlStatus.Stale;
            if (!TryApply(options)) return ControlStatus.Invalid;
            _version = version;
            return ControlStatus.Ok;
        }
    }

    bool TryApply(LiveOptions o)
    {
        static StrikeRule Rule(Proto.Gateway.StrikeRule? r, StrikeRule current) => r is null ? current : new StrikeRule
        {
            Failures = r.Failures,
            Window = TimeSpan.FromMilliseconds(r.WindowMs),
            Base = TimeSpan.FromMilliseconds(r.BaseMs),
            Cap = TimeSpan.FromMilliseconds(r.CapMs),
            RepeatWindow = TimeSpan.FromMilliseconds(r.RepeatWindowMs),
        };
        var candidate = new GatewayOptions
        {
            PresenceTimeout = TimeSpan.FromMilliseconds(o.PresenceTimeoutMs),
            TempBan = new TempBanOptions { Ip = Rule(o.TempBanIp, _o.TempBan.Ip), SteamId = Rule(o.TempBanSteamid, _o.TempBan.SteamId) },
            Steam = new SteamOptions
            {
                Mode = _o.Steam.Mode, AppId = _o.Steam.AppId, LoginToken = _o.Steam.LoginToken, NativeLibraryPath = _o.Steam.NativeLibraryPath,
                A2s = _o.Steam.A2s, OutgoingTick = _o.Steam.OutgoingTick, LogonRetry = _o.Steam.LogonRetry,
                AuthTimeout = TimeSpan.FromMilliseconds(o.AuthTimeoutMs),
                ReauthTimeout = TimeSpan.FromMilliseconds(o.ReauthTimeoutMs),
                ReauthAttempts = o.ReauthAttempts,
                ReauthUnanswered = Enum.TryParse<ReauthUnanswered>(o.ReauthUnanswered, out var ru) ? ru : (ReauthUnanswered)(-1),
                ClaimedTrust = TimeSpan.FromMilliseconds(o.ClaimedTrustMs),
                KickGrace = TimeSpan.FromMilliseconds(o.KickGraceMs),
                UnavailableReason = o.UnavailableReason,
            },
        };
        if (!Enum.IsDefined(candidate.Steam.ReauthUnanswered) || GatewayOptionsValidator.Validate(candidate).Count > 0) return false;
        _o.PresenceTimeout = candidate.PresenceTimeout;
        _o.TempBan = candidate.TempBan;
        _o.Steam = candidate.Steam;
        return true;
    }

    /// <summary>The player left for good (the game's PlayerLeft, never a hop): release the ticket now (S-13).</summary>
    public ControlStatus EndAuth(string sessionId)
    {
        lock (_sync)
        {
            _steam?.End(sessionId);
            if (_byKey.Values.FirstOrDefault(x => x.Id == sessionId) is { } s)
            {
                if (s.Auth == AuthState.Revoked) Close(s, $"revoked: {s.RevokeReason}");
                else if (s.Auth is AuthState.Valid or AuthState.Reauthing) s.Auth = AuthState.None;
            }
            return ControlStatus.Ok;
        }
    }

    /// <summary>The Steam game server's state for /healthz and the admin (Off when there is none).</summary>
    public (SteamMode Mode, SteamAuthStatus Status) SteamState =>
        _steam is null ? (SteamMode.Off, SteamAuthStatus.Off) : (_o.Steam.Mode, _steam.Status);

    /// <summary>Whether <paramref name="ip"/> or <paramref name="steamId"/> is banned now (Dashboard, facts).</summary>
    public bool IsBanned(IPAddress? ip = null, ulong? steamId = null)
    {
        lock (_sync)
        {
            var now = _time.GetUtcNow();
            return (ip is not null && _bans.Address(ip, now) is not null) || (steamId is { } sid && _bans.SteamId(sid, now) is not null);
        }
    }
}
