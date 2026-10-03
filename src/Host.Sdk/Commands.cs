using P = SourceSharp.Host.Proto;

namespace SourceSharp.Host.Sdk;

public sealed record DrainCommand(string CommandId, string Reason);
public sealed record KickCommand(string CommandId, string SteamId, string Reason);
public sealed record ShutdownCommand(string CommandId, string Reason);
public sealed record RetryCommand(string CommandId, string SteamId);
public sealed record SayCommand(string CommandId, string Text);

/// <summary>An admin console command (§11); the game runs it and replies with its output.</summary>
public sealed class ExecRequest
{
    readonly SdkCore _core;
    internal ExecRequest(SdkCore core, string commandId, string command) { _core = core; CommandId = commandId; Command = command; }
    public string CommandId { get; }
    public string Command { get; }

    public Task<HostResult> Reply(string output) => _core.Background(async () =>
    {
        var request = new P.ExecResultRequest { CommandId = CommandId, Output = output };
        return (await _core.Rpc.Call((m, d, ct) => _core.Instance.ExecResultAsync(request, m, d, ct)).ConfigureAwait(false)).Untyped();
    });
}

/// <summary>
/// The service is moving a player to another instance (§6.5). The game checkpoints the player
/// first, then calls <see cref="Ready"/>; the SDK sends HopReady with this hop id. Nothing is sent
/// until the game says so.
/// <para>
/// <see cref="IsSameProcess"/> (local mode only, D-H13): the target is served by this process.
/// After Ready() the game changes map to <see cref="Map"/> itself; when it calls
/// <c>Booting(Map)</c> the SDK first becomes the target instance, so Booting and MapReady run as
/// that instance and the BootInfo is the target's. Hosted, a process never changes map.
/// </para>
/// </summary>
public sealed class HopRequest
{
    readonly SdkCore _core;
    readonly string _requestId = Rpc.NewRequestId();
    Task<HostResult>? _sent;
    internal HopRequest(SdkCore core, string commandId, string steamId, string hopId, string targetInstance, string map = "", HostLocalFile? local = null)
    {
        _core = core; CommandId = commandId; SteamId = steamId; HopId = hopId; TargetInstance = targetInstance; Map = map;
        Target = local is null ? null : new HopTarget(local.Map ?? map, local.LevelFiles ?? []);
    }
    public string CommandId { get; }
    public string SteamId { get; }
    public string HopId { get; }
    public string TargetInstance { get; }
    /// <summary>The target's map ("" from a service that does not send it).</summary>
    public string Map { get; }
    /// <summary>This process serves the target: change map to <see cref="Map"/> after Ready(), don't wait for a Retry.</summary>
    public bool IsSameProcess => Target is not null;
    /// <summary>Same-process only: what to load. Link <see cref="HopTarget.LevelFiles"/> into &lt;mod&gt;/pool/maps/, then change map.</summary>
    public HopTarget? Target { get; }
    public bool Completed => _sent is not null;

    /// <summary>Sends HopReady once; a second call returns the first call's task.</summary>
    public Task<HostResult> Ready() => _sent ??= _core.Background(async () =>
    {
        var request = new P.HopReadyRequest { RequestId = _requestId, HopId = HopId, Steamid = SteamId };
        return (await _core.Rpc.Call((m, d, ct) => _core.Instance.HopReadyAsync(request, m, d, ct)).ConfigureAwait(false)).Untyped();
    });
}

/// <summary>
/// D-H14: the party's next level down, claimed and booting while they play. The game's client
/// side fetches <c>{DownloadUrl}maps/{Map}.bsp.bz2</c> in the background (stock Source clients
/// only download at connect), so the map is local when the hop comes. <see cref="DownloadUrl"/>
/// is empty when the service has no public fastdl; the map name carries the level's hash.
/// <see cref="MapSha256"/> is the SHA-256 of the unpacked .bsp (32 bytes), to verify a download
/// before installing it and a cached map before skipping it; null when the host does not know it.
/// </summary>
public sealed record NextLevelInfo(string CommandId, int Depth, string Map, string DownloadUrl, byte[]? MapSha256 = null)
{
    /// <summary>The bz2's full URL, or null when there is nowhere to fetch it from.</summary>
    public string? MapUrl => DownloadUrl.Length == 0 ? null : $"{DownloadUrl.TrimEnd('/')}/maps/{Map}.bsp.bz2";
}

/// <summary>D-H16: a teleporter of this party was placed, moved or cleared: lay out the pads again from ITravel.ListPartyAnchors. <paramref name="PartyId"/> is empty for a player in no party.</summary>
public sealed record AnchorsChangedInfo(string CommandId, string PartyId);

/// <summary>D-H17: a store key this server can read changed (TOWN, or this level's data written by town). Read it with IHostStore.</summary>
public sealed record StoreChangedInfo(string CommandId, HostStoreScope Scope, string Owner, string Key);

