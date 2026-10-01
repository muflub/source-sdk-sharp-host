using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using SourceSharp.Host.Abstractions;

namespace SourceSharp.Host.Instances;

/// <summary>
/// What the SDK's local mode reads to connect (D-H13); the same record Host.Sdk defines as
/// HostLocalFile. <see cref="Map"/> is what a pod's +map would carry, <see cref="Port"/> the
/// -port the game must bind (the row's ExpectedPort), <see cref="LevelFiles"/> the level's
/// .bsp/.nav3d/.map2d in the local pool (a pod's init container fetches them; locally the game
/// links them into &lt;mod&gt;/pool/maps/). Null for the hub.
/// </summary>
public sealed record LocalConnection(string Service, string InstanceId, string Token,
    string? Map = null, IReadOnlyList<string>? LevelFiles = null, int? Port = null);

/// <summary>The connection record a local instance's game would start from, for a same-process hop to it.</summary>
public interface ILocalConnections
{
    /// <summary>The instance's connection record as written to its file (HostLocalFile JSON); null when it has no pod.</summary>
    string? For(string instanceId);
}

/// <summary>
/// D-H13's instance host: no pods. "Creating" a pod records it as running and ready (no IP, so
/// no gateway route) and writes the connection file the SDK's local mode reads: local-host.json
/// for the hub, local-level-&lt;id&gt;.json for a level (a second game process can take it).
/// The developer's own game process is the pod. All of them share one machine, so a level gets
/// its own port (the hub keeps the configured one, the port a hand-started game uses). With
/// <paramref name="sameProcess"/> one game serves every instance in turn, so all share that port.
/// </summary>
public sealed class LocalInstanceHost(string directory, Func<string> serviceAddress,
    string? levelsDirectory = null, Func<int, bool>? portFree = null, bool sameProcess = false) : IInstanceHost, ILocalConnections
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>The level files a game needs beside its map (LevelStorage.InternalExtensions).</summary>
    public static readonly IReadOnlyList<string> LevelExtensions = [".bsp", ".nav3d", ".map2d"];

    readonly ConcurrentDictionary<string, PodStatus> _pods = new();
    readonly ConcurrentDictionary<int, string> _ports = new();   // port → pod name, or "" while reserved
    readonly ConcurrentDictionary<string, string> _connections = new(); // instance id → its file's JSON
    readonly Func<int, bool> _portFree = portFree ?? UdpPortFree;
    readonly Channel<PodEvent> _events = Channel.CreateUnbounded<PodEvent>();

    public static string FileFor(string directory, IReadOnlyDictionary<string, string> labels) =>
        Path.Combine(directory, labels.TryGetValue(GamePodLabels.Kind, out var k) && k == "hub"
            ? "local-host.json"
            : $"local-level-{labels.GetValueOrDefault(GamePodLabels.Instance, "unknown")}.json");

    public async Task<PodRef> Create(PodSpec spec, CancellationToken ct = default)
    {
        var uid = Guid.NewGuid().ToString("N");
        var instanceId = spec.Labels.GetValueOrDefault(GamePodLabels.Instance, spec.Name);
        var token = spec.Secret?.Data.GetValueOrDefault(GamePodBuilder.SecretKey)
                    ?? throw new InvalidOperationException($"pod {spec.Name} carries no token secret");
        var hub = spec.Labels.GetValueOrDefault(GamePodLabels.Kind) == "hub";
        var args = spec.Containers.FirstOrDefault()?.Args ?? [];
        var map = ArgAfter(args, "+map");
        var port = int.TryParse(ArgAfter(args, "-port"), out var p) ? p : (int?)null;
        IReadOnlyList<string>? levelFiles = null;
        if (!hub && map is not null && levelsDirectory is not null)
        {
            levelFiles = LevelExtensions.Select(e => Path.GetFullPath(Path.Combine(levelsDirectory, map + e))).ToList();
            if (!File.Exists(levelFiles[0]))
            {
                if (port is { } reserved) _ports.TryRemove(reserved, out _);
                throw new InvalidOperationException($"local level {map}: {levelFiles[0]} is not in the local pool");
            }
        }
        if (port is { } bound) _ports[bound] = spec.Name;
        Directory.CreateDirectory(directory);
        var file = FileFor(directory, spec.Labels);
        var tmp = file + ".tmp";
        var json = JsonSerializer.Serialize(new LocalConnection(serviceAddress(), instanceId, token, map, levelFiles, port), Json);
        await File.WriteAllTextAsync(tmp, json, ct);
        _connections[instanceId] = json;
        File.Move(tmp, file, overwrite: true);
        var status = new PodStatus(spec.Name, uid, PodPhase.Running, Ready: true, Ip: null, 0, null, spec.Labels);
        _pods[spec.Name] = status;
        await _events.Writer.WriteAsync(new PodEvent(PodEventType.Added, status), ct);
        return new PodRef(spec.Name, uid);
    }

    public async IAsyncEnumerable<PodEvent> Watch([EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var p in _pods.Values) yield return new PodEvent(PodEventType.Added, p);
        await foreach (var e in _events.Reader.ReadAllAsync(ct)) yield return e;
    }

    public async Task<bool> Delete(string name, string uid, TimeSpan grace, CancellationToken ct = default)
    {
        if (!_pods.TryGetValue(name, out var p) || p.Uid != uid) return false;
        _pods.TryRemove(name, out _);
        foreach (var (port, owner) in _ports)
            if (owner == name) _ports.TryRemove(port, out _);
        if (p.Labels.TryGetValue(GamePodLabels.Instance, out var instance)) _connections.TryRemove(instance, out _);
        try { File.Delete(FileFor(directory, p.Labels)); } catch (IOException) { }
        await _events.Writer.WriteAsync(new PodEvent(PodEventType.Deleted, p with { Phase = PodPhase.Succeeded, Ready = false }), ct);
        return true;
    }

    /// <summary>
    /// The hub: <paramref name="configured"/>. A level: the first port above it that no instance of
    /// this host holds and nothing on the machine has bound, reserved until its pod is deleted.
    /// </summary>
    public int GamePort(InstanceKind kind, int configured)
    {
        if (kind == InstanceKind.Hub || sameProcess) return configured;
        for (var port = configured + 1; port <= ushort.MaxValue; port++)
            if (_portFree(port) && _ports.TryAdd(port, ""))
                return port;
        throw new InvalidOperationException($"no free UDP port above {configured}");
    }

    public string? For(string instanceId) => _connections.GetValueOrDefault(instanceId);

    static bool UdpPortFree(int port)
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Bind(new IPEndPoint(IPAddress.Any, port));
            return true;
        }
        catch (SocketException) { return false; }
    }

    static string? ArgAfter(IReadOnlyList<string> args, string flag)
    {
        var i = args.ToList().IndexOf(flag);
        return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
    }

    public Task<string> Logs(string name, int tail, CancellationToken ct = default) =>
        Task.FromResult("(local mode: the game's log is in its own console)");

    public Task<IReadOnlyList<PodStatus>> List(string labelSelector, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PodStatus>>(_pods.Values.ToList());

    public Task<JobRef> RunJob(JobSpec spec, CancellationToken ct = default) =>
        throw new HostRefusal(RefusalCode.Unimplemented, "local_mode", "bake Jobs need Kubernetes; upload a room pack instead");

    public async IAsyncEnumerable<JobEvent> WatchJob(string name, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}
