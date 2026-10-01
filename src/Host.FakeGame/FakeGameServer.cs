using System.Collections.Concurrent;
using System.Net;
using System.Numerics;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using SourceSharp.Host.FakeClient;
using SourceSharp.Host.Relay;
using SourceSharp.Host.Sdk;
using SourceSharp.Host.Testing;

namespace SourceSharp.Host.FakeGame;

/// <summary>
/// The fake game server (plan §7.6): a game pod's shape without the engine. The relay (Host.Relay)
/// serves the gateway and forwards to a <see cref="ToyBackend"/> standing in for the engine; the
/// game side is written on Host.Sdk and nothing else of the API, on a main loop that pumps the SDK
/// ~60 times a second. Players who finish the toy handshake join through the SDK; the hub creates
/// and leases their character, a level leases the one the trip claimed for it; commands down the
/// stream are handled as the game will (PrepareHop: checkpoint, release, then HopReady).
/// </summary>
public sealed class FakeGameServer : IAsyncDisposable
{
    sealed class Player(string steamId, string peer, IPEndPoint? endPoint)
    {
        public string SteamId { get; } = steamId;
        public string Peer { get; set; } = peer;
        public IPEndPoint? EndPoint { get; set; } = endPoint;
        public string ClientAddr { get; set; } = "";
        public HostCharacter? Character { get; set; }
        public ICharacterLease? Lease { get; set; }
        public IReserve? Reserve { get; set; }
        public DateTime LastSeen { get; set; } = DateTime.UtcNow;
        public Task? Joining { get; set; }
    }

    readonly FakeGameConfig _config;
    readonly ConcurrentQueue<Action> _work = new();
    readonly List<(DateTime Due, TaskCompletionSource Done)> _timers = [];
    readonly Dictionary<string, Player> _players = [];           // main thread only
    readonly ConcurrentQueue<string> _events = new();
    readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly FakeGameRules _rules = new();                        // the game's own copy of its rules module
    readonly CancellationTokenSource _stop = new();
    ToyBackend _backend = null!;
    WebApplication _web = null!;
    HostSdk _sdk = null!;
    Thread? _main;
    int _logIndex;
    readonly HashSet<IPEndPoint> _connecting = [];
    uint _killSeq;
    volatile bool _hung;
    volatile int _slowMs;
    volatile bool _draining;
    int _stopping;

    public FakeGameServer(FakeGameConfig config) => _config = config;

    public FakeGameConfig Config => _config;
    public HostSdk Sdk => _sdk;
    /// <summary>The relay host's services (its PeerTable, RelayMetrics).</summary>
    public IServiceProvider Services => _web.Services;
    public ToyBackend Backend => _backend;
    public BootInfo? Boot { get; private set; }
    public bool MapIsReady { get; private set; }
    public string? ModuleSha256 { get; private set; }
    public bool Hung => _hung;
    public bool Draining => _draining;
    /// <summary>The exit code, when the process would have exited (Shutdown: 0, Crash: its code).</summary>
    public Task<int> Exited => _exited.Task;
    public IReadOnlyList<string> Events => [.. _events];
    public IPEndPoint ControlEndPoint { get; private set; } = null!;

    void Note(string e)
    {
        _events.Enqueue(e);
        if (_config.Fake.EchoEvents) Console.Error.WriteLine($"fakegame event: {e}");
    }

