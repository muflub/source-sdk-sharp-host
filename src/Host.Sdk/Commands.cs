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
/// </summary>
public sealed class HopRequest
{
    readonly SdkCore _core;
    readonly string _requestId = Rpc.NewRequestId();
    Task<HostResult>? _sent;
    internal HopRequest(SdkCore core, string commandId, string steamId, string hopId, string targetInstance)
    {
        _core = core; CommandId = commandId; SteamId = steamId; HopId = hopId; TargetInstance = targetInstance;
    }
    public string CommandId { get; }
    public string SteamId { get; }
    public string HopId { get; }
    public string TargetInstance { get; }
    public bool Completed => _sent is not null;

    /// <summary>Sends HopReady once; a second call returns the first call's task.</summary>
    public Task<HostResult> Ready() => _sent ??= _core.Background(async () =>
    {
        var request = new P.HopReadyRequest { RequestId = _requestId, HopId = HopId, Steamid = SteamId };
        return (await _core.Rpc.Call((m, d, ct) => _core.Instance.HopReadyAsync(request, m, d, ct)).ConfigureAwait(false)).Untyped();
    });
}

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
                core.Main.Post(() => PrepareHop?.Invoke(new HopRequest(core, id, c.PrepareHop.Steamid, c.PrepareHop.HopId, c.PrepareHop.TargetInstance)));
                break;
            case P.ServerCommand.KindOneofCase.LeaseRevoked:
                core.Leases.Revoked(c.LeaseRevoked.CharacterId, c.LeaseRevoked.Reason);
                break;
        }
    }
}
