using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.FakeGame;

namespace Descent.Service.Tests.EndToEnd;

/// <summary>
/// An IInstanceHost whose pods are fake game servers hosted in this process (plan §7.6): each pod
/// gets its own loopback address (127.R.n.1, R random per host so parallel runs do not meet) and
/// runs <see cref="FakeGameHost"/> with the PodSpec's arguments and environment — the token from
/// the pod's Secret — with the pod-local addresses rewritten to the pod's own. The pod is Ready
/// once the fake's relay listens; a fake that exits makes the pod Succeeded (0) or Failed; a
/// delete stops the fake (SIGTERM: a clean exit) and removes the pod.
/// </summary>
public sealed class FakeGameInstanceHost : IInstanceHost, IAsyncDisposable
{
    sealed class Pod
    {
        public required PodSpec Spec;
        public required string Uid;
        public required string Ip;
        public required int N;
        public PodPhase Phase = PodPhase.Pending;
        public bool Ready;
        public int? ExitCode;
        public bool Deleting;
        public FakeGameHost? Game;
        public string Log = "";
        public PodStatus Status() => new(Spec.Name, Uid, Phase, Ready, Ip, 0, ExitCode, Spec.Labels);
    }

    readonly Lock _gate = new();
    readonly Dictionary<string, Pod> _pods = [];
    readonly List<Channel<PodEvent>> _watchers = [];
    readonly ConcurrentDictionary<string, FakeGameHost> _games = new();
    readonly List<Task> _background = [];
    readonly int _octet = Random.Shared.Next(20, 100);
    int _n;

    /// <summary>Added to every pod's environment (FAKEGAME_* timers for fast facts).</summary>
    public Dictionary<string, string?> ExtraEnv { get; } = [];
    public ConcurrentQueue<(string Op, string Name)> Calls { get; } = new();

    /// <summary>The running fake of an instance id, if its pod is up.</summary>
    public FakeGameHost? Game(string instanceId) => _games.GetValueOrDefault(instanceId);
    public IReadOnlyCollection<FakeGameHost> Games => [.. _games.Values];
    public PodStatus? Get(string name) { lock (_gate) return _pods.TryGetValue(name, out var p) ? p.Status() : null; }

    public Task<PodRef> Create(PodSpec spec, CancellationToken ct = default)
    {
        Pod pod;
        lock (_gate)
        {
            if (_pods.ContainsKey(spec.Name)) throw new InvalidOperationException($"pod {spec.Name} already exists");
            var n = ++_n;
            pod = new Pod { Spec = spec, Uid = Guid.NewGuid().ToString(), Ip = $"127.{_octet}.{n}.1", N = n };
            _pods[spec.Name] = pod;
            Emit(PodEventType.Added, pod);
        }
        Calls.Enqueue(("create", spec.Name));
        lock (_gate) _background.Add(Task.Run(() => Run(pod)));
        return Task.FromResult(new PodRef(spec.Name, pod.Uid));
    }

    Dictionary<string, string?> EnvOf(Pod pod)
    {
        var game = pod.Spec.Containers[0];
        var env = new Dictionary<string, string?>();
        foreach (var e in game.Env)
            env[e.Name] = e.FromSecret is { } s ? pod.Spec.Secret?.Data.GetValueOrDefault(s.Key) : e.Value;
        // The pod's own address in place of the pod-local ones (a pod has its own IP; here they share a host).
        foreach (var k in env.Keys.Where(k => k.StartsWith("RELAY_", StringComparison.Ordinal) || k == "DESCENT_SIDECAR_INFO").ToList())
            env[k] = env[k]?.Replace("0.0.0.0", pod.Ip).Replace("127.0.0.1", pod.Ip);
        env["RELAY_AddressPool"] = $"127.{_octet + 100}.{pod.N}.0/24";
        env["FAKEGAME_ControlListen"] = $"{pod.Ip}:5020";
        foreach (var (k, v) in ExtraEnv) env[k] = v;
        return env;
    }

