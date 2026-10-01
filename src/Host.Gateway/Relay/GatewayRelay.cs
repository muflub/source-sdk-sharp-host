using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SourceSharp.Host.Gateway.A2s;
using SourceSharp.Host.Gateway.Events;
using SourceSharp.Host.Gateway.Handshake;
using SourceSharp.Host.Proto.Gateway;
using SourceSharp.Host.Proto.Relay;
using Route = SourceSharp.Host.Proto.Gateway.Route;

namespace SourceSharp.Host.Gateway.Relay;

/// <summary>
/// The UDP relay (plan §8.1, with the sidecar transport ruled 2026-09-30): one public socket; per
/// client address a session with its own PeerRelay stream to the backend pod's sidecar, which gives
/// the session its own loopback address at the engine (relay.proto, docs/net-protocol.md §11);
/// payload-agnostic forwarding; the default route for new and unidentified sessions; route flips as a
/// hard cut applied at the client's next handshake (the stream to A ends before B's is opened, and
/// B gets nothing until it answers Opened); expiry after <c>SessionExpiry</c> of silence.
///
/// <para>All state is guarded by one lock. Datagrams are queued onto a stream under it, so ordering
/// statements ("A's stream is ended before the first datagram is queued to B") hold by construction.</para>
/// </summary>
public sealed class GatewayRelay : IAsyncDisposable
{
    readonly GatewayOptions _o;
    readonly TimeProvider _time;
    readonly IGatewayEvents _events;
    readonly ILogger _log;
    readonly Lock _sync = new();
    readonly Dictionary<SocketAddress, Session> _byAddress = [];
    readonly Dictionary<string, Session> _byKey = [];
    readonly Dictionary<string, Route> _pendingRoutes = [];
    /// <summary>When each pending route was first carried here; a later sync never restarts its expiry.</summary>
    readonly Dictionary<string, DateTimeOffset> _pendingSince = [];
    readonly IHandshakeCodec _codec;
    readonly Dictionary<IPEndPoint, GrpcChannel> _channels = [];
    readonly Func<IPEndPoint, GrpcChannel> _channelFor;
    readonly CancellationTokenSource _stop = new();
    Socket? _public;
    Task? _publicLoop;
    ITimer? _sweep;
    ITimer? _stats;
    ulong _version;
    A2sResponder? _a2s;
    IPEndPoint? _default;
    readonly Dictionary<IPEndPoint, bool> _readiness = [];
    readonly Dictionary<IPAddress, (double Tokens, DateTimeOffset At)> _handshakeBuckets = [];
    long _sessionSeq;
    (long PacketsIn, long PacketsOut, long BytesIn, long BytesOut) _closedTotals;

