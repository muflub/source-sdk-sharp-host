using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Contracts;
using SourceSharp.Host.Testing;

namespace SourceSharp.Host.Modules.Tests;

/// <summary>A fixture rules module as the SDK would announce and stream it: built by the test build, never referenced.</summary>
public sealed record FixtureModule(string Assembly, IReadOnlyList<(string Assembly, byte[] Bytes)> Deps, byte[] Main)
{
    public static string Dir(string project) => Path.Combine(AppContext.BaseDirectory, "module-fixtures", project);

    static byte[] Read(string project, string assembly)
    {
        var path = Path.Combine(Dir(project), assembly + ".dll");
        // A missing fixture is a failure, never a pass.
        Assert.True(File.Exists(path), $"fixture {path} was not built next to the tests");
        return File.ReadAllBytes(path);
    }

    /// <summary>Every kill drops tier 0 with 7 × depth Australium, from its private dependency ModuleFixture.Dep.</summary>
    public static FixtureModule AlwaysDrop { get; } = new("ModuleFixture.AlwaysDrop",
        [("ModuleFixture.Dep", Read("ModuleFixture.AlwaysDrop", "ModuleFixture.Dep"))], Read("ModuleFixture.AlwaysDrop", "ModuleFixture.AlwaysDrop"));

    /// <summary>No kill ever drops.</summary>
    public static FixtureModule NeverDrop { get; } = new("ModuleFixture.NeverDrop", [], Read("ModuleFixture.NeverDrop", "ModuleFixture.NeverDrop"));

    public static string Hash(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));
    public string Sha256 => Hash(Main);

    public ModuleAnnouncement Announcement(string? contract = null) =>
        new(Assembly, "1.0.0", Sha256, contract ?? HostContract.Version, Deps.Select(d => new ModuleDependencyInfo(d.Assembly, Hash(d.Bytes))).ToList());

    /// <summary>(sha, file name, bytes) per file, main first, as UploadModule streams them.</summary>
    public IEnumerable<(string Sha, string File, byte[] Bytes)> Files() =>
        [(Sha256, Assembly + ".dll", Main), .. Deps.Select(d => (Hash(d.Bytes), d.Assembly + ".dll", d.Bytes))];
}

/// <summary>The registry, loader and provider over the real stores on :memory:, a temp module root, and a switchable reference set.</summary>
public sealed class ModuleHarness : IAsyncDisposable
{
    public const string ModImage = "registry.local/descent-mod:1";

    public TestData D { get; } = new();
    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"d2-modules-{Guid.NewGuid():N}");
    public ModuleOptions Options { get; } = new();
    public RulesModuleLoader Loader { get; } = new();
    public RulesModuleRegistry Registry { get; }
    public RulesProvider Provider { get; }
    public HashSet<string> Referenced { get; } = [];
    public string CurrentModImage { get; set; } = ModImage;

    public ModuleHarness(bool requireApproval = false)
    {
        Options.Path = Root;
        Options.RequireApproval = requireApproval;
        Func<Task<IReadOnlySet<string>>> refs = () => Task.FromResult<IReadOnlySet<string>>(Referenced.ToHashSet());
        Registry = new RulesModuleRegistry(D.Data, () => Options, Loader, refs);
        Provider = new RulesProvider(Registry, () => CurrentModImage, refs);
    }

    /// <summary>Booting, then (on Send) the whole upload in chunks of <paramref name="chunk"/> bytes. Returns the announce answer and, if uploaded, the upload's.</summary>
    public async Task<(RulesModuleAnswer Announce, RulesModuleAnswer? Upload)> Boot(FixtureModule m, string instance, int chunk = 4096)
    {
        var a = await Registry.Announce(m.Announcement(), instance);
        if (a.Kind != RulesModuleAnswerKind.Send) return (a, null);
        return (a, await Upload(m, instance, chunk));
    }

    public async Task<RulesModuleAnswer> Upload(FixtureModule m, string instance, int chunk = 4096, Func<string, byte[], byte[]>? tamper = null)
    {
        await using var up = await Registry.BeginUpload(m.Sha256, instance);
        foreach (var (sha, file, original) in m.Files())
        {
            var bytes = tamper?.Invoke(file, original) ?? original;
            for (var at = 0; at < bytes.Length; at += chunk)
            {
                var n = Math.Min(chunk, bytes.Length - at);
                await up.Write(sha, file, bytes.AsMemory(at, n), last: at + n == bytes.Length);
            }
        }
        return await up.Complete();
    }

    public Task<int> AuditCount(string action, string? target = null) =>
        D.Read(tx => tx.Audit.Count(new AuditQuery(Action: action, Target: target)));

    public Task<IReadOnlyList<AuditEntry>> Audits(string action) =>
        D.Read(tx => tx.Audit.List(new AuditQuery(Action: action)));

    public Task<InstanceRecord> AddInstance(string id, string? sha, InstanceState state = InstanceState.Live) =>
        D.Write(tx => tx.Instances.Add(new InstanceRecord(id, InstanceKind.Level, state, 1, null, null, $"descent-{id}", $"uid-{id}",
            "10.42.0.9", 27015, "hash", ModImage, null, sha, Seed: 1, D.Clock.GetUtcNow(), D.Clock.GetUtcNow(), null, null, null, null, null, null, 0, 27015)));

    /// <summary>Every file under the module root, relative, sorted: what an upload left on disk.</summary>
    public IReadOnlyList<string> DiskFiles() =>
        Directory.Exists(Root)
            ? Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(Root, f)).Order(StringComparer.Ordinal).ToList()
            : [];

    /// <summary>Loads through the provider and returns only a weak handle on the context, so no strong reference survives this frame.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public WeakReference LoadWeak(string sha, UnloadWitness? witness = null)
    {
        var rules = Provider.For(sha);
        Assert.NotNull(rules);
        var weak = Loader.ContextOf(sha) ?? throw new InvalidOperationException("not loaded");
        if (witness is not null && weak.Target is System.Runtime.Loader.AssemblyLoadContext alc)
            alc.Unloading += _ => witness.Unloaded = true;
        return weak;
    }

    /// <summary>Set by the context's Unloading event: proves Unload() was called, not merely that the object went away.</summary>
    public sealed class UnloadWitness { public bool Unloaded { get; set; } }

    public static bool Collected(WeakReference w)
    {
        for (var i = 0; i < 20 && w.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        return !w.IsAlive;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var sha in Loader.Loaded) Loader.Unload(sha);
        await D.DisposeAsync();
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}
