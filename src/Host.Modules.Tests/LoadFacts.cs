using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Contracts;

namespace SourceSharp.Host.Modules.Tests;

/// <summary>Plan §1.2: one collectible load context per hash; Host.Contracts shared; unload when unreferenced.</summary>
public class LoadFacts
{
    static readonly FixtureModule A = FixtureModule.AlwaysDrop;
    static readonly FixtureModule B = FixtureModule.NeverDrop;

    [Fact]
    public void The_fixtures_are_not_in_the_tests_own_context()
    {
        Assert.DoesNotContain(AssemblyLoadContext.Default.Assemblies, x => x.GetName().Name!.StartsWith("ModuleFixture", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Two_instances_on_different_hashes_replay_with_different_modules()
    {
        await using var h = new ModuleHarness();
        await h.Boot(A, "pod-a");
        await h.Boot(B, "pod-b");
        await h.AddInstance("pod-a", A.Sha256);
        await h.AddInstance("pod-b", B.Sha256);
        var shaOf = await h.D.Read(async tx => ((await tx.Instances.Get("pod-a"))!.RulesSha256!, (await tx.Instances.Get("pod-b"))!.RulesSha256!));

        var rollA = h.Provider.For(shaOf.Item1).KillRoll(1, 1, "scout_bot", 2, 1);
        var rollB = h.Provider.For(shaOf.Item2).KillRoll(1, 1, "scout_bot", 2, 1);

        Assert.Equal(new KillRoll(true, 0, 14), rollA);
        Assert.Equal(new KillRoll(false, -1, 0), rollB);
    }

    [Fact]
    public async Task Each_module_gets_its_own_collectible_context()
    {
        await using var h = new ModuleHarness();
        await h.Boot(A, "pod-a");
        await h.Boot(B, "pod-b");

        var ca = AssemblyLoadContext.GetLoadContext(h.Provider.For(A.Sha256).GetType().Assembly)!;
        var cb = AssemblyLoadContext.GetLoadContext(h.Provider.For(B.Sha256).GetType().Assembly)!;

        Assert.NotSame(ca, cb);
        Assert.NotSame(AssemblyLoadContext.Default, ca);
        Assert.True(ca.IsCollectible);
    }

    [Fact]
    public async Task The_entry_type_is_instantiated_once_per_context()
    {
        await using var h = new ModuleHarness();
        await h.Boot(A, "pod-a");

        Assert.Same(h.Provider.For(A.Sha256), h.Provider.For(A.Sha256));
    }

    [Fact]
    public async Task The_module_binds_the_hosts_Host_Contracts()
    {
        await using var h = new ModuleHarness();
        await h.Boot(A, "pod-a");

        var rules = h.Provider.For(A.Sha256);

        Assert.Same(typeof(IGameRules), rules.GetType().GetInterface(nameof(IGameRules)));
        Assert.Same(typeof(IGameRules).Assembly, rules.GetType().BaseType!.GetInterface(nameof(IGameRules))!.Assembly);
    }

    [Fact]
    public async Task A_copy_of_Host_Contracts_in_the_module_folder_does_not_split_the_type()
    {
        await using var h = new ModuleHarness();
        await h.Boot(A, "pod-a");
        // The stimulus: a second Host.Contracts where the module's own dependencies resolve from.
        var copy = Path.Combine(h.Root, A.Sha256, "SourceSharp.Host.Contracts.dll");
        File.Copy(typeof(IGameRules).Assembly.Location, copy);
        Assert.True(File.Exists(copy));

        var rules = h.Provider.For(A.Sha256);

        Assert.Same(typeof(IGameRules), rules.GetType().GetInterface(nameof(IGameRules)));
        Assert.Equal(new KillRoll(true, 0, 7), rules.KillRoll(1, 1, "x", 1, 1));
    }

    [Fact]
    public async Task A_private_dependency_resolves_from_the_module_folder()
    {
        await using var h = new ModuleHarness();
        await h.Boot(A, "pod-a");

        var rules = h.Provider.For(A.Sha256);
        var roll = rules.KillRoll(1, 1, "x", 3, 1); // Australium = DropBonus.For(3), from ModuleFixture.Dep

        Assert.Equal(21, roll.Australium);
        var context = AssemblyLoadContext.GetLoadContext(rules.GetType().Assembly)!;
        Assert.Contains(context.Assemblies, x => x.GetName().Name == "ModuleFixture.Dep");
    }

    [Fact]
    public async Task A_module_unreferenced_at_the_sweep_is_unloaded_and_collected()
    {
        await using var h = new ModuleHarness();
        await h.Boot(A, "pod-a");
        var witness = new ModuleHarness.UnloadWitness();
        var weak = h.LoadWeak(A.Sha256, witness);
        Assert.True(weak.IsAlive);
        Assert.False(witness.Unloaded);

        var gone = await h.Provider.SweepAsync();

        Assert.Equal([A.Sha256], gone);
        Assert.True(witness.Unloaded, "the context's Unload was never called");
        Assert.True(ModuleHarness.Collected(weak), "the unloaded context was not collected");
    }

    [Fact]
    public async Task A_referenced_module_stays_loaded_across_the_sweep()
    {
        await using var h = new ModuleHarness();
        await h.Boot(A, "pod-a");
        var weak = h.LoadWeak(A.Sha256);
        h.Referenced.Add(A.Sha256.ToUpperInvariant()); // the lead's set may not be normalised

        var gone = await h.Provider.SweepAsync();

        Assert.Empty(gone);
        Assert.Equal([A.Sha256], h.Loader.Loaded);
        Assert.False(ModuleHarness.Collected(weak));
    }

    [Fact]
    public async Task The_current_mod_images_module_stays_loaded_across_the_sweep()
    {
        await using var h = new ModuleHarness();
        await h.Boot(A, "hub");
        h.Provider.NoteBooted("hub", ModuleHarness.ModImage, A.Sha256);
        Assert.NotNull(h.Provider.Current);

        Assert.Empty(await h.Provider.SweepAsync());
        Assert.Equal([A.Sha256], h.Loader.Loaded);
    }

    [Fact]
    public async Task An_unloaded_module_reloads_on_demand()
    {
        await using var h = new ModuleHarness();
        await h.Boot(A, "pod-a");
        Unload(h);

        Assert.Equal(new KillRoll(true, 0, 7), h.Provider.For(A.Sha256).KillRoll(1, 1, "x", 1, 1));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Unload(ModuleHarness h)
    {
        h.Provider.For(A.Sha256);
        Assert.True(h.Loader.Unload(A.Sha256));
    }

    [Fact]
    public async Task A_module_altered_on_disk_is_refused_at_load()
    {
        await using var h = new ModuleHarness();
        await h.Boot(A, "pod-a");
        var dep = Path.Combine(h.Root, A.Sha256, "ModuleFixture.Dep.dll");
        var bytes = File.ReadAllBytes(dep);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(dep, bytes);

        var refusal = Assert.Throws<HostRefusal>(() => h.Provider.For(A.Sha256));

        Assert.Equal("module_corrupt", refusal.Reason);
    }
}
