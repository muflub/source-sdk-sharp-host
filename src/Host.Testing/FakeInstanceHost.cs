using System.Runtime.CompilerServices;
using System.Threading.Channels;
using SourceSharp.Host.Abstractions;

namespace SourceSharp.Host.Testing;

/// <summary>
/// An in-memory pod store (plan §7.2): creates pods with fresh uids, scripted phase and
/// readiness changes, deletes that honour the grace on the given clock, a watch stream, and
/// Secrets owned by their pod (deleted with it). Every event is also queued in
/// <see cref="TakePending"/> so a fact can deliver events to a manager by hand, in order.
/// </summary>
public sealed class FakeInstanceHost(TimeProvider clock) : IInstanceHost
{
    sealed class Pod
    {
        public required PodSpec Spec;
        public required string Uid;
        public PodPhase Phase = PodPhase.Pending;
        public bool Ready;
        public int Restarts;
        public int? ExitCode;
        public string? Ip;
        public bool Terminating;
        public ITimer? GraceTimer;
        public string Log = "";
        public PodStatus Status() => new(Spec.Name, Uid, Phase, Ready, Ip, Restarts, ExitCode, Spec.Labels);
    }

    readonly object _gate = new();
    readonly Dictionary<string, Pod> _pods = [];
    readonly List<PodEvent> _pending = [];
    readonly List<Channel<PodEvent>> _watchers = [];
    readonly Dictionary<string, (JobSpec Spec, string Uid, JobPhase Phase)> _jobs = [];
    readonly List<Channel<JobEvent>> _jobWatchers = [];
    int _ips;

    /// <summary>Every call that changes a pod: ("create", name, uid, null) and ("delete", name, uid, grace).</summary>
    public List<(string Op, string Name, string Uid, TimeSpan? Grace)> Calls { get; } = [];
    public List<PodSpec> Created { get; } = [];
    /// <summary>Live Secrets by name, with the uid of the pod that owns them.</summary>
    public Dictionary<string, (SecretSpec Secret, string OwnerUid)> Secrets { get; } = [];
    /// <summary>Called after a pod is created: an in-process fake game server hooks in here.</summary>
    public Func<PodSpec, PodRef, Task>? OnCreated { get; set; }
    /// <summary>The next Create throws this.</summary>
    public Exception? FailNextCreate { get; set; }

    public IReadOnlyList<PodEvent> TakePending()
    {
        lock (_gate) { var e = _pending.ToList(); _pending.Clear(); return e; }
    }

    public PodStatus? Get(string name) { lock (_gate) return _pods.TryGetValue(name, out var p) ? p.Status() : null; }
    public IReadOnlyList<string> Names { get { lock (_gate) return [.. _pods.Keys]; } }

    public async Task<PodRef> Create(PodSpec spec, CancellationToken ct = default)
    {
        PodRef r;
        lock (_gate)
        {
            if (FailNextCreate is { } fail) { FailNextCreate = null; throw fail; }
            if (_pods.ContainsKey(spec.Name)) throw new InvalidOperationException($"pod {spec.Name} already exists");
            var pod = new Pod { Spec = spec, Uid = Guid.NewGuid().ToString(), Ip = $"10.42.0.{++_ips}" };
            _pods[spec.Name] = pod;
            Created.Add(spec);
            r = new PodRef(spec.Name, pod.Uid);
            Calls.Add(("create", spec.Name, pod.Uid, null));
            if (spec.Secret is { } s) Secrets[s.Name] = (s, pod.Uid);
            Emit(PodEventType.Added, pod);
        }
        if (OnCreated is { } hook) await hook(spec, r);
        return r;
    }

    /// <summary>A pod that exists before the manager starts (adoption facts).</summary>
    public PodRef Seed(string name, IReadOnlyDictionary<string, string> labels, PodPhase phase = PodPhase.Running, bool ready = true, string? uid = null)
    {
        lock (_gate)
        {
            var spec = new PodSpec(name, labels, [], [], [], new Dictionary<string, string>(), 60, null);
            var pod = new Pod { Spec = spec, Uid = uid ?? Guid.NewGuid().ToString(), Phase = phase, Ready = ready, Ip = $"10.42.0.{++_ips}" };
            _pods[name] = pod;
            return new PodRef(name, pod.Uid);
        }
    }