/// <summary>
/// A same-process hop's target (D-H13 local mode): its map, and its level's .bsp/.nav3d/.map2d as
/// absolute paths (empty for the hub, whose map ships with the mod).
/// </summary>
public sealed record HopTarget(string Map, IReadOnlyList<string> LevelFiles);

/// <summary>The down-stream commands (§6.1) as events. Every event is raised inside Pump(), on the game's main thread.</summary>
public interface IHostCommands
{
    event Action<DrainCommand>? Drain;
    event Action<KickCommand>? Kick;
    event Action<ShutdownCommand>? Shutdown;
    event Action<RetryCommand>? Retry;
    event Action<SayCommand>? Say;
    event Action<ExecRequest>? Exec;
    event Action<HopRequest>? PrepareHop;
    /// <summary>D-H14: the party's next level down is booting; its map can be fetched in the background.</summary>
    event Action<NextLevelInfo>? NextLevel;
    event Action<AnchorsChangedInfo>? AnchorsChanged;
    event Action<StoreChangedInfo>? StoreChanged;
}

internal sealed class HostCommands(SdkCore core) : IHostCommands
{
    public event Action<DrainCommand>? Drain;
    public event Action<KickCommand>? Kick;
    public event Action<ShutdownCommand>? Shutdown;
    public event Action<RetryCommand>? Retry;
    public event Action<SayCommand>? Say;
    public event Action<ExecRequest>? Exec;
    public event Action<HopRequest>? PrepareHop;
    public event Action<NextLevelInfo>? NextLevel;
    public event Action<AnchorsChangedInfo>? AnchorsChanged;
    public event Action<StoreChangedInfo>? StoreChanged;

    /// <summary>Called on the stream's reader thread: nothing is raised here, only queued.</summary>
    public void OnCommand(P.ServerCommand c)
    {
        var id = c.CommandId;
        switch (c.KindCase)
        {
            case P.ServerCommand.KindOneofCase.Drain: core.Main.Post(() => Drain?.Invoke(new DrainCommand(id, c.Drain.Reason))); break;
            case P.ServerCommand.KindOneofCase.Kick: core.Main.Post(() => Kick?.Invoke(new KickCommand(id, c.Kick.Steamid, c.Kick.Reason))); break;
            case P.ServerCommand.KindOneofCase.Shutdown: core.Main.Post(() => Shutdown?.Invoke(new ShutdownCommand(id, c.Shutdown.Reason))); break;
            case P.ServerCommand.KindOneofCase.Retry: core.Main.Post(() => Retry?.Invoke(new RetryCommand(id, c.Retry.Steamid))); break;
            case P.ServerCommand.KindOneofCase.Say: core.Main.Post(() => Say?.Invoke(new SayCommand(id, c.Say.Text))); break;
            case P.ServerCommand.KindOneofCase.Exec: core.Main.Post(() => Exec?.Invoke(new ExecRequest(core, id, c.Exec.Command))); break;
            case P.ServerCommand.KindOneofCase.PrepareHop:
            {
                var h = c.PrepareHop;
                var local = SameProcessTarget(h);
                if (local is not null) core.PendingLocalHop = local;
                core.Main.Post(() => PrepareHop?.Invoke(new HopRequest(core, id, h.Steamid, h.HopId, h.TargetInstance, h.Map, local)));
                break;
            }
            case P.ServerCommand.KindOneofCase.NextLevel:
            {
                var n = c.NextLevel;
                var sha = n.MapSha256.Length == 32 ? n.MapSha256.ToByteArray() : null;
                core.Main.Post(() => NextLevel?.Invoke(new NextLevelInfo(id, n.Depth, n.Map, n.DownloadUrl, sha)));
                break;
            }
            case P.ServerCommand.KindOneofCase.LeaseRevoked:
                core.Leases.Revoked(c.LeaseRevoked.CharacterId, c.LeaseRevoked.Reason);
                break;
            case P.ServerCommand.KindOneofCase.AnchorsChanged:
                core.Main.Post(() => AnchorsChanged?.Invoke(new AnchorsChangedInfo(id, c.AnchorsChanged.PartyId)));
                break;
            case P.ServerCommand.KindOneofCase.StoreChanged:
            {
                var s = c.StoreChanged;
                core.Main.Post(() => StoreChanged?.Invoke(new StoreChangedInfo(id, (HostStoreScope)(int)s.Scope, s.Owner, s.Key)));
                break;
            }
        }
    }

    /// <summary>The target's connection record when this is a same-process hop to it, else null.</summary>
    static HostLocalFile? SameProcessTarget(P.PrepareHop h)
    {
        if (h.LocalConnection.Length == 0 || h.Map.Length == 0) return null;
        try
        {
            var f = HostLocalFile.Parse(h.LocalConnection, "PrepareHop.local_connection");
            return f.InstanceId == h.TargetInstance ? f : null;
        }
        catch (HostConfigException) { return null; }
    }
}