    // ------------------------------------------------------------------ start and stop

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_config.Note is { } note) { Note(note); Console.Error.WriteLine($"fakegame: {note}"); }
        var engine = IPEndPoint.Parse(_config.Relay.EngineEndpoint);
        _backend = new ToyBackend(_config.InstanceId, engine.Address, _config.GamePort);

        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.Services.AddPeerRelay(_config.Relay);
        b.Services.AddSingleton(this);
        b.Services.Configure<HostOptions>(h => h.ShutdownTimeout = TimeSpan.FromSeconds(2));
        var control = _config.Fake.Control;
        b.WebHost.ConfigureKestrel(k =>
        {
            k.ListenRelay(_config.Relay);
            k.Listen(control, l => l.Protocols = HttpProtocols.Http2);
        });
        _web = b.Build();
        _web.MapPeerRelay(_config.Relay, Health);
        var svc = _web.MapGrpcService<FakeGameControlService>();
        if (control.Port != 0) svc.WithMetadata(new RelayListenerPort(control.Port));
        await _web.StartAsync(ct);
        ControlEndPoint = control.Port != 0 ? control : BoundControl();

        var f = _config.Fake;
        _sdk = await HostSdk.StartAsync(_config.Args, _config.Env, new HostSdkOptions
        {
            HeartbeatInterval = TimeSpan.FromMilliseconds(f.HeartbeatMs),
            LeaseTtl = TimeSpan.FromMilliseconds(f.LeaseTtlMs),
            MainThreadStallLimit = TimeSpan.FromMilliseconds(f.MainStallMs),
        }, ct);
        Wire();
        _main = new Thread(MainLoop) { IsBackground = true, Name = $"fakegame-main-{_config.InstanceId}" };
        _main.Start();
        Post(() => _ = BootAsync());
    }

    IPEndPoint BoundControl()
    {
        var addresses = _web.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses;
        return addresses.Select(a => IPEndPoint.Parse(new Uri(a).Authority)).Last();
    }

    void Health(JsonObject j)
    {
        j["fake"] = new JsonObject
        {
            ["instance"] = _config.InstanceId, ["booted"] = Boot is not null, ["mapReady"] = MapIsReady,
            ["hung"] = _hung, ["draining"] = _draining,
        };
        if (_hung) j["ok"] = false; // liveness fails as a hung engine's would
    }

    /// <summary>A clean exit (the Shutdown command): the SDK closes its stream, the relay ends its streams.</summary>
    public Task StopAsync(int exitCode = 0) => StopCore(exitCode, clean: true);

    /// <summary>The process dies: nothing is said to anyone.</summary>
    public Task CrashAsync(int exitCode) => StopCore(exitCode, clean: false);

    async Task StopCore(int exitCode, bool clean)
    {
        if (Interlocked.Exchange(ref _stopping, 1) == 1) { await _exited.Task; return; }
        Note(clean ? $"exit {exitCode}" : $"crash {exitCode}");
        await _stop.CancelAsync();
        _main?.Join(TimeSpan.FromSeconds(2));
        try { if (_sdk is not null) await _sdk.DisposeAsync(); } catch (Exception) { }
        try { if (_web is not null) { await _web.StopAsync(); await _web.DisposeAsync(); } } catch (Exception) { }
        _backend?.Dispose();
        _exited.TrySetResult(exitCode);
    }

    public async ValueTask DisposeAsync() => await StopAsync(0);

    // ------------------------------------------------------------------ the main loop

    /// <summary>Runs <paramref name="work"/> on the main thread; the SDK's completions resume there too (inside Pump).</summary>
    public Task<T> OnMain<T>(Func<Task<T>> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            Task<T> t;
            try { t = work(); }
            catch (Exception e) { done.TrySetException(e); return; }
            t.ContinueWith(r =>
            {
                if (r.IsFaulted) done.TrySetException(r.Exception!.InnerExceptions);
                else if (r.IsCanceled) done.TrySetCanceled();
                else done.TrySetResult(r.Result);
            }, TaskContinuationOptions.ExecuteSynchronously);
        });
        return done.Task;
    }

    void Post(Action a) => _work.Enqueue(a);

    /// <summary>A delay that resumes on the main thread (a frame timer), never on a pool thread.</summary>
    Task Delay(TimeSpan d)
    {
        var tcs = new TaskCompletionSource();
        _timers.Add((DateTime.UtcNow + d, tcs));
        return tcs.Task;
    }

    void MainLoop()
    {
        var frame = TimeSpan.FromSeconds(1.0 / Math.Max(1, _config.Fake.FrameHz));
        while (!_stop.IsCancellationRequested)
        {
            if (_hung) { Thread.Sleep(20); continue; }
            try
            {
                _sdk.Pump();
                while (_work.TryDequeue(out var w)) w();
                var now = DateTime.UtcNow;
                for (var i = _timers.Count - 1; i >= 0; i--)
                    if (_timers[i].Due <= now) { var t = _timers[i].Done; _timers.RemoveAt(i); t.TrySetResult(); }
                WatchPlayers();
            }
            catch (Exception e) { Note($"main_error {e.GetType().Name}: {e.Message}"); }
            Thread.Sleep(frame + TimeSpan.FromMilliseconds(_slowMs));
        }
    }

    async Task BootAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var boot = await _sdk.Session.Booting(_config.Map);
            if (boot.Ok)
            {
                Boot = boot.Value;
                ModuleSha256 = new LoadedAssemblyModuleSource().Find() is { } m ? Convert.ToHexStringLower(
                    System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(m.Path))) : null;
                Note($"booted {boot.Value.InstanceKind} module={boot.Value.Module} uploaded={boot.Value.Uploaded}");
                break;
            }
            Note($"booting_refused {boot.Refusal!.Error} {boot.Refusal.Reason}");
            if (boot.Error is not (HostError.Unreachable or HostError.Rejected)) return;
            await Delay(TimeSpan.FromSeconds(1));
        }
        while (!_stop.IsCancellationRequested)
        {
            var ready = await _sdk.Session.MapReady(_config.GamePort, _config.Map);
            if (ready.Ok) { MapIsReady = true; Note("map_ready"); return; }
            Note($"map_ready_refused {ready.Refusal!.Error}");
            if (ready.Error != HostError.Unreachable) return;
            await Delay(TimeSpan.FromSeconds(1));
        }
    }

    bool IsHub => Boot?.InstanceKind == "hub";

    // ------------------------------------------------------------------ players

    void WatchPlayers()
    {
        var log = _backend.Log;
        for (; _logIndex < log.Count; _logIndex++)
        {
            var r = log[_logIndex];
            if (r.Type == ToyWire.Connect) _connecting.Add(r.From); // a handshake: join once it is accepted
            foreach (var p in _players.Values)
                if (p.EndPoint is { } ep && ep.Equals(r.From)) p.LastSeen = DateTime.UtcNow;
        }
        if (Boot is null) return;
        foreach (var (ep, steam) in _backend.Accepted)
        {
            if (!_connecting.Remove(ep)) continue;
            _ = Join(steam.ToString(), ep.ToString(), ep);
        }
        var idle = TimeSpan.FromMilliseconds(_config.Fake.PlayerIdleMs);
        foreach (var p in _players.Values.Where(p => p.EndPoint is not null && DateTime.UtcNow - p.LastSeen > idle).ToList())
            _ = Leave(p, "idle");
    }

    Task Join(string steamId, string peer, IPEndPoint? endPoint)
    {
        if (_players.TryGetValue(steamId, out var existing))
        {
            existing.Peer = peer;
            existing.EndPoint = endPoint;
            existing.LastSeen = DateTime.UtcNow;
            if (existing.Lease is { IsHeld: true }) return existing.Joining ?? Task.CompletedTask;
            return existing.Joining = JoinCore(existing); // a return to this instance: take the character again
        }
        var p = new Player(steamId, peer, endPoint);
        _players[steamId] = p;
        _sdk.Session.Players = _players.Count;
        return p.Joining = JoinCore(p);
    }

    async Task JoinCore(Player p)
    {
        var joined = await _sdk.Session.PlayerJoined(p.Peer, p.SteamId);
        if (!joined.Ok || !joined.Value.Allowed)
        {
            Note($"join_refused {p.SteamId} {joined.Refusal?.Reason ?? joined.Value.Reason}");
            return;
        }
        p.ClientAddr = joined.Value.ClientAddr;
        var list = await _sdk.Characters.List(p.SteamId);
        HostCharacter? ch = list.Ok && list.Value.Count > 0 ? list.Value[0] : null;
        if (ch is null && IsHub)
        {
            var created = await _sdk.Characters.Create(p.SteamId, _config.Fake.ClassName, $"hero-{p.SteamId}");
            if (!created.Ok) { Note($"create_refused {p.SteamId} {created.Refusal!.Reason}"); return; }
            ch = created.Value;
        }
        if (ch is null) { Note($"no_character {p.SteamId} {list.Refusal?.Reason}"); return; }
        var lease = await _sdk.Characters.Lease(ch.Id);
        if (!lease.Ok) { Note($"lease_refused {p.SteamId} {lease.Refusal!.Reason}"); return; }
        p.Character = ch;
        p.Lease = lease.Value;
        p.Lease.LeaseLost += (l, why, detail) => { Note($"lease_lost {p.SteamId} {why} {detail}"); if (p.Lease == l) p.Lease = null; };
        if (!IsHub)
        {
            p.Reserve = _sdk.Items.OpenReserve(p.Lease);
            // Standing on a depth unlocks the next one (§6.5: StairsDown needs reached depth + 1).
            if (Boot is { Depth: > 0 } b && b.Depth > ch.ReachedDepth)
            {
                var reached = await p.Lease.SetReachedDepth(b.Depth);
                Note($"reached {p.SteamId} {b.Depth} {(reached.Ok ? "ok" : reached.Refusal!.Reason)}");
            }
        }
        Note($"joined {p.SteamId} {ch.Id} on {Boot?.InstanceKind}");
    }

    async Task Leave(Player p, string why)
    {
        _players.Remove(p.SteamId);
        _sdk.Session.Players = _players.Count;
        await Put(p);
        await _sdk.Session.PlayerLeft(p.SteamId, p.Peer);
        Note($"left {p.SteamId} {why}");
    }

    /// <summary>The final checkpoint and release of a player's lease, if it is still held here.</summary>
    async Task<bool> Put(Player p)
    {
        if (p.Lease is not { IsHeld: true } lease) return false;
        var cp = await lease.Checkpoint(lease.Character.Sheet);
        Note($"checkpoint {p.SteamId} {(cp.Ok ? cp.Value.Version.ToString() : cp.Refusal!.Reason)}");
        var r = await lease.Release();
        Note($"release {p.SteamId} {(r.Ok ? "ok" : r.Refusal!.Reason)}");
        p.Lease = null;
        p.Reserve = null;
        return cp.Ok;
    }

    // ------------------------------------------------------------------ commands down the stream

    void Wire()
    {
        var c = _sdk.Commands;
        c.Drain += d => { _draining = true; Note($"drain {d.Reason}"); };
        c.Say += s => Note($"say {s.Text}");
        c.Kick += k =>
        {
            Note($"kick {k.SteamId} {k.Reason}");
            if (_players.TryGetValue(k.SteamId, out var p)) _ = Leave(p, "kicked");
        };
        c.Retry += r =>
        {
            if (_players.TryGetValue(r.SteamId, out var p) && p.EndPoint is { } ep)
            {
                _backend.SendRetry(ep);
                Note($"retry {r.SteamId}");
                _ = Leave(p, "retry"); // the client is leaving for the new route
            }
            else Note($"retry_unknown {r.SteamId}");
        };
        c.Exec += e => { Note($"exec {e.Command}"); _ = e.Reply($"fake {_config.InstanceId}: {e.Command}"); };
        c.PrepareHop += h => _ = PrepareHop(h);
        c.Shutdown += s => { Note($"shutdown {s.Reason}"); _ = Task.Run(() => StopAsync(0)); };
    }

    /// <summary>The hop (§6.5): the player's last checkpoint and release here, and only then HopReady.</summary>
    async Task PrepareHop(HopRequest hop)
    {
        Note($"prepare_hop {hop.SteamId} {hop.TargetInstance}");
        if (_players.TryGetValue(hop.SteamId, out var p)) await Put(p);
        var r = await hop.Ready();
        Note($"hop_ready {hop.SteamId} {(r.Ok ? "ok" : r.Refusal!.Reason)}");
    }

    // ------------------------------------------------------------------ control (called through OnMain)

    public void Hang() { Note("hang"); _hung = true; }
    public void Slow(int ms) { _slowMs = Math.Max(0, ms); _backend.ResponseDelay = TimeSpan.FromMilliseconds(_slowMs); Note($"slow {ms}"); }

    public IReadOnlyList<string> Players => [.. _players.Keys];

    public async Task<(bool Ok, string Reason)> Simulate(string steamId)
    {
        // A join without UDP: a synthetic peer the gateway never relayed (facts drive the SDK path).
        var n = (uint)(steamId.GetHashCode() & 0xffff);
        await Join(steamId, $"127.254.{n >> 8}.{n & 0xff}:27005", null);
        return _players.TryGetValue(steamId, out var p) && p.Lease is not null ? (true, "") : (false, "join did not lease a character");
    }

    public (string CharacterId, string Peer, bool Leased, string ClientAddr)? PlayerInfo(string steamId) =>
        _players.TryGetValue(steamId, out var p) ? (p.Character?.Id ?? "", p.Peer, p.Lease is { IsHeld: true }, p.ClientAddr) : null;

    public async Task<(bool Ok, string Reason)> LeaveNow(string steamId)
    {
        if (!_players.TryGetValue(steamId, out var p)) return (false, "no_player");
        await Leave(p, "control");
        return (true, "");
    }

    (Player? P, string Reason) Leased(string steamId) =>
        !_players.TryGetValue(steamId, out var p) ? (null, "no_player")
        : p.Lease is not { IsHeld: true } ? (null, "no_lease")
        : (p, "");

    static string Why(HostRefusal? r) => r is null ? "" : $"{r.Error} {r.Reason}";

    public async Task<(bool Ok, string Reason, bool Dropped, int Tier, string Source, IReadOnlyList<string> Items, uint Seq)> Kill(string steamId, string robot, bool boss)
    {
        var (p, reason) = Leased(steamId);
        if (p is null || Boot is null) return (false, reason == "" ? "not_booted" : reason, false, -1, "none", [], 0);
        var seq = ++_killSeq;
        if (boss)
        {
            var drops = await _sdk.Items.MintDrops(p.Lease!, seq, robot, -1);
            return drops.Ok ? (true, "", true, -1, "boss", drops.Value.Select(i => i.Id).ToList(), seq) : (false, Why(drops.Refusal), false, -1, "boss", [], seq);
        }
        var roll = _rules.KillRoll(Boot.InstanceSeed, seq, robot, Boot.Depth, 1);
        if (!roll.Drop) return (true, "", false, -1, "none", [], seq);
        if (p.Reserve?.NextForTier(roll.Tier) is { } item)
        {
            var revealed = await p.Reserve.Reveal(item, seq, robot);
            return revealed.Ok ? (true, "", true, roll.Tier, "reserve", [item.Id], seq) : (false, Why(revealed.Refusal), true, roll.Tier, "reserve", [item.Id], seq);
        }
        var fallback = await _sdk.Items.MintDrops(p.Lease!, seq, robot, roll.Tier);
        return fallback.Ok ? (true, "", true, roll.Tier, "fallback", fallback.Value.Select(i => i.Id).ToList(), seq)
            : (false, Why(fallback.Refusal), true, roll.Tier, "fallback", [], seq);
    }

    async Task<(bool Ok, string Reason, IReadOnlyList<string> Ids)> Items<T>(string steamId, Func<Player, Task<HostResult<T>>> call, Func<T, IEnumerable<string>> ids)
    {
        var (p, reason) = Leased(steamId);
        if (p is null) return (false, reason, []);
        var r = await call(p);
        return r.Ok ? (true, "", ids(r.Value).ToList()) : (false, Why(r.Refusal), []);
    }

    public Task<(bool Ok, string Reason, IReadOnlyList<string> Ids)> Pickup(string steamId, string itemId) =>
        Items(steamId, p => _sdk.Items.Claim(p.Lease!, itemId), i => [i.Id]);

    public Task<(bool Ok, string Reason, IReadOnlyList<string> Ids)> Die(string steamId) =>
        Items(steamId, p => _sdk.Items.RecordDeath(p.Lease!, Vector3.Zero), l => l.Select(i => i.Id));

    public Task<(bool Ok, string Reason, IReadOnlyList<string> Ids)> Reclaim(string steamId, string entryId) =>
        Items(steamId, p => _sdk.LostAndFound.Reclaim(p.Lease!, entryId), i => [i.Id]);

    public async Task<(bool Ok, string Reason, IReadOnlyList<string> Ids)> Backpack(string steamId)
    {
        if (!_players.TryGetValue(steamId, out var p) || p.Character is null) return (false, "no_player", []);
        var r = await _sdk.Items.GetBackpack(p.Character.Id);
        return r.Ok ? (true, "", r.Value.Select(i => i.Id).ToList()) : (false, Why(r.Refusal), []);
    }

    public async Task<(bool Ok, string Reason, IReadOnlyList<string> Ids)> LostAndFound(string steamId)
    {
        if (!_players.TryGetValue(steamId, out var p) || p.Character is null) return (false, "no_player", []);
        var r = await _sdk.LostAndFound.List(p.Character.Id);
        return r.Ok ? (true, "", r.Value.Select(e => e.Id).ToList()) : (false, Why(r.Refusal), []);
    }

    public async Task<(bool Ok, string Reason, string PartyId)> Party(IReadOnlyList<string> steamIds)
    {
        if (steamIds.Count == 0) return (false, "no_members", "");
        var (leader, reason) = Leased(steamIds[0]);
        if (leader is null) return (false, reason, "");
        var party = await _sdk.Parties.Create(leader.Lease!);
        if (!party.Ok) return (false, Why(party.Refusal), "");
        foreach (var s in steamIds.Skip(1))
        {
            var (m, why) = Leased(s);
            if (m is null) return (false, $"{s}: {why}", party.Value.Id);
            var inv = await _sdk.Parties.Invite(leader.Lease!, party.Value.Id, m.Character!.Id);
            if (!inv.Ok) return (false, Why(inv.Refusal), party.Value.Id);
            var acc = await _sdk.Parties.Accept(m.Lease!, party.Value.Id);
            if (!acc.Ok) return (false, Why(acc.Refusal), party.Value.Id);
        }
        return (true, "", party.Value.Id);
    }

    public enum Trip { Descend, TownPortal, ReturnThroughPortal }

    public async Task<(bool Ok, string Reason, string State, string InstanceId)> Travel(Trip trip, string steamId, int depth, string partyId)
    {
        var (p, reason) = Leased(steamId);
        if (p is null) return (false, reason, "refused", "");
        var r = trip switch
        {
            Trip.TownPortal => await _sdk.Travel.TownPortal(p.Lease!),
            Trip.ReturnThroughPortal => await _sdk.Travel.ReturnThroughPortal(p.Lease!),
            _ when IsHub => await _sdk.Travel.RequestDescent(p.Lease!, partyId, depth),
            _ => await _sdk.Travel.StairsDown(p.Lease!, partyId, depth),
        };
        Note($"travel {trip} {steamId} {depth} {(r.Ok ? $"{r.Value.State} {r.Value.InstanceId}" : Why(r.Refusal))}");
        return r.Ok ? (true, r.Value.Reason, r.Value.State.ToString().ToLowerInvariant(), r.Value.InstanceId) : (false, Why(r.Refusal), "refused", "");
    }
}