    async Task Run(Pod pod)
    {
        FakeGameHost game;
        try { game = await FakeGameHost.StartAsync(pod.Spec.Containers[0].Args, EnvOf(pod)); }
        catch (Exception e)
        {
            lock (_gate)
            {
                pod.Log += $"start failed: {e.Message}\n";
                if (!_pods.ContainsKey(pod.Spec.Name)) return;
                pod.Phase = PodPhase.Failed;
                pod.ExitCode = 70;
                Emit(PodEventType.Modified, pod);
            }
            return;
        }
        lock (_gate)
        {
            pod.Game = game;
            if (pod.Deleting || !_pods.ContainsKey(pod.Spec.Name)) { _ = game.DisposeAsync(); return; }
            _games[game.InstanceId] = game;
            pod.Phase = PodPhase.Running;
            pod.Ready = true; // the relay (and its /healthz) is up
            Emit(PodEventType.Modified, pod);
        }
        var code = await game.Exited;
        lock (_gate)
        {
            pod.Log += string.Join('\n', game.Server.Events) + "\n";
            _games.TryRemove(game.InstanceId, out _);
            if (pod.Deleting || !_pods.ContainsKey(pod.Spec.Name)) return;
            pod.ExitCode = code;
            pod.Phase = code == 0 ? PodPhase.Succeeded : PodPhase.Failed;
            pod.Ready = false;
            Emit(PodEventType.Modified, pod);
        }
    }

    public Task<bool> Delete(string name, string uid, TimeSpan grace, CancellationToken ct = default)
    {
        Pod pod;
        lock (_gate)
        {
            if (!_pods.TryGetValue(name, out var p) || p.Uid != uid) return Task.FromResult(false);
            if (p.Deleting) return Task.FromResult(true);
            p.Deleting = true;
            pod = p;
        }
        Calls.Enqueue(("delete", name));
        lock (_gate) _background.Add(Task.Run(async () =>
        {
            if (pod.Game is { } g) { await g.StopAsync(0); _games.TryRemove(g.InstanceId, out _); }
            lock (_gate)
            {
                if (!_pods.Remove(name)) return;
                pod.Phase = pod.ExitCode is null or 0 ? PodPhase.Succeeded : PodPhase.Failed;
                pod.Ready = false;
                Emit(PodEventType.Deleted, pod);
            }
        }));
        return Task.FromResult(true);
    }

    void Emit(PodEventType type, Pod pod)
    {
        var e = new PodEvent(type, pod.Status());
        foreach (var w in _watchers) w.Writer.TryWrite(e);
    }

    public async IAsyncEnumerable<PodEvent> Watch([EnumeratorCancellation] CancellationToken ct)
    {
        var ch = Channel.CreateUnbounded<PodEvent>();
        lock (_gate)
        {
            foreach (var p in _pods.Values) ch.Writer.TryWrite(new PodEvent(PodEventType.Added, p.Status()));
            _watchers.Add(ch);
        }
        try { await foreach (var e in ch.Reader.ReadAllAsync(ct)) yield return e; }
        finally { lock (_gate) _watchers.Remove(ch); }
    }

    public Task<string> Logs(string name, int tail, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_pods.TryGetValue(name, out var p)) throw new KeyNotFoundException(name);
            var events = p.Game is { } g ? string.Join('\n', g.Server.Events) : p.Log;
            return Task.FromResult(string.Join('\n', events.Split('\n').TakeLast(tail)));
        }
    }

    public Task<IReadOnlyList<PodStatus>> List(string labelSelector, CancellationToken ct = default)
    {
        var want = labelSelector.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(kv => kv.Split('=', 2)).ToList();
        lock (_gate)
            return Task.FromResult<IReadOnlyList<PodStatus>>(_pods.Values
                .Where(p => want.All(kv => p.Spec.Labels.TryGetValue(kv[0], out var v) && v == kv[1])).Select(p => p.Status()).ToList());
    }

    public Task<JobRef> RunJob(JobSpec spec, CancellationToken ct = default) => throw new NotSupportedException("no bake jobs with fake game pods");
    public IAsyncEnumerable<JobEvent> WatchJob(string name, CancellationToken ct) => throw new NotSupportedException("no bake jobs with fake game pods");

    public async ValueTask DisposeAsync()
    {
        foreach (var g in _games.Values) { try { await g.DisposeAsync(); } catch (Exception) { } }
        Task[] pending;
        lock (_gate) pending = [.. _background];
        try { await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception) { }
    }
}
