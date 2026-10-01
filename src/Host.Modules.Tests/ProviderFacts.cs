using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Contracts;

namespace SourceSharp.Host.Modules.Tests;

/// <summary>Plan §1.2: per instance, that pod's module; for the pool, the current mod image's.</summary>
public class ProviderFacts
{
    static readonly FixtureModule A = FixtureModule.AlwaysDrop;
    static readonly FixtureModule B = FixtureModule.NeverDrop;
    const string OtherImage = "registry.local/descent-mod:2";

    [Fact]
    public async Task An_unknown_hash_is_refused()
    {
        await using var h = new ModuleHarness();

        var refusal = Assert.Throws<HostRefusal>(() => h.Provider.For(A.Sha256));

        Assert.Equal("unknown_module", refusal.Reason);
    }

    [Fact]
    public async Task A_deleted_module_is_refused_as_missing()
    {
        await using var h = new ModuleHarness();
        await h.Boot(A, "pod-a");
        await h.Registry.Delete(A.Sha256, "admin");

        var refusal = Assert.Throws<HostRefusal>(() => h.Provider.For(A.Sha256));

        Assert.Equal("module_missing", refusal.Reason);
    }

    [Fact]
    public async Task Current_is_null_until_an_instance_boots_from_the_mod_image()
    {
        await using var h = new ModuleHarness();
        await h.Boot(A, "hub");
        Assert.Null(h.Provider.Current);

        h.Provider.NoteBooted("hub", ModuleHarness.ModImage, A.Sha256);

        Assert.Equal(A.Sha256, h.Provider.CurrentSha256);
        Assert.True(h.Provider.Current!.KillRoll(1, 1, "x", 1, 1).Drop);
    }

    [Fact]
    public async Task Current_ignores_instances_of_another_image()
    {
        await using var h = new ModuleHarness();
        await h.Boot(A, "hub");
        await h.Boot(B, "old");
        h.Provider.NoteBooted("hub", ModuleHarness.ModImage, A.Sha256);

        h.Provider.NoteBooted("old", OtherImage, B.Sha256);

        Assert.Equal(A.Sha256, h.Provider.CurrentSha256);
    }

    [Fact]
    public async Task Current_follows_the_configured_mod_image()
    {
        await using var h = new ModuleHarness();
        await h.Boot(A, "hub");
        await h.Boot(B, "hub-2");
        h.Provider.NoteBooted("hub", ModuleHarness.ModImage, A.Sha256);
        h.Provider.NoteBooted("hub-2", OtherImage, B.Sha256);

        h.CurrentModImage = OtherImage;

        Assert.Equal(B.Sha256, h.Provider.CurrentSha256);
        Assert.False(h.Provider.Current!.KillRoll(1, 1, "x", 1, 1).Drop);
    }

    [Fact]
    public async Task Current_is_the_last_instance_booted_from_the_image()
    {
        await using var h = new ModuleHarness();
        await h.Boot(A, "hub");
        await h.Boot(B, "hub-2");
        h.Provider.NoteBooted("hub", ModuleHarness.ModImage, A.Sha256);

        h.Provider.NoteBooted("hub-2", ModuleHarness.ModImage, B.Sha256);

        Assert.Equal(B.Sha256, h.Provider.CurrentSha256);
    }

    [Fact]
    public async Task A_quarantined_current_module_reads_as_none()
    {
        await using var h = new ModuleHarness();
        await h.Boot(A, "hub");
        h.Provider.NoteBooted("hub", ModuleHarness.ModImage, A.Sha256);
        Assert.NotNull(h.Provider.Current);

        await h.Registry.Quarantine(A.Sha256, "admin");

        Assert.Null(h.Provider.Current);
    }

    [Fact]
    public async Task Restore_relearns_current_from_the_instance_rows()
    {
        await using var h = new ModuleHarness();
        await h.Boot(A, "hub");
        await h.AddInstance("hub", A.Sha256);
        Assert.Null(h.Provider.CurrentSha256); // a fresh provider, as after a restart

        await h.Provider.RestoreAsync(h.D.Data);

        Assert.Equal(A.Sha256, h.Provider.CurrentSha256);
    }

    [Fact]
    public async Task A_module_awaiting_approval_is_not_loaded()
    {
        await using var h = new ModuleHarness(requireApproval: true);
        await h.Boot(A, "pod-a");

        var refusal = Assert.Throws<HostRefusal>(() => h.Provider.For(A.Sha256));

        Assert.Equal("module_pending", refusal.Reason);
        Assert.Empty(h.Loader.Loaded);
    }

    [Fact]
    public async Task AddRulesModules_makes_the_module_provider_the_IRulesProvider()
    {
        await using var h = new ModuleHarness();
        var services = new ServiceCollection();
        services.AddSingleton<IHostData>(h.D.Data);
        services.AddSingleton<TimeProvider>(h.D.Clock);
        services.AddOptions<ServiceOptions>().Configure(o => o.Modules.Path = h.Root);
        services.AddSingleton<IRulesProvider>(new SingleRulesProvider(new SourceSharp.Host.Testing.FakeGameRules()));
        services.AddRulesModules(_ => () => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>()));
        await using var sp = services.BuildServiceProvider();

        Assert.IsType<RulesProvider>(sp.GetRequiredService<IRulesProvider>());
        Assert.Same(sp.GetRequiredService<RulesModuleRegistry>(), sp.GetRequiredService<IRulesModuleRegistry>());
        Assert.Contains(sp.GetServices<IHostedService>(), s => s is RulesModuleSweeper);
    }
}