    public Task<bool> Delete(string name, string uid, TimeSpan grace, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_pods.TryGetValue(name, out var pod) || pod.Uid != uid) return Task.FromResult(false);
            Calls.Add(("delete", name, uid, grace));
            if (pod.Terminating) return Task.FromResult(true);
            pod.Terminating = true;
            if (grace <= TimeSpan.Zero) Remove(pod);
            else pod.GraceTimer = clock.CreateTimer(_ => { lock (_gate) Remove(pod); }, null, grace, Timeout.InfiniteTimeSpan);
            return Task.FromResult(true);
        }
    }

    /// <summary>The process exited during its grace (SIGTERM honoured): the pod goes before the deadline.</summary>
    public void ExitNow(string name, int exitCode = 0)
    {
        lock (_gate)
        {
            if (!_pods.TryGetValue(name, out var pod)) return;
            pod.ExitCode = exitCode;
            Remove(pod);
        }
    }

    /// <summary>`kubectl delete pod` from outside the service.</summary>
    public void DeleteExternally(string name) { lock (_gate) if (_pods.TryGetValue(name, out var p)) Remove(p); }

    public void SetPhase(string name, PodPhase phase, int? exitCode = null)
    {
        lock (_gate)
        {
            var p = _pods[name];
            p.Phase = phase;
            if (exitCode is not null) p.ExitCode = exitCode;
            if (phase is PodPhase.Failed or PodPhase.Succeeded) p.Ready = false;
            Emit(PodEventType.Modified, p);
        }
    }

    public void SetReady(string name, bool ready = true)
    {
        lock (_gate)
        {
            var p = _pods[name];
            p.Ready = ready;
            if (ready) p.Phase = PodPhase.Running;
            Emit(PodEventType.Modified, p);
        }
    }

    public void Restart(string name) { lock (_gate) { var p = _pods[name]; p.Restarts++; Emit(PodEventType.Modified, p); } }

    public void AppendLog(string name, string text) { lock (_gate) _pods[name].Log += text; }

    void Remove(Pod pod)
    {
        if (!_pods.Remove(pod.Spec.Name)) return;
        pod.GraceTimer?.Dispose();
        foreach (var s in Secrets.Where(kv => kv.Value.OwnerUid == pod.Uid).Select(kv => kv.Key).ToList()) Secrets.Remove(s);
        if (pod.Phase is not (PodPhase.Failed or PodPhase.Succeeded))
            pod.Phase = pod.ExitCode is null or 0 ? PodPhase.Succeeded : PodPhase.Failed;
        pod.Ready = false;
        Emit(PodEventType.Deleted, pod);
    }

    void Emit(PodEventType type, Pod pod)
    {
        var e = new PodEvent(type, pod.Status());
        _pending.Add(e);
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
        try
        {
            await foreach (var e in ch.Reader.ReadAllAsync(ct)) yield return e;
        }
        finally { lock (_gate) _watchers.Remove(ch); }
    }

    public Task<string> Logs(string name, int tail, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_pods.TryGetValue(name, out var p)) throw new KeyNotFoundException(name);
            var lines = p.Log.Split('\n');
            return Task.FromResult(string.Join('\n', lines.TakeLast(tail)));
        }
    }

    public Task<IReadOnlyList<PodStatus>> List(string labelSelector, CancellationToken ct = default)
    {
        var want = labelSelector.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(kv => kv.Split('=', 2)).ToList();
        lock (_gate)
            return Task.FromResult<IReadOnlyList<PodStatus>>(_pods.Values
                .Where(p => want.All(kv => p.Spec.Labels.TryGetValue(kv[0], out var v) && v == kv[1]))
                .Select(p => p.Status()).ToList());
    }

    public Task<JobRef> RunJob(JobSpec spec, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_jobs.ContainsKey(spec.Name)) throw new InvalidOperationException($"job {spec.Name} already exists");
            var uid = Guid.NewGuid().ToString();
            _jobs[spec.Name] = (spec, uid, JobPhase.Pending);
            return Task.FromResult(new JobRef(spec.Name, uid));
        }
    }

    public IReadOnlyList<JobSpec> Jobs { get { lock (_gate) return _jobs.Values.Select(j => j.Spec).ToList(); } }

    public void SetJobPhase(string name, JobPhase phase, string? message = null)
    {
        lock (_gate)
        {
            var j = _jobs[name];
            _jobs[name] = (j.Spec, j.Uid, phase);
            foreach (var w in _jobWatchers) w.Writer.TryWrite(new JobEvent(name, phase, message));
        }
    }

    public async IAsyncEnumerable<JobEvent> WatchJob(string name, [EnumeratorCancellation] CancellationToken ct)
    {
        var ch = Channel.CreateUnbounded<JobEvent>();
        lock (_gate)
        {
            if (!_jobs.TryGetValue(name, out var j)) throw new KeyNotFoundException(name);
            ch.Writer.TryWrite(new JobEvent(name, j.Phase, null));
            _jobWatchers.Add(ch);
        }
        try
        {
            await foreach (var e in ch.Reader.ReadAllAsync(ct))
            {
                if (e.Name != name) continue;
                yield return e;
                if (e.Phase is JobPhase.Succeeded or JobPhase.Failed) yield break;
            }
        }
        finally { lock (_gate) _jobWatchers.Remove(ch); }
    }
}
