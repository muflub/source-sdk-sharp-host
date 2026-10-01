using SourceSharp.Host.Abstractions;

namespace SourceSharp.Host.Modules.Tests;

/// <summary>Plan §1.2 trust, §11 Modules page: approval, quarantine (drains, refuses uploads), delete when unreferenced.</summary>
public class AdminFacts
{
    static readonly FixtureModule A = FixtureModule.AlwaysDrop;
    static readonly FixtureModule B = FixtureModule.NeverDrop;

    [Fact]
    public async Task Require_approval_holds_a_new_hash_pending()
    {
        await using var h = new ModuleHarness(requireApproval: true);

        var boot = await h.Boot(A, "pod-1");
        var again = await h.Registry.Announce(A.Announcement(), "pod-2");

        Assert.Equal(RulesModuleAnswerKind.Pending, boot.Upload?.Kind);
        Assert.Equal(RulesModuleAnswerKind.Pending, again.Kind);
        Assert.Equal(ModuleState.Pending, (await h.D.Read(tx => tx.Modules.Get(A.Sha256)))?.State);
    }

    [Fact]
    public async Task Approve_makes_a_pending_hash_known_and_loadable()
    {
        await using var h = new ModuleHarness(requireApproval: true);
        await h.Boot(A, "pod-1");

        await h.Registry.Approve(A.Sha256, "admin");

        Assert.Equal(RulesModuleAnswerKind.Known, (await h.Registry.Announce(A.Announcement(), "pod-2")).Kind);
        Assert.True(h.Provider.For(A.Sha256).KillRoll(1, 1, "x", 1, 1).Drop);
        Assert.Equal(1, await h.AuditCount("module.approved", A.Sha256));
    }

    [Fact]
    public async Task Without_require_approval_a_new_hash_is_approved_at_once()
    {
        await using var h = new ModuleHarness(requireApproval: false);

        var boot = await h.Boot(A, "pod-1");

        Assert.Equal(RulesModuleAnswerKind.Known, boot.Upload?.Kind);
    }

    [Fact]
    public async Task Quarantine_returns_the_instances_to_drain()
    {
        await using var h = new ModuleHarness();
        await h.Boot(A, "a-1");
        await h.Boot(B, "b-1");
        await h.AddInstance("a-1", A.Sha256);
        await h.AddInstance("a-2", A.Sha256, InstanceState.Booting);
        await h.AddInstance("a-gone", A.Sha256, InstanceState.Reaped);
        await h.AddInstance("b-1", B.Sha256);

        var drain = await h.Registry.Quarantine(A.Sha256, "admin");

        Assert.Equal(["a-1", "a-2"], drain);
        var audit = Assert.Single(await h.Audits("module.quarantined"));
        Assert.Equal("admin", audit.Actor);
    }

    [Fact]
    public async Task Quarantine_refuses_new_announces_of_that_hash()
    {
        await using var h = new ModuleHarness();
        await h.Boot(A, "pod-1");

        await h.Registry.Quarantine(A.Sha256, "admin");
        var answer = await h.Registry.Announce(A.Announcement(), "pod-2");

        Assert.Equal(("quarantined", RulesModuleAnswerKind.Refused), (answer.Reason, answer.Kind));
        Assert.Equal(1, await h.AuditCount("module.refused", A.Sha256));
    }

    [Fact]
    public async Task Quarantine_refuses_uploads_of_that_hash()
    {
        await using var h = new ModuleHarness();
        await h.Boot(A, "pod-1");
        await h.Registry.Delete(A.Sha256, "admin"); // files gone: an announce would ask for the bytes again
        Assert.Equal(RulesModuleAnswerKind.Send, (await h.Registry.Announce(A.Announcement(), "pod-2")).Kind);

        await h.Registry.Quarantine(A.Sha256, "admin");

        var refusal = await Assert.ThrowsAsync<HostRefusal>(() => h.Registry.BeginUpload(A.Sha256, "pod-2"));
        Assert.Equal("quarantined", refusal.Reason);
        Assert.Empty(h.DiskFiles());
    }

    [Fact]
    public async Task Quarantine_refuses_the_module_to_the_provider_and_unloads_it()
    {
        await using var h = new ModuleHarness();
        await h.Boot(A, "pod-1");
        h.Provider.For(A.Sha256);
        Assert.NotEmpty(h.Loader.Loaded);

        await h.Registry.Quarantine(A.Sha256, "admin");

        Assert.Empty(h.Loader.Loaded);
        Assert.Equal("quarantined", Assert.Throws<HostRefusal>(() => h.Provider.For(A.Sha256)).Reason);
    }

    [Fact]
    public async Task Approve_lifts_a_quarantine()
    {
        await using var h = new ModuleHarness();
        await h.Boot(A, "pod-1");
        await h.Registry.Quarantine(A.Sha256, "admin");

        await h.Registry.Approve(A.Sha256, "admin");

        Assert.Equal(RulesModuleAnswerKind.Known, (await h.Registry.Announce(A.Announcement(), "pod-2")).Kind);
    }

    [Fact]
    public async Task Delete_is_refused_while_the_hash_is_referenced()
    {
        await using var h = new ModuleHarness();
        await h.Boot(A, "pod-1");
        h.Referenced.Add(A.Sha256);

        var refusal = await Assert.ThrowsAsync<HostRefusal>(() => h.Registry.Delete(A.Sha256, "admin"));

        Assert.Equal("module_in_use", refusal.Reason);
        Assert.NotEmpty(h.DiskFiles());
    }

    [Fact]
    public async Task Delete_removes_the_files_and_the_next_pod_uploads_again()
    {
        await using var h = new ModuleHarness();
        await h.Boot(A, "pod-1");

        await h.Registry.Delete(A.Sha256, "admin");

        Assert.Empty(h.DiskFiles());
        Assert.Equal(1, await h.AuditCount("module.deleted", A.Sha256));
        var boot = await h.Boot(A, "pod-2");
        Assert.Equal((RulesModuleAnswerKind.Send, RulesModuleAnswerKind.Known), (boot.Announce.Kind, boot.Upload?.Kind));
        Assert.NotEmpty(h.DiskFiles());
    }

    [Fact]
    public async Task List_shows_every_hash()
    {
        await using var h = new ModuleHarness();
        await h.Boot(A, "pod-1");
        await h.Boot(B, "pod-2");

        Assert.Equal(
            new[] { A.Sha256, B.Sha256 }.Order(StringComparer.Ordinal),
            (await h.Registry.List()).Select(r => r.Sha256).Order(StringComparer.Ordinal));
    }
}