    public GatewayRelay(GatewayOptions options, TimeProvider time, IGatewayEvents? events = null, ILogger? log = null, GatewayMetrics? metrics = null,
                        Func<IPEndPoint, GrpcChannel>? channelFor = null)
    {
        Metrics = metrics ?? new GatewayMetrics();
        _o = options;
        _time = time;
        _events = events ?? NullGatewayEvents.Instance;
        _log = log ?? NullLogger.Instance;
        _codec = HandshakeCodecs.For(options.Wire);
        _channelFor = channelFor ?? (ep => GrpcChannel.ForAddress($"http://{ep}", new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler { EnableMultipleHttp2Connections = true },
            // A pod's sidecar comes up seconds after the pod is created: reconnect quickly.
            InitialReconnectBackoff = TimeSpan.FromMilliseconds(200),
            MaxReconnectBackoff = TimeSpan.FromSeconds(2),
        }));
    }

    IPEndPoint? _publicEndPoint;
    public IPEndPoint PublicEndPoint => _publicEndPoint ?? throw new InvalidOperationException("not started");
    public ulong TableVersion { get { lock (_sync) return _version; } }
    public GatewayMetrics Metrics { get; }
    public IPEndPoint? DefaultBackend { get { lock (_sync) return _default; } }
    public int SessionCount { get { lock (_sync) return _byKey.Count; } }

    /// <summary>Binds the public socket and starts relaying and sweeping.</summary>
    public void Start()
    {
        _public = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _public.Bind(IPEndPoint.Parse(_o.Public));
        _publicEndPoint = (IPEndPoint)_public.LocalEndPoint!;
        _a2s = new A2sResponder(_publicEndPoint.Port);
        _publicLoop = Task.Run(PublicLoop);
        _sweep = _time.CreateTimer(_ => Sweep(), null, _o.SweepInterval, _o.SweepInterval);
        _stats = _time.CreateTimer(_ => _events.Stats(Stats()), null, _o.StatsInterval, _o.StatsInterval);
        _log.LogInformation("gateway relaying on {Public}", PublicEndPoint);
    }

    // ---------------------------------------------------------------- data path

    async Task PublicLoop()
    {
        var buf = ArrayPool<byte>.Shared.Rent(65536);
        var from = new SocketAddress(AddressFamily.InterNetwork);
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                int n;
                try { n = await _public!.ReceiveFromAsync(buf.AsMemory(), SocketFlags.None, from, _stop.Token); }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { continue; }
                lock (_sync) OnClientPacket(from, buf.AsSpan(0, n));
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    static bool IsConnectionless(ReadOnlySpan<byte> p) =>
        p.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(p) == 0xFFFFFFFF;

    void OnClientPacket(SocketAddress from, ReadOnlySpan<byte> p)
    {
        if (IsConnectionless(p) && p.Length >= 5 && A2sResponder.IsQuery(p[4]))
        {
            // The gateway answers the server queries itself, for the hub; they never create a session (§8.3).
            var reply = _a2s!.Answer(p, (IPEndPoint)new IPEndPoint(IPAddress.Any, 0).Create(from));
            if (reply is not null && reply[4] != A2sResponder.ChallengeReply) Metrics.A2sAnswered.Inc();
            if (reply is not null)
                try { _public!.SendTo(reply, SocketFlags.None, from); } catch (SocketException) { }
            return;
        }
        if (IsConnectionless(p) && _codec.IsGetChallenge(p) && !HandshakeAllowed(from))
        {
            Metrics.HandshakesRateLimited.Inc();
            return;
        }
        if (!_byAddress.TryGetValue(from, out var s)) s = CreateSession(from);
        var now = _time.GetUtcNow();
        s.LastSeen = now;
        s.PacketsIn++;
        s.BytesIn += p.Length;
        Metrics.PacketsIn.Inc();
        Metrics.BytesIn.Inc(p.Length);

        if (_o.IdentityFirst && IsConnectionless(p) && IdentityFirst(s, p)) return;
        Forward(s, p);
    }

    /// <summary>The relay path for one client datagram: route change at a handshake, hold, or send.</summary>
    void Forward(Session s, ReadOnlySpan<byte> p)
    {
        if (IsConnectionless(p))
        {
            // A handshake packet: the one moment a route change takes effect (§8.1: a flip affects
            // only new handshakes). Different target → hard cut: the old socket closes first.
            var target = TargetOf(s);
            if (s.Link is not null && target is not null && !s.Link.Backend.Equals(target))
            {
                Cut(s, "route");
                Metrics.Flips.Inc();
            }
        }

        if (s.Hold is null && s.Link is { Open: true } open) { Send(open, p); return; }
        // No open stream: hold (§8.1b) and open one if the target exists and is ready.
        if (s.Hold is null) StartHold(s);
        HoldPacket(s, p);
        TryOpen(s);
    }

    // ---------------------------------------------------------------- identity first (§8.1b)

    /// <summary>
    /// The identity-first handshake: the gateway answers the client's getchallenge itself, reads the
    /// SteamID from the connect that answers it (never forwarding that connect), asks the service
    /// (Identify, single presence) where the client may go, and only then replays the client's own
    /// getchallenge to the chosen backend. The backend's challenge goes back unchanged (it echoes the
    /// client's challenge), and the client's second connect is forwarded as is. Returns true when the
    /// datagram was consumed here.
    /// </summary>
    bool IdentityFirst(Session s, ReadOnlySpan<byte> p)
    {
        if (_codec.IsGetChallenge(p))
        {
            // A new handshake: whatever the session was connected to is over (hard cut).
            if (s.Link is not null) { Cut(s, "new handshake"); Metrics.Flips.Inc(); }
            if (s.Hold is not null) { Metrics.HeldPackets.Dec(s.Hold.Count); s.Hold = null; s.HoldReason = null; }
            s.Phase = IdentityPhase.Challenged;
            s.GatewayChallenge = Random.Shared.Next(1, int.MaxValue);
            s.HeldGetChallenge = p.ToArray();
            ReplyToClient(s, _codec.Challenge(s.GatewayChallenge, p));
            return true;
        }
        if (!_codec.TryReadConnect(p, out var challenge, out var steamId)) return false;
        if (s.Phase == IdentityPhase.None || s.Phase == IdentityPhase.Replayed) return false; // the backend's own connect: forward
        if (challenge != s.GatewayChallenge) return false;
        if (s.Phase != IdentityPhase.Challenged) return true; // a resend of the connect already being identified
        if (steamId is not { } sid)
        {
            // Unreadable ticket: the plan as it was — the default backend, unidentified, an ephemeral peer.
            _log.LogInformation("session {Session}: connect ticket unreadable; default backend", s.Id);
            s.Route = null;
            ReplayGetChallenge(s);
            return true;
        }
        s.Phase = IdentityPhase.Identifying;
        var connect = p.ToArray();
        var request = new IdentifyRequest
        {
            RequestId = Guid.NewGuid().ToString("N"),
            SessionId = s.Id,
            ClientAddr = s.ClientKey,
            Steamid = sid.ToString(),
        };
        _ = Task.Run(async () =>
        {
            IdentifyResponse? r = null;
            try { r = await _events.IdentifyAsync(request, _stop.Token); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.LogWarning("Identify for {Session} failed ({Error}); default backend, unidentified", s.Id, e.Message);
            }
            catch (OperationCanceledException) { return; }
            lock (_sync) OnIdentified(s, sid, r, connect);
        });
        return true;
    }

    void OnIdentified(Session s, ulong steamId, IdentifyResponse? r, byte[] connect)
    {
        if (!_byKey.TryGetValue(s.ClientKey, out var live) || live != s || s.Phase != IdentityPhase.Identifying) return;
        if (r is null) { ReplayGetChallenge(s); return; }
        if (!r.Allowed)
        {
            var reason = string.IsNullOrEmpty(r.Reason) ? "refused" : r.Reason;
            ReplyToClient(s, _codec.Reject(connect, reason));
            Close(s, $"identify refused: {reason}");
            return;
        }
        foreach (var id in r.CloseSessions)
            if (_byKey.Values.FirstOrDefault(x => x.Id == id && x != s) is { } old) Close(old, "taken over");
        if (_byKey.Values.Any(x => x != s && x.SteamId == steamId))
        {
            ReplyToClient(s, _codec.Reject(connect, "already connected"));
            Close(s, "identify refused: steamid held by a live session");
            return;
        }
        s.SteamId = steamId;
        if (!string.IsNullOrEmpty(r.Backend) && IPEndPoint.TryParse(r.Backend, out var backend)) s.Route = backend;
        if (r.TableVersion > _version) _version = r.TableVersion;
        ReplayGetChallenge(s);
    }

    /// <summary>Sends the client's own getchallenge to the target, through the normal path (holds included).</summary>
    void ReplayGetChallenge(Session s)
    {
        var q = s.HeldGetChallenge!;
        s.Phase = IdentityPhase.Replayed;
        s.HeldGetChallenge = null;
        Forward(s, q);
    }

    void ReplyToClient(Session s, byte[] reply)
    {
        try { _public!.SendTo(reply, SocketFlags.None, s.ClientAddress); }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Per source IP, at most <c>HandshakesPerSecond</c> getchallenges a second (a token bucket of that
    /// size, refilled continuously on the relay's clock); the rest are dropped (Q24). This is the only
    /// handshake limiter left: with the sidecar each client reaches the engine from its own address, so
    /// the engine's per-address limits no longer see the gateway as one client (docs/net-protocol.md §11).
    /// </summary>
    bool HandshakeAllowed(SocketAddress from)
    {
        var rate = _o.HandshakesPerSecond;
        if (rate <= 0) return true;
        var ip = ((IPEndPoint)new IPEndPoint(IPAddress.Any, 0).Create(from)).Address;
        var now = _time.GetUtcNow();
        var (tokens, at) = _handshakeBuckets.TryGetValue(ip, out var b) ? b : (rate, now);
        tokens = Math.Min(rate, tokens + (now - at).TotalSeconds * rate);
        if (tokens < 1) { _handshakeBuckets[ip] = (tokens, now); return false; }
        _handshakeBuckets[ip] = (tokens - 1, now);
        return true;
    }

    bool IsReady(IPEndPoint backend) => !_readiness.TryGetValue(backend, out var ready) || ready;

    void StartHold(Session s)
    {
        s.Hold = new HoldBuffer(_o.HoldPackets, _o.HoldBytes);
        s.HoldSince = _time.GetUtcNow();
        Metrics.AdmissionQueue.Set(_byKey.Values.Count(x => x.Hold is not null));
    }

    /// <summary>Opens a stream for a holding session when its target exists, is ready and is not in back-off.</summary>
    void TryOpen(Session s)
    {
        if (s.Link is not null) { s.HoldReason = $"opening stream to {s.Link.Backend}"; return; }
        var target = TargetOf(s);
        if (target is null) { s.HoldReason = "no default backend"; return; }
        if (!IsReady(target)) { s.HoldReason = $"backend {target} not ready"; return; }
        // Back off from the backend that just failed; a route change to another one goes at once.
        if (target.Equals(s.RetryBackend) && s.RetryAt > _time.GetUtcNow()) return;
        s.HoldReason = $"opening stream to {target}";
        OpenLink(s, target);
    }

    void HoldPacket(Session s, ReadOnlySpan<byte> p)
    {
        var before = s.Hold!.Count;
        var evicted = s.Hold.Add(p);
        if (evicted > 0) Metrics.HoldEvicted.Inc(evicted);
        Metrics.HeldPackets.Inc(s.Hold.Count - before);
    }

    /// <summary>After a table change: every holding session tries to open toward its (new) target.</summary>
    void ReleaseHolds()
    {
        foreach (var s in _byKey.Values.Where(x => x.Hold is not null).OrderBy(x => x.HoldSince).ToList())
        {
            if (s.Link is { Open: false } opening && !opening.Backend.Equals(TargetOf(s))) Cut(s, "route changed while opening");
            TryOpen(s);
        }
        Metrics.AdmissionQueue.Set(_byKey.Values.Count(x => x.Hold is not null));
    }

    void Send(StreamLink link, ReadOnlySpan<byte> p) =>
        link.Up.Writer.TryWrite(new RelayUp { Datagram = new Proto.Relay.Datagram { Payload = ByteString.CopyFrom(p) } });

    GrpcChannel ChannelFor(IPEndPoint backend)
    {
        if (!_channels.TryGetValue(backend, out var ch)) _channels[backend] = ch = _channelFor(backend);
        return ch;
    }

    /// <summary>Opens a PeerRelay stream to the target's sidecar; the session holds until Opened.</summary>
    void OpenLink(Session s, IPEndPoint target)
    {
        var link = new StreamLink(target);
        s.Link = link;
        link.Up.Writer.TryWrite(new RelayUp
        {
            Open = new Open
            {
                SessionId = s.Id,
                ClientAddr = s.ClientKey,
                Steamid = s.SteamId?.ToString() ?? "",
                TableVersion = _version,
                PeerHint = s.PeerHint?.ToString() ?? "",
            },
        });
        s.PeerHint = null;
        try { link.Call = new PeerRelay.PeerRelayClient(ChannelFor(target)).Relay(cancellationToken: link.Stop.Token); }
        catch (Exception e) when (e is RpcException or InvalidOperationException or HttpRequestException)
        {
            _log.LogWarning("session {Session}: cannot open a stream to {Backend}: {Error}", s.Id, target, e.Message);
            LinkEnded(s, link, null);
            return;
        }
        _ = Task.Run(() => PumpUp(link));
        _ = Task.Run(() => PumpDown(s, link));
    }

    static async Task PumpUp(StreamLink link)
    {
        var ct = link.Stop.Token;
        try
        {
            await foreach (var m in link.Up.Reader.ReadAllAsync(ct))
                await link.Call!.RequestStream.WriteAsync(m, ct);
            await link.Call!.RequestStream.CompleteAsync();
        }
        catch (Exception e) when (e is OperationCanceledException or RpcException or InvalidOperationException or IOException) { }
    }

    async Task PumpDown(Session s, StreamLink link)
    {
        string? closed = null;
        try
        {
            await foreach (var m in link.Call!.ResponseStream.ReadAllAsync(link.Stop.Token))
            {
                lock (_sync)
                {
                    if (s.Link != link) return;
                    switch (m.KindCase)
                    {
                        case RelayDown.KindOneofCase.Opened: OnOpened(s, link, m.Opened); break;
                        case RelayDown.KindOneofCase.Datagram: OnBackendPacket(s, m.Datagram.Payload.Span); break;
                        case RelayDown.KindOneofCase.Closed: closed = m.Closed.Reason; break;
                    }
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or RpcException or InvalidOperationException or IOException)
        {
            if (link.Stop.IsCancellationRequested) return; // we cut it
            _log.LogInformation("session {Session}: stream to {Backend} failed: {Error}", s.Id, link.Backend, e.Message);
        }
        lock (_sync) LinkEnded(s, link, closed);
    }

    /// <summary>The sidecar answered Opened: the peer is known, the hold is released in arrival order.</summary>
    void OnOpened(Session s, StreamLink link, Opened opened)
    {
        link.Open = true;
        link.Peer = IPEndPoint.TryParse(opened.Peer, out var peer) ? peer : null;
        var e = new SessionEvent
        {
            RequestId = Guid.NewGuid().ToString("N"),
            SessionId = s.Id,
            ClientAddr = s.ClientKey,
            Backend = link.Backend.ToString(),
            Peer = opened.Peer,
            Reason = s.MovedFrom is null ? "open" : "route",
            Steamid = s.SteamId?.ToString() ?? "",
        };
        if (!s.EverLinked) _events.SessionOpened(e);
        else _events.SessionMoved(e);
        s.EverLinked = true;
        s.MovedFrom = null;
        if (s.Hold is not null)
        {
            var held = s.Hold.Drain();
            Metrics.HeldPackets.Dec(held.Count);
            s.Hold = null;
            s.HoldReason = null;
            foreach (var d in held) Send(link, d);
            Metrics.AdmissionQueue.Set(_byKey.Values.Count(x => x.Hold is not null));
        }
    }

    /// <summary>A stream ended from the far side (or never opened).</summary>
    void LinkEnded(Session s, StreamLink link, string? closed)
    {
        if (s.Link != link) return;
        s.Link = null;
        link.Up.Writer.TryComplete();
        link.Stop.Cancel();
        if (!_byKey.TryGetValue(s.ClientKey, out var live) || live != s) return;
        if (closed == "superseded") { Close(s, "superseded at the backend"); return; }
        if (!link.Open)
        {
            // Never opened: back off and keep holding; the sweep retries until the hold expires.
            s.RetryAt = _time.GetUtcNow() + _o.StreamRetry;
            s.RetryBackend = link.Backend;
            s.HoldReason = $"backend {link.Backend} unreachable";
            return;
        }
        s.MovedFrom = link.Backend;
        _log.LogInformation("session {Session}: stream to {Backend} ended ({Reason})", s.Id, link.Backend, closed ?? "by the backend");
    }

    void OnBackendPacket(Session s, ReadOnlySpan<byte> p)
    {
        s.PacketsOut++;
        s.BytesOut += p.Length;
        Metrics.PacketsOut.Inc();
        Metrics.BytesOut.Inc(p.Length);
        try { _public!.SendTo(p, SocketFlags.None, s.ClientAddress); }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    // ---------------------------------------------------------------- sessions

    Session CreateSession(SocketAddress from)
    {
        var key = new SocketAddress(from.Family, from.Size);
        from.Buffer.Span[..from.Size].CopyTo(key.Buffer.Span);
        var client = (IPEndPoint)new IPEndPoint(IPAddress.Any, 0).Create(key);
        var clientKey = client.ToString();
        string id;
        _pendingRoutes.Remove(clientKey, out var route);
        _pendingSince.Remove(clientKey);
        id = route is { SessionId.Length: > 0 } ? route.SessionId : $"{_o.GatewayId}-{Interlocked.Increment(ref _sessionSeq)}";
        var s = new Session(id, key, client, _time.GetUtcNow());
        if (route is not null) ApplyRoute(s, route);
        _byAddress[key] = s;
        _byKey[clientKey] = s;
        Metrics.Sessions.Set(_byKey.Count);
        return s;
    }

    static ulong? ParseSteamId(string? v) => ulong.TryParse(v, out var id) && id != 0 ? id : null;

    void ApplyRoute(Session s, Route r)
    {
        if (ParseSteamId(r.Steamid) is { } sid) s.SteamId = sid;
        s.Route = string.IsNullOrEmpty(r.Backend) ? null : IPEndPoint.Parse(r.Backend);
        if (!string.IsNullOrEmpty(r.Peer) && IPEndPoint.TryParse(r.Peer, out var peer)) s.PeerHint = peer;
    }

    /// <summary>Where the session's next handshake goes: its route if identified, else the default.</summary>
    IPEndPoint? TargetOf(Session s) => s.Identified && s.Route is not null ? s.Route : _default;

    /// <summary>The hard cut (R21): the stream to the old backend is ended now; nothing more is queued to it.</summary>
    void Cut(Session s, string reason)
    {
        var link = s.Link;
        if (link is null) return;
        s.Link = null;
        if (link.Open) s.MovedFrom = link.Backend;
        link.Up.Writer.TryComplete();
        link.Stop.Cancel();
        _log.LogInformation("session {Session} cut from {Backend} ({Reason})", s.Id, link.Backend, reason);
    }

    void Close(Session s, string reason)
    {
        var backend = s.Link?.Backend;
        var peer = s.Link?.Peer;
        Cut(s, reason);
        if (s.Hold is not null) { Metrics.HeldPackets.Dec(s.Hold.Count); s.Hold = null; }
        _byAddress.Remove(s.ClientAddress);
        _byKey.Remove(s.ClientKey);
        _closedTotals = (_closedTotals.PacketsIn + s.PacketsIn, _closedTotals.PacketsOut + s.PacketsOut,
                         _closedTotals.BytesIn + s.BytesIn, _closedTotals.BytesOut + s.BytesOut);
        _log.LogInformation("session {Session} {Client} closed: {Reason}", s.Id, s.ClientKey, reason);
        Metrics.Sessions.Set(_byKey.Count);
        Metrics.Closes.Inc();
        _events.SessionClosed(new SessionEvent
        {
            RequestId = Guid.NewGuid().ToString("N"),
            SessionId = s.Id,
            ClientAddr = s.ClientKey,
            Backend = backend?.ToString() ?? "",
            Peer = peer?.ToString() ?? "",
            Reason = reason,
            Steamid = s.SteamId?.ToString() ?? "",
        });
    }

    /// <summary>Expiry, hold deadlines and stream retries; runs every <c>SweepInterval</c> on the relay's clock.</summary>
    public void Sweep()
    {
        lock (_sync)
        {
            var now = _time.GetUtcNow();
            foreach (var s in _byKey.Values.Where(s => now - s.LastSeen >= _o.SessionExpiry).ToList())
            {
                Close(s, "expired");
                Metrics.Expiries.Inc();
            }
            // A route carried over (SyncTable after a restart) whose client never came back: the
            // service's session would stay open forever, so it expires like a silent session.
            foreach (var (addr, since) in _pendingSince.Where(p => now - p.Value >= _o.SessionExpiry).ToList())
            {
                _pendingSince.Remove(addr);
                if (!_pendingRoutes.Remove(addr, out var r) || r.SessionId.Length == 0) continue;
                _log.LogInformation("carried session {Session} {Client} closed: never resumed", r.SessionId, addr);
                Metrics.Expiries.Inc();
                _events.SessionClosed(new SessionEvent
                {
                    RequestId = Guid.NewGuid().ToString("N"),
                    SessionId = r.SessionId, ClientAddr = addr, Backend = r.Backend, Peer = r.Peer,
                    Reason = "expired: never resumed", Steamid = r.Steamid,
                });
            }
            foreach (var s in _byKey.Values.Where(s => s.Hold is not null && now - s.HoldSince >= _o.HoldTime).ToList())
            {
                _log.LogWarning("session {Session} hold expired after {HoldTime}: {Reason}", s.Id, _o.HoldTime, s.HoldReason);
                Close(s, $"hold expired: {s.HoldReason}");
            }
            foreach (var s in _byKey.Values.Where(s => s.Hold is not null && s.Link is null && s.RetryAt <= now).ToList())
                TryOpen(s);
            foreach (var ip in _handshakeBuckets.Where(b => now - b.Value.At > TimeSpan.FromSeconds(10)).Select(b => b.Key).ToList())
                _handshakeBuckets.Remove(ip);
            Metrics.AdmissionQueue.Set(_byKey.Values.Count(x => x.Hold is not null));
        }
    }

    // ---------------------------------------------------------------- control (§8.2)

    bool Fresh(ulong version, bool allowEqual = false) => allowEqual ? version >= _version : version > _version;

    public ControlStatus SetDefault(ulong version, string backend)
    {
        if (!IPEndPoint.TryParse(backend, out var ep)) return ControlStatus.Invalid;
        lock (_sync)
        {
            if (!Fresh(version)) return ControlStatus.Stale;
            _version = version;
            _default = ep;
            ReleaseHolds();
            return ControlStatus.Ok;
        }
    }

    public ControlStatus SetRoute(ulong version, string clientAddr, string backend, string? steamId = null, bool holdUntilReady = false)
    {
        if (!IPEndPoint.TryParse(clientAddr, out _) || !IPEndPoint.TryParse(backend, out var ep)) return ControlStatus.Invalid;
        var sid = ParseSteamId(steamId);
        lock (_sync)
        {
            if (!Fresh(version)) return ControlStatus.Stale;
            _byKey.TryGetValue(clientAddr, out var s);
            if (sid is { } id && _byKey.Values.Any(o => o != s && o.SteamId == id)) return ControlStatus.Conflict;
            var identified = sid is not null || s?.Identified == true
                || (_pendingRoutes.TryGetValue(clientAddr, out var pr) && ParseSteamId(pr.Steamid) is not null);
            if (!identified && !ep.Equals(_default)) return ControlStatus.Unidentified;
            _version = version;
            if (holdUntilReady && !_readiness.ContainsKey(ep)) _readiness[ep] = false;
            if (s is null)
            {
                var r = _pendingRoutes.TryGetValue(clientAddr, out var existing) ? existing : new Route { ClientAddr = clientAddr };
                r.Backend = backend;
                if (sid is not null) { r.Steamid = sid.ToString(); r.Identified = true; }
                _pendingRoutes[clientAddr] = r;
                _pendingSince.TryAdd(clientAddr, _time.GetUtcNow());
                return ControlStatus.Ok;
            }
            if (sid is not null) s.SteamId = sid;
            s.Route = ep;
            ReleaseHolds();
            return ControlStatus.Ok;
        }
    }

    public ControlStatus CloseSession(ulong version, string clientAddr, string reason)
    {
        lock (_sync)
        {
            if (!Fresh(version)) return ControlStatus.Stale;
            _version = version;
            _pendingRoutes.Remove(clientAddr);
            _pendingSince.Remove(clientAddr);
            if (_byKey.TryGetValue(clientAddr, out var s)) Close(s, string.IsNullOrEmpty(reason) ? "closed by service" : reason);
            return ControlStatus.Ok;
        }
    }

    /// <summary>Marks a backend ready (its held sessions are released in order) or not ready.</summary>
    public ControlStatus BackendReady(ulong version, string backend, bool ready)
    {
        if (!IPEndPoint.TryParse(backend, out var ep)) return ControlStatus.Invalid;
        lock (_sync)
        {
            if (!Fresh(version)) return ControlStatus.Stale;
            _version = version;
            _readiness[ep] = ready;
            if (ready) ReleaseHolds();
            return ControlStatus.Ok;
        }
    }

    /// <summary>Sessions holding packets and how many each holds, for facts and the Snapshot.</summary>
    public int HeldPackets { get { lock (_sync) return _byKey.Values.Sum(x => x.Hold?.Count ?? 0); } }
    public int HoldingSessions { get { lock (_sync) return _byKey.Values.Count(x => x.Hold is not null); } }

    /// <summary>Replaces the table (after a gateway restart, or when the service saw a version mismatch).</summary>
    public ControlStatus SyncTable(RouteTable table)
    {
        lock (_sync)
        {
            if (!Fresh(table.Version, allowEqual: true)) return ControlStatus.Stale;
            _version = table.Version;
            _default = string.IsNullOrEmpty(table.DefaultBackend) ? null : IPEndPoint.Parse(table.DefaultBackend);
            _pendingRoutes.Clear();
            foreach (var r in table.Routes)
            {
                if (_byKey.TryGetValue(r.ClientAddr, out var s))
                {
                    if (ParseSteamId(r.Steamid) is { } sid) s.SteamId = sid;
                    s.Route = string.IsNullOrEmpty(r.Backend) ? null : IPEndPoint.Parse(r.Backend);
                }
                else
                {
                    _pendingRoutes[r.ClientAddr] = r.Clone();
                    _pendingSince.TryAdd(r.ClientAddr, _time.GetUtcNow());
                }
            }
            foreach (var gone in _pendingSince.Keys.Where(k => !_pendingRoutes.ContainsKey(k)).ToList()) _pendingSince.Remove(gone);
            _readiness.Clear();
            foreach (var b in table.UnreadyBackends)
                if (IPEndPoint.TryParse(b, out var ub)) _readiness[ub] = false;
            ReleaseHolds();
            return ControlStatus.Ok;
        }
    }

    /// <summary>The totals reported to the service every <c>StatsInterval</c>.</summary>
    public GatewayStats Stats()
    {
        lock (_sync)
            return new GatewayStats
            {
                GatewayId = _o.GatewayId,
                Sessions = _byKey.Count,
                PacketsIn = _closedTotals.PacketsIn + _byKey.Values.Sum(x => x.PacketsIn),
                PacketsOut = _closedTotals.PacketsOut + _byKey.Values.Sum(x => x.PacketsOut),
                BytesIn = _closedTotals.BytesIn + _byKey.Values.Sum(x => x.BytesIn),
                BytesOut = _closedTotals.BytesOut + _byKey.Values.Sum(x => x.BytesOut),
                HeldPackets = _byKey.Values.Sum(x => x.Hold?.Count ?? 0),
                AdmissionQueue = _byKey.Values.Count(x => x.Hold is not null),
            };
    }

    /// <summary>The A2S snapshot for the hub (SetServerInfo); versionless, it replaces the last one.</summary>
    public void SetServerInfo(ServerInfo info)
    {
        lock (_sync) (_a2s ?? throw new InvalidOperationException("not started")).SetInfo(info);
    }

    public ServerInfo? ServerInfo { get { lock (_sync) return _a2s?.Info; } }
    public A2sResponder A2s => _a2s ?? throw new InvalidOperationException("not started");

    /// <summary>The live table: every session with its backend and peer.</summary>
    public IReadOnlyList<SessionInfo> Snapshot()
    {
        lock (_sync)
            return _byKey.Values.Select(s => new SessionInfo
            {
                SessionId = s.Id,
                ClientAddr = s.ClientKey,
                Backend = (s.Link?.Backend ?? TargetOf(s))?.ToString() ?? "",
                Peer = s.Link?.Peer?.ToString() ?? "",
                Steamid = s.SteamId?.ToString() ?? "",
                State = s.State,
                OpenedUnixMs = s.Opened.ToUnixTimeMilliseconds(),
                LastSeenUnixMs = s.LastSeen.ToUnixTimeMilliseconds(),
                BytesIn = s.BytesIn,
                BytesOut = s.BytesOut,
                Held = s.Hold?.Count ?? 0,
            }).ToList();
    }

    /// <summary>Why a client's session is holding, or null when it is not (Dashboard, facts).</summary>
    public string? HoldReasonOf(IPEndPoint client)
    {
        lock (_sync) return _byKey.TryGetValue(client.ToString(), out var s) && s.Hold is not null ? s.HoldReason : null;
    }

    /// <summary>The session for a client address, as the snapshot shows it; null when there is none.</summary>
    public SessionInfo? SessionOf(IPEndPoint client) => Snapshot().FirstOrDefault(i => i.ClientAddr == client.ToString());

    int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _stop.Cancel();
        _sweep?.Dispose();
        _stats?.Dispose();
        lock (_sync)
        {
            foreach (var s in _byKey.Values.ToList()) Cut(s, "gateway stopping");
            foreach (var ch in _channels.Values) ch.Dispose();
            _channels.Clear();
        }
        _public?.Dispose();
        if (_publicLoop is not null) await _publicLoop.ConfigureAwait(false);
        _stop.Dispose();
    }
}
