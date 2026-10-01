using System.Collections.Concurrent;
using Grpc.Core;
using P = SourceSharp.Host.Proto;

namespace SourceSharp.Host.Sdk;

/// <summary>
/// The instance's life with the host (§6.1, §6.6): the Connect stream and its heartbeat, Booting
/// with the rules-module handshake, MapReady, players, logs, and the main-thread pump.
/// Every <c>Task</c> it returns completes inside <see cref="Pump"/>.
/// </summary>
public interface IHostSession
{
    HostEndpoint Endpoint { get; }
    /// <summary>True while the Connect stream is open and its heartbeats are acknowledged.</summary>
    bool Connected { get; }
    /// <summary>What Booting answered, once it has (set on the main thread, in Pump).</summary>
    BootInfo? Boot { get; }
    /// <summary>Reported on every heartbeat.</summary>
    int Players { get; set; }
    float TickMs { get; set; }
    /// <summary>True when the game pumped once and then stopped for longer than the stall limit: heartbeats are withheld.</summary>
    bool MainThreadStalled { get; }
    /// <summary>Why the last Connect stream ended, for the game's status line; null while none has.</summary>
    string? LastStreamError { get; }
    /// <summary>Raised in Pump when the stream comes up (true) or goes down (false).</summary>
    event Action<bool>? ConnectionChanged;

    /// <summary>
    /// Announces the SDK version and the rules module (hash first); on SEND streams the module's
    /// files. Refused with <see cref="HostError.SdkVersion"/> / <see cref="HostError.ContractVersion"/> by a host that cannot speak to us.
    /// </summary>
    Task<HostResult<BootInfo>> Booting(string map, string? modImageDigest = null);
    /// <summary>The port the socket reports; the answer is the port the pod was given (<see cref="HostError.PortMismatch"/> if they differ).</summary>
    Task<HostResult<int>> MapReady(int port, string map);
    /// <summary>A player connected from <paramref name="peer"/> (as the engine sees it); the answer carries the real address and session.</summary>
    Task<HostResult<PlayerJoin>> PlayerJoined(string peer, string steamId);
    Task<HostResult> PlayerLeft(string steamId, string peer);
    /// <summary>Queues a log line; safe from any thread. Batched, throttled, and dropped (counted) when the buffer is full.</summary>
    void Log(string level, string text);
    /// <summary>Runs every queued completion, event and command on the calling thread; call it once per frame from the main thread.</summary>
    int Pump();
}

internal sealed class HostSession : IHostSession
{
    readonly SdkCore _core;
    readonly HostCommands _commands;
    readonly ConcurrentQueue<P.LogLine> _logs = new();
    readonly ConcurrentDictionary<ulong, DateTimeOffset> _sentAt = new();
    int _logCount;
    long _droppedPending;
    long _seq;
    volatile bool _connected;
    volatile int _players;
    volatile float _tickMs;
    bool _everConnected;
    Task _stream = Task.CompletedTask, _watchdog = Task.CompletedTask, _logFlusher = Task.CompletedTask;

    public HostSession(SdkCore core, HostCommands commands)
    {
        _core = core;
        _commands = commands;
    }

    public HostEndpoint Endpoint => _core.Endpoint;
    public bool Connected => _connected;
    public BootInfo? Boot { get; private set; }
    public int Players { get => _players; set => _players = value; }
    public float TickMs { get => _tickMs; set => _tickMs = value; }
    public event Action<bool>? ConnectionChanged;
    public string? LastStreamError { get; private set; }

    public void Start()
    {
        _stream = Task.Run(RunStream);
        _watchdog = Task.Run(RunWatchdog);
        _logFlusher = Task.Run(RunLogFlusher);
    }

    public async Task StopAsync()
    {
        _core.Cancel();
        foreach (var t in new[] { _stream, _watchdog, _logFlusher })
            try { await t.ConfigureAwait(false); } catch (OperationCanceledException) { }
    }

    long _lastPumpTicks;

