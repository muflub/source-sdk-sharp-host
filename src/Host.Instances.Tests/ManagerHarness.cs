using Microsoft.Extensions.Options;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Instances;
using SourceSharp.Host.Testing;

namespace SourceSharp.Host.Instances.Tests;

/// <summary>
/// The manager over the real stores (SQLite :memory:), FakeInstanceHost, a fake clock and
/// recording commands / hooks that share one ordered log with the host's deletes.
/// Pod events are delivered by <see cref="Pump"/>, in order, so every fact is deterministic.
/// </summary>
sealed class ManagerHarness : IAsyncDisposable
{
    public TestData D { get; } = new();
    public FakeInstanceHost Pods { get; }
    public List<string> Log { get; } = [];
    public ServiceOptions Options { get; } = new();
    public RecordingCommands Commands { get; }
    public RecordingHooks Hooks { get; }
    public IModImageInspector? Inspector { get; set; }
    public InstanceManager M { get; private set; } = null!;

    public Microsoft.Extensions.Time.Testing.FakeTimeProvider Clock => D.Clock;

    public ManagerHarness(Action<ServiceOptions>? configure = null)
    {
        Pods = new FakeInstanceHost(D.Clock);
        Commands = new RecordingCommands(Log);
        Hooks = new RecordingHooks(Log);
        Options.Instances.VerifyModLabel = false;
        Options.Instances.Hubs = 0; // level facts see only their own pods; hub facts set Hubs = 1
        configure?.Invoke(Options);
        M = NewManager();
    }

    /// <summary>A fresh manager on the same rows and pods: a service restart.</summary>
    public InstanceManager NewManager() => M = new InstanceManager(D.Data, new LoggingHost(Pods, Log), Commands, Hooks,
        new StaticOptions(Options), D.Clock, Inspector is null ? null : new ModImageGuard(Inspector, D.Clock));

    public async Task Pump()
    {
        foreach (var e in Pods.TakePending()) await M.Apply(e);
    }

    public async Task Tick() { await M.Tick(); await Pump(); }

    public async Task Advance(TimeSpan by) { Clock.Advance(by); await Pump(); }

    public Task<InstanceRecord> Row(string id) => D.Read(tx => tx.Instances.Get(id)).ContinueWith(t => t.Result!);

    public async Task<InstanceRecord> Hub() =>
        (await D.Read(tx => tx.Instances.NonTerminal())).Single(r => r.Kind == InstanceKind.Hub && r.State != InstanceState.Draining);

    public Task<IReadOnlyList<AuditEntry>> Audits(string target) => D.Read(tx => tx.Audit.List(new AuditQuery(Target: target, Take: 500)));

    public async Task<IReadOnlyList<string>> Actions(string target) =>
        (await Audits(target)).OrderBy(a => a.Id).Select(a => a.Action).ToList();

    /// <summary>The token the pod got, read from its Secret (the only place it lives).</summary>
    public string Token(InstanceRecord row) => Pods.Secrets[GamePodBuilder.SecretName(row.PodName!)].Secret.Data["token"];

    public async Task<InstanceRecord> RequestLevel(int depth = 3, string hash = "h1")
    {
        var r = await M.RequestLevel(depth, "party-1", hash, 1);
        return Assert.IsType<LevelRequestResult.Accepted>(r).Instance;
    }

    /// <summary>Booting → MapReady on the expected port and pod Ready → Live.</summary>
    public async Task<InstanceRecord> BringLive(InstanceRecord row)
    {
        row = await Row(row.Id);
        await M.OnBooting(row.Id, "sha-rules", "sha256:mod");
        Pods.SetReady(row.PodName!);
        await Pump();
        await M.OnStreamOpened(row.Id);
        var ready = await M.OnMapReady(row.Id, 27015);
        Assert.Equal(MapReadyOutcome.Live, ready.Outcome);
        return await Row(row.Id);
    }

    public async Task<InstanceRecord> LiveLevel(int depth = 3, string hash = "h1")
    {
        var row = await RequestLevel(depth, hash);
        await Tick();
        return await BringLive(row);
    }

    public ValueTask DisposeAsync() => D.DisposeAsync();
}

sealed class StaticOptions(ServiceOptions value) : IOptionsMonitor<ServiceOptions>
{
    public ServiceOptions CurrentValue => value;
    public ServiceOptions Get(string? name) => value;
    public IDisposable? OnChange(Action<ServiceOptions, string?> listener) => null;
}

/// <summary>Writes the host's deletes into the shared log, so ordering against the hooks is observable.</summary>
sealed class LoggingHost(FakeInstanceHost inner, List<string> log) : IInstanceHost
{
    public Task<PodRef> Create(PodSpec spec, CancellationToken ct = default) { log.Add($"create {spec.Name}"); return inner.Create(spec, ct); }
    public IAsyncEnumerable<PodEvent> Watch(CancellationToken ct) => inner.Watch(ct);
    public Task<bool> Delete(string name, string uid, TimeSpan grace, CancellationToken ct = default)
    {
        log.Add($"delete {name} {uid} {grace.TotalSeconds:0}s");
        return inner.Delete(name, uid, grace, ct);
    }
    public Task<string> Logs(string name, int tail, CancellationToken ct = default) => inner.Logs(name, tail, ct);
    public Task<IReadOnlyList<PodStatus>> List(string labelSelector, CancellationToken ct = default) => inner.List(labelSelector, ct);
    public Task<JobRef> RunJob(JobSpec spec, CancellationToken ct = default) => inner.RunJob(spec, ct);
    public IAsyncEnumerable<JobEvent> WatchJob(string name, CancellationToken ct) => inner.WatchJob(name, ct);
}

sealed class RecordingCommands(List<string> log) : IInstanceCommands
{
    public List<(string Instance, InstanceCommand Command)> Sent { get; } = [];
    public bool Connected { get; set; } = true;
    public Task<bool> Send(string instanceId, InstanceCommand command, CancellationToken ct = default)
    {
        Sent.Add((instanceId, command));
        log.Add($"send {instanceId} {command.GetType().Name}");
        return Task.FromResult(Connected);
    }
}

sealed class RecordingHooks(List<string> log) : IInstanceHooks
{
    public HashSet<string> Corpses { get; } = [];
    public List<(string Hook, InstanceRecord Row)> Calls { get; } = [];
    Task Record(string hook, InstanceRecord row) { Calls.Add((hook, row)); log.Add($"{hook} {row.Id}"); return Task.CompletedTask; }
    public Task OnLive(InstanceRecord instance, CancellationToken ct) => Record("OnLive", instance);
    public Task OnReaping(InstanceRecord instance, CancellationToken ct) => Record("OnReaping", instance);
    /// <summary>The next OnReaped throws after recording: the service "dies" between the delete and the sweep.</summary>
    public bool FailNextReaped { get; set; }
    public async Task OnReaped(InstanceRecord instance, CancellationToken ct)
    {
        await Record("OnReaped", instance);
        if (FailNextReaped) { FailNextReaped = false; throw new InvalidOperationException("service died mid-reap"); }
    }
    public Task OnCrashed(InstanceRecord instance, CancellationToken ct) => Record("OnCrashed", instance);
    public Task<bool> HasCorpses(InstanceRecord instance, CancellationToken ct) => Task.FromResult(Corpses.Contains(instance.Id));
    public IEnumerable<string> For(string id) => Calls.Where(c => c.Row.Id == id).Select(c => c.Hook);
}
