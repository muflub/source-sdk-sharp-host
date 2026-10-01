using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using SourceSharp.Host.Abstractions;

namespace SourceSharp.Host.Instances;

/// <summary>What the SDK's local mode reads to connect (D-H13); the same record Host.Sdk defines.</summary>
public sealed record LocalConnection(string Service, string InstanceId, string Token);

/// <summary>
/// D-H13's instance host: no pods. "Creating" a pod records it as running and ready (no IP, so
/// no gateway route) and writes the connection file the SDK's local mode reads: local-host.json
/// for the hub, local-level-&lt;id&gt;.json for a level (a second game process can take it).
/// The developer's own game process is the pod.
/// </summary>
public sealed class LocalInstanceHost(string directory, Func<string> serviceAddress) : IInstanceHost
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    readonly ConcurrentDictionary<string, PodStatus> _pods = new();
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
        Directory.CreateDirectory(directory);
        var file = FileFor(directory, spec.Labels);
        var tmp = file + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(new LocalConnection(serviceAddress(), instanceId, token), Json), ct);
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
        try { File.Delete(FileFor(directory, p.Labels)); } catch (IOException) { }
        await _events.Writer.WriteAsync(new PodEvent(PodEventType.Deleted, p with { Phase = PodPhase.Succeeded, Ready = false }), ct);
        return true;
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