    /// <summary>True once the game has pumped and then stopped for longer than <see cref="HostSdkOptions.MainThreadStallLimit"/>.</summary>
    public bool MainThreadStalled
    {
        get
        {
            var limit = _core.Options.MainThreadStallLimit;
            var last = Interlocked.Read(ref _lastPumpTicks);
            return limit > TimeSpan.Zero && last != 0 && _core.Options.Clock.UtcNow.UtcTicks - last > limit.Ticks;
        }
    }

    public int Pump()
    {
        Interlocked.Exchange(ref _lastPumpTicks, _core.Options.Clock.UtcNow.UtcTicks);
        return _core.Main.Pump();
    }

    // ------------------------------------------------------------------ the stream

    async Task RunStream()
    {
        var o = _core.Options;
        var backoff = o.ReconnectBackoff;
        while (!_core.Stopping.IsCancellationRequested)
        {
            var acked = await OneStream().ConfigureAwait(false);
            if (_core.Stopping.IsCancellationRequested) break;
            if (!acked) _core.Metrics.ConnectFailure();
            else backoff = o.ReconnectBackoff;
            try { await o.Clock.Delay(backoff, _core.Stopping).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            if (!acked) backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, o.ReconnectBackoffMax.Ticks));
        }
    }

    /// <summary>One Connect stream until it fails or goes silent; true when it was ever acknowledged.</summary>
    async Task<bool> OneStream()
    {
        var o = _core.Options;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_core.Stopping);
        var acked = false;
        var lastAckTicks = o.Clock.UtcNow.UtcTicks;
        try
        {
            using var call = _core.Instance.Connect(_core.Rpc.Auth(), cancellationToken: cts.Token);
            var reader = Task.Run(async () =>
            {
                await foreach (var cmd in call.ResponseStream.ReadAllAsync(cts.Token).ConfigureAwait(false))
                {
                    if (cmd.KindCase == P.ServerCommand.KindOneofCase.HeartbeatAck)
                    {
                        Volatile.Write(ref lastAckTicks, o.Clock.UtcNow.UtcTicks);
                        if (!acked)
                        {
                            acked = true;
                            if (_everConnected) _core.Metrics.Reconnect();
                            _everConnected = true;
                            _connected = true;
                            _core.Main.Post(() => ConnectionChanged?.Invoke(true));
                        }
                        OnAck(cmd.HeartbeatAck.Seq);
                    }
                    else _commands.OnCommand(cmd);
                }
            }, cts.Token);

            while (!cts.IsCancellationRequested)
            {
                // A main thread that stopped pumping is a hung game: stop vouching for it, so the
                // service sees the missed heartbeats (suspect, then reap) while the stream stays open.
                var stalled = MainThreadStalled;
                if (!stalled)
                {
                    var seq = (ulong)Interlocked.Increment(ref _seq);
                    _sentAt[seq] = o.Clock.UtcNow;
                    await call.RequestStream.WriteAsync(new P.Heartbeat { Seq = seq, Players = _players, TickMs = _tickMs }, cts.Token).ConfigureAwait(false);
                    _core.Metrics.HeartbeatSent();
                }
                else
                {
                    _core.Metrics.HeartbeatWithheld();
                    Volatile.Write(ref lastAckTicks, o.Clock.UtcNow.UtcTicks); // nothing sent, nothing owed
                }
                var tick = o.Clock.Delay(o.HeartbeatInterval, cts.Token);
                if (await Task.WhenAny(tick, reader).ConfigureAwait(false) == reader)
                {
                    await reader.ConfigureAwait(false); // surfaces the stream's end
                    break;
                }
                await tick.ConfigureAwait(false);
                if (o.Clock.UtcNow.UtcTicks - Volatile.Read(ref lastAckTicks) > (o.HeartbeatInterval * o.MissedAcksBeforeReconnect).Ticks) break; // silent: reopen
            }
        }
        catch (Exception e) when (e is RpcException or OperationCanceledException or IOException or HttpRequestException or InvalidOperationException)
        {
            LastStreamError = $"{e.GetType().Name}: {e.Message}";
        }
        finally
        {
            await cts.CancelAsync().ConfigureAwait(false);
            if (_connected)
            {
                _connected = false;
                _core.Main.Post(() => ConnectionChanged?.Invoke(false));
            }
        }
        return acked;
    }

    /// <summary>
    /// The service renewed this instance's leases before acknowledging: each is confirmed until
    /// the heartbeat's send time plus the TTL (a bound the service's own expiry cannot undercut).
    /// </summary>
    void OnAck(ulong seq)
    {
        _core.Metrics.HeartbeatAcked();
        if (!_sentAt.TryRemove(seq, out var sent)) return;
        foreach (var k in _sentAt.Keys) if (k < seq) _sentAt.TryRemove(k, out _);
        _core.Leases.Confirm(sent + _core.Options.LeaseTtl);
    }

    /// <summary>Fail closed: leases whose renewal could not be confirmed within the TTL are lost.</summary>
    async Task RunWatchdog()
    {
        var o = _core.Options;
        var period = TimeSpan.FromTicks(Math.Max(TimeSpan.FromMilliseconds(10).Ticks, o.HeartbeatInterval.Ticks / 2));
        while (!_core.Stopping.IsCancellationRequested)
        {
            try { await o.Clock.Delay(period, _core.Stopping).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            _core.Leases.ExpireUnconfirmed(o.Clock.UtcNow);
        }
    }

    // ------------------------------------------------------------------ unary

    public Task<HostResult<BootInfo>> Booting(string map, string? modImageDigest = null) => _core.Background(async () =>
    {
        (P.RulesModule announce, IReadOnlyList<(string, string, byte[])> files) prepared;
        try { prepared = ModuleHandshake.Prepare(_core.Options.ModuleSource.Find()); }
        catch (IOException e) { return HostResult<BootInfo>.Refused(new HostRefusal(HostError.ModuleRefused, "module_unreadable", e.Message, Local: true)); }
        var request = new P.BootingRequest
        {
            RequestId = Rpc.NewRequestId(), SdkVersion = HostSdk.Version, Module = prepared.announce, Map = map, ModImageDigest = modImageDigest ?? "",
        };
        var r = await _core.Rpc.Call((m, d, ct) => _core.Instance.BootingAsync(request, m, d, ct)).ConfigureAwait(false);
        if (!r.Ok) return HostResult<BootInfo>.Refused(r.Refusal!);
        var uploaded = false;
        if (r.Value.Module == P.ModuleAnswer.Send)
        {
            var up = await UploadWithRetry(prepared.files).ConfigureAwait(false);
            if (!up.Ok) return HostResult<BootInfo>.Refused(up.Refusal!);
            uploaded = true;
        }
        var info = new BootInfo((ModuleAnswer)(int)r.Value.Module, r.Value.InstanceSeed, r.Value.InstanceKind, r.Value.Depth, r.Value.LevelHash, uploaded);
        return HostResult<BootInfo>.Success(info);
    }, onMain: r => { if (r.Ok) Boot = r.Value; });

    async Task<HostResult> UploadWithRetry(IReadOnlyList<(string, string, byte[])> files)
    {
        var o = _core.Options;
        var backoff = o.RetryBackoff;
        string last = "";
        for (var attempt = 1; attempt <= o.RpcAttempts; attempt++)
        {
            if (attempt > 1)
            {
                _core.Metrics.Retry();
                try { await o.Clock.Delay(backoff, _core.Stopping).ConfigureAwait(false); }
                catch (OperationCanceledException) { return HostResult.Refused(HostRefusal.Disposed()); }
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, o.RetryBackoffMax.Ticks));
            }
            try
            {
                var r = await ModuleHandshake.Upload(_core, files).ConfigureAwait(false);
                return r.Accepted ? HostResult.Success : HostResult.Refused(new HostRefusal(HostError.ModuleRefused, "module_refused", r.Reason));
            }
            catch (RpcException e) when (HostRefusal.Transient(e)) { last = e.Status.Detail; }
            catch (RpcException e) { return HostResult.Refused(HostRefusal.FromRpc(e)); }
            catch (Exception e) when (e is IOException or HttpRequestException or InvalidOperationException) { last = e.Message; }
        }
        return HostResult.Refused(HostRefusal.Unreachable($"module upload: {last}"));
    }

    public Task<HostResult<int>> MapReady(int port, string map) => _core.Background(async () =>
    {
        // Live means reachable: the service pushes PrepareHop, Retry and Shutdown down the stream, so
        // an instance does not report its map ready before the stream is up (it may still be backing off).
        var o = _core.Options;
        var until = o.Clock.UtcNow + o.StreamWaitBeforeMapReady;
        while (!_connected)
        {
            if (o.Clock.UtcNow >= until)
                return HostResult<int>.Refused(new HostRefusal(HostError.Unreachable, "stream_not_connected",
                    $"the Connect stream did not come up within {o.StreamWaitBeforeMapReady}: {LastStreamError}", Local: true));
            try { await o.Clock.Delay(TimeSpan.FromMilliseconds(20), _core.Stopping).ConfigureAwait(false); }
            catch (OperationCanceledException) { return HostResult<int>.Refused(HostRefusal.Disposed()); }
        }
        var request = new P.MapReadyRequest { RequestId = Rpc.NewRequestId(), Port = port, Map = map };
        var r = await _core.Rpc.Call((m, d, ct) => _core.Instance.MapReadyAsync(request, m, d, ct)).ConfigureAwait(false);
        return r.Map(v => v.ExpectedPort);
    });

    public Task<HostResult<PlayerJoin>> PlayerJoined(string peer, string steamId) => _core.Background(async () =>
    {
        var request = new P.PlayerJoinedRequest { RequestId = Rpc.NewRequestId(), Peer = peer, Steamid = steamId };
        var r = await _core.Rpc.Call((m, d, ct) => _core.Instance.PlayerJoinedAsync(request, m, d, ct)).ConfigureAwait(false);
        if (r.Ok && r.Value.Allowed && r.Value.ClientAddr.Length > 0)
            _core.PeerCache[peer] = new PeerIdentity(peer, r.Value.ClientAddr, r.Value.SessionId, steamId, PeerSource.Service);
        return r.Map(v => new PlayerJoin(v.Allowed, v.Reason, v.SessionId, v.ClientAddr));
    });

    public Task<HostResult> PlayerLeft(string steamId, string peer) => _core.Background(async () =>
    {
        _core.PeerCache.TryRemove(peer, out _);
        var request = new P.PlayerLeftRequest { RequestId = Rpc.NewRequestId(), Steamid = steamId, Peer = peer };
        return (await _core.Rpc.Call((m, d, ct) => _core.Instance.PlayerLeftAsync(request, m, d, ct)).ConfigureAwait(false)).Untyped();
    });

    // ------------------------------------------------------------------ logs

    public void Log(string level, string text)
    {
        if (Interlocked.Increment(ref _logCount) > _core.Options.LogCapacity)
        {
            Interlocked.Decrement(ref _logCount);
            Interlocked.Increment(ref _droppedPending);
            _core.Metrics.Dropped(1);
            return;
        }
        _logs.Enqueue(new P.LogLine { AtUnixMs = _core.Options.Clock.UtcNow.ToUnixTimeMilliseconds(), Level = level, Text = text });
    }

    async Task RunLogFlusher()
    {
        var o = _core.Options;
        while (!_core.Stopping.IsCancellationRequested)
        {
            try { await o.Clock.Delay(o.LogFlushInterval, _core.Stopping).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            var request = new P.LogRequest();
            while (request.Lines.Count < o.LogBatch && _logs.TryDequeue(out var line))
            {
                Interlocked.Decrement(ref _logCount);
                request.Lines.Add(line);
            }
            request.Dropped = (int)Interlocked.Exchange(ref _droppedPending, 0);
            if (request.Lines.Count == 0 && request.Dropped == 0) continue;
            var r = await _core.Rpc.Call((m, d, ct) => _core.Instance.LogAsync(request, m, d, ct), attempts: 1).ConfigureAwait(false);
            if (!r.Ok)
            {
                // Logs never block the game: a batch the host did not take is counted as dropped.
                _core.Metrics.Dropped(request.Lines.Count);
                Interlocked.Add(ref _droppedPending, request.Lines.Count + request.Dropped);
            }
        }
    }
}
