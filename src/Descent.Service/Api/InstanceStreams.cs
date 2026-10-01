using System.Collections.Concurrent;
using System.Threading.Channels;
using SourceSharp.Host.Abstractions;
using P = SourceSharp.Host.Proto;

namespace Descent.Service.Api;

/// <summary>
/// The down half of each pod's Connect stream (plan §6.1): the game process hosts no gRPC
/// server, so every command the service must push travels here. One writer per instance;
/// a newer stream for the same instance replaces the older.
/// </summary>
public sealed class InstanceStreams : IInstanceCommands
{
    sealed class Stream(long generation)
    {
        public long Generation { get; } = generation;
        public Channel<P.ServerCommand> Out { get; } = Channel.CreateBounded<P.ServerCommand>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.Wait });
    }

    readonly ConcurrentDictionary<string, Stream> _streams = new();
    long _generation;

    public bool IsOpen(string instanceId) => _streams.ContainsKey(instanceId);

    /// <summary>Registers a stream; returns its reader and a token that unregisters only this generation.</summary>
    public (ChannelReader<P.ServerCommand> Reader, long Generation) Open(string instanceId)
    {
        var s = new Stream(Interlocked.Increment(ref _generation));
        if (_streams.TryGetValue(instanceId, out var old)) old.Out.Writer.TryComplete();
        _streams[instanceId] = s;
        return (s.Out.Reader, s.Generation);
    }

    /// <summary>Unregisters this generation; false when a newer stream has already replaced it (a reconnect).</summary>
    public bool Close(string instanceId, long generation)
    {
        if (_streams.TryGetValue(instanceId, out var s) && s.Generation == generation && _streams.TryRemove(new(instanceId, s)))
        {
            s.Out.Writer.TryComplete();
            return true;
        }
        return false;
    }

    public async Task<bool> Send(string instanceId, P.ServerCommand command, CancellationToken ct = default)
    {
        if (!_streams.TryGetValue(instanceId, out var s)) return false;
        if (string.IsNullOrEmpty(command.CommandId)) command.CommandId = Ulid.New(DateTimeOffset.UtcNow);
        try { await s.Out.Writer.WriteAsync(command, ct); return true; }
        catch (ChannelClosedException) { return false; }
    }

    public Task<bool> Send(string instanceId, InstanceCommand command, CancellationToken ct = default) =>
        Send(instanceId, ToProto(command), ct);

    public static string HopId(string targetInstanceId, string steamId) => $"{targetInstanceId}:{steamId}";

    public static P.ServerCommand ToProto(InstanceCommand command) => command switch
    {
        InstanceCommand.Drain d => new P.ServerCommand { Drain = new P.Drain { Reason = d.Reason } },
        InstanceCommand.Shutdown s => new P.ServerCommand { Shutdown = new P.Shutdown { Reason = s.Reason } },
        InstanceCommand.Kick k => new P.ServerCommand { Kick = new P.Kick { Steamid = k.SteamId, Reason = k.Reason } },
        InstanceCommand.Retry r => new P.ServerCommand { Retry = new P.Retry { Steamid = r.SteamId } },
        InstanceCommand.PrepareHop h => new P.ServerCommand
        {
            PrepareHop = new P.PrepareHop { Steamid = h.SteamId, TargetInstance = h.TargetInstanceId, HopId = HopId(h.TargetInstanceId, h.SteamId) },
        },
        InstanceCommand.Say s => new P.ServerCommand { Say = new P.Say { Text = s.Text } },
        InstanceCommand.Exec e => new P.ServerCommand { Exec = new P.Exec { Command = e.Command } },
        InstanceCommand.LeaseRevoked r => new P.ServerCommand { LeaseRevoked = new P.LeaseRevoked { CharacterId = r.CharacterId, Reason = r.Reason } },
        _ => throw new ArgumentOutOfRangeException(nameof(command), command.GetType().Name),
    };
}

/// <summary>What pods said recently: Exec output and batched log lines, for the admin console and log tail.</summary>
public sealed class InstanceChatter
{
    readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _logs = new();
    readonly ConcurrentDictionary<string, string> _exec = new();
    public const int KeepLines = 2000;

    public void Log(string instanceId, IEnumerable<string> lines)
    {
        var q = _logs.GetOrAdd(instanceId, _ => new());
        foreach (var l in lines) q.Enqueue(l);
        while (q.Count > KeepLines && q.TryDequeue(out _)) { }
    }

    public IReadOnlyList<string> Tail(string instanceId, int n) =>
        _logs.TryGetValue(instanceId, out var q) ? q.TakeLast(n).ToList() : [];

    public void ExecResult(string commandId, string output) => _exec[commandId] = output;
    public string? ExecOutput(string commandId) => _exec.TryGetValue(commandId, out var o) ? o : null;
}
