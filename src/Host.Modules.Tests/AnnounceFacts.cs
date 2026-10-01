using SourceSharp.Host.Contracts;

namespace SourceSharp.Host.Modules.Tests;

/// <summary>Plan §1.2 gate: hash first, bytes on request; the contract checked before anything else.</summary>
public class AnnounceFacts
{
    static readonly FixtureModule A = FixtureModule.AlwaysDrop;

    [Fact]
    public async Task A_new_hash_is_answered_send()
    {
        await using var h = new ModuleHarness();
        Assert.Equal(RulesModuleAnswerKind.Send, (await h.Registry.Announce(A.Announcement(), "pod-1")).Kind);
    }

    [Fact]
    public async Task Announce_of_an_uploaded_hash_answers_known_and_skips_the_upload()
    {
        await using var h = new ModuleHarness();
        var first = await h.Boot(A, "pod-1");
        Assert.Equal(RulesModuleAnswerKind.Known, first.Upload?.Kind); // the first pod did upload

        var second = await h.Boot(A, "pod-2");

        Assert.Equal(RulesModuleAnswerKind.Known, second.Announce.Kind);
        Assert.Null(second.Upload);
    }

    [Fact]
    public async Task A_second_pod_on_the_same_hash_uploads_nothing()
    {
        await using var h = new ModuleHarness();
        await h.Boot(A, "pod-1");
        var filesBefore = h.DiskFiles();

        await h.Boot(A, "pod-2");

        Assert.Equal(1, await h.AuditCount("module.accepted", A.Sha256));
        Assert.Equal(filesBefore, h.DiskFiles());
        Assert.Equal("pod-1", (await h.D.Read(tx => tx.Modules.Get(A.Sha256)))?.FirstInstance);
    }

    [Fact]
    public async Task A_wrong_contract_version_is_refused_and_audited()
    {
        await using var h = new ModuleHarness();
        var bumped = $"{int.Parse(HostContract.Version.Split('.')[0]) + 1}.0.0";
        // Control: the same module on the host's own contract is accepted.
        Assert.Equal(RulesModuleAnswerKind.Send, (await h.Registry.Announce(A.Announcement(), "pod-0")).Kind);

        var answer = await h.Registry.Announce(A.Announcement(contract: bumped), "pod-1");

        Assert.Equal(RulesModuleAnswerKind.Refused, answer.Kind);
        Assert.Equal("contract_unsupported", answer.Reason);
        Assert.Contains(bumped, answer.Message);
        Assert.Contains(HostContract.Version, answer.Message);
        var audit = Assert.Single(await h.Audits("module.refused"));
        Assert.Equal(A.Sha256, audit.Target);
        Assert.Contains("contract_unsupported", audit.AfterJson);
    }

    [Fact]
    public async Task The_contract_is_checked_before_quarantine()
    {
        await using var h = new ModuleHarness();
        await h.Boot(A, "pod-1");
        await h.Registry.Quarantine(A.Sha256, "admin");

        var answer = await h.Registry.Announce(A.Announcement(contract: "9.0.0"), "pod-2");

        Assert.Equal("contract_unsupported", answer.Reason);
    }

    [Fact]
    public async Task Announcing_host_contracts_as_a_dependency_is_refused()
    {
        await using var h = new ModuleHarness();
        var a = A.Announcement();
        var shipped = a with { Deps = [.. a.Deps, new ModuleDependencyInfo("SourceSharp.Host.Contracts", new string('a', 64))] };

        var answer = await h.Registry.Announce(shipped, "pod-1");

        Assert.Equal("bad_announcement", answer.Reason);
        Assert.Contains("SourceSharp.Host.Contracts", answer.Message);
    }

    [Fact]
    public async Task A_malformed_hash_is_refused()
    {
        await using var h = new ModuleHarness();

        var answer = await h.Registry.Announce(A.Announcement() with { Sha256 = "../../etc" }, "pod-1");

        Assert.Equal("bad_announcement", answer.Reason);
        Assert.Equal(1, await h.AuditCount("module.refused"));
    }

    [Fact]
    public async Task An_uppercase_hash_is_the_same_module()
    {
        await using var h = new ModuleHarness();
        await h.Boot(A, "pod-1");

        var answer = await h.Registry.Announce(A.Announcement() with { Sha256 = A.Sha256.ToUpperInvariant() }, "pod-2");

        Assert.Equal(RulesModuleAnswerKind.Known, answer.Kind);
    }

    [Fact]
    public async Task A_known_hash_announced_with_other_dependencies_is_refused()
    {
        await using var h = new ModuleHarness();
        await h.Boot(A, "pod-1");

        var answer = await h.Registry.Announce(A.Announcement() with { Deps = [] }, "pod-2");

        Assert.Equal("deps_mismatch", answer.Reason);
    }
}
