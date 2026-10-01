using System.Collections.Concurrent;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Contracts;
using SourceSharp.Host.MapPool;
using SourceSharp.Host.Testing;

namespace SourceSharp.Host.MapPool.Tests;

/// <summary>An <see cref="IRulesProvider"/> a fact can switch on: null until the hub's module is "known".</summary>
public sealed class SwitchableRules : IRulesProvider
{
    public IGameRules? Current { get; set; } = new FakeGameRules();
    public string? CurrentSha256 => Current is null ? null : "fake";
    public IGameRules For(string sha256) => Current ?? throw new InvalidOperationException("no module");
}

public sealed class Demand : IPoolDemand
{
    public HashSet<int> Parties { get; } = [];
    public HashSet<int> Selectable { get; } = [];
    public IReadOnlyCollection<int> PartyDepths() => Parties;
    public IReadOnlyCollection<int> SelectableDepths() => Selectable;
}

/// <summary>Records bakes into the same event list as links, so a fact can see which ran first.</summary>
public sealed class FakeBaker(ConcurrentQueue<string> events) : IBakeLauncher
{
    public SemaphoreSlim? Gate { get; set; }
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task<string> BakeAsync(string mod, string library, CancellationToken ct)
    {
        events.Enqueue($"bake:{library}");
        Started.TrySetResult();
        if (Gate is { } g) await g.WaitAsync(ct);
        return "fake bake";
    }
}

/// <summary>The pool on the real stores (SQLite :memory:), the fake compiler, the fake validator and a fake clock.</summary>
public sealed class PoolHarness : IAsyncDisposable
{
    public const string Mod = "descent", Lib = "crypt";
    public TestData Db { get; } = new();
    public ServiceOptions Options { get; }
    public FakeMapCompiler Compiler { get; } = new();
    public SwitchableRules Rules { get; } = new();
    public Demand Demand { get; } = new();
    public ConcurrentQueue<string> Events { get; } = new();
    public FakeBaker Baker { get; }
    public PackCatalog Catalog { get; }
    public MapPoolWorker Worker { get; }
    public LevelStorage Storage { get; }
    public MapPoolLayoutOptions Layout { get; } = new();

    public PoolHarness(int depths = 3, int k = 2, int concurrent = 2)
    {
        var maps = Path.Combine(Path.GetTempPath(), "lane-m-pool-" + Guid.NewGuid().ToString("N"));
        Options = new ServiceOptions
        {
            MapPool = { Depths = depths, DefaultPerDepth = k, ConcurrentLinks = concurrent, MapsPath = maps, LibraryPath = Path.Combine(maps, "library") },
        };
        Storage = new LevelStorage(Options.MapPool);
        var signal = new PoolSignal();
        Catalog = new PackCatalog(Db.Data, new FakePackValidator(Mod), Storage, Options, signal);
        Baker = new FakeBaker(Events);
        Compiler.FailWhen = r => { Events.Enqueue($"link:{r.Depth}:{Path.GetFileName(Path.GetDirectoryName(r.PackPath))}/{Path.GetFileName(r.PackPath)}"); return FailWhen(r); };
        Worker = new MapPoolWorker(Db.Data, Rules, Demand, Catalog, new LinkDriver(Compiler, Storage, Options.Mod, Options.MapPool, Layout),
            Options, Layout, Db.Clock, signal, Baker);
    }

    public Func<LinkRequest, bool> FailWhen { get; set; } = _ => false;

    public async Task<PackRecord> Upload(string library = Lib)
    {
        using var s = new MemoryStream(FakePack.Bytes(library, ["start", "hall", "stairs"]));
        var r = await Catalog.UploadAsync(Mod, library, s, "{}", null, PackCatalog.SourceUpload, "admin@localhost");
        Assert.True(r.Accepted);
        return r.Pack;
    }

    public async Task<PackRecord> UploadAndActivate(params int[] depths)
    {
        var p = await Upload();
        await Catalog.ActivateAsync(Mod, depths.Length > 0 ? depths : [.. Enumerable.Range(1, Options.MapPool.Depths)], p.Id, "admin");
        return p;
    }

    public async Task Fill()
    {
        await Worker.PlanAsync();
        await Worker.RunPendingAsync();
    }

    public Task<int> Ready(int depth, long? packId = null) => Db.Read(tx => tx.Levels.Count(depth, packId, LevelState.Ready));

    public Task<IReadOnlyList<MapJob>> Jobs(MapJobState? state = null) => Db.Read(tx => tx.MapJobs.List(state, 1000));

    public ValueTask DisposeAsync() => Db.DisposeAsync();
}
