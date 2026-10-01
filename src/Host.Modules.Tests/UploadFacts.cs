using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Contracts;

namespace SourceSharp.Host.Modules.Tests;

/// <summary>Plan §1.2: the upload must match the hash it announced; content-addressed, atomic, recorded.</summary>
public class UploadFacts
{
    static readonly FixtureModule A = FixtureModule.AlwaysDrop;
    static readonly FixtureModule B = FixtureModule.NeverDrop;

    static byte[] Flip(byte[] b)
    {
        var c = (byte[])b.Clone();
        c[c.Length / 2] ^= 0xFF;
        return c;
    }

    [Fact]
    public async Task A_mismatched_hash_is_refused_audited_and_leaves_nothing_on_disk()
    {
        await using var h = new ModuleHarness();
        await h.Registry.Announce(A.Announcement(), "pod-1");

        var refusal = await Assert.ThrowsAsync<HostRefusal>(() =>
            h.Upload(A, "pod-1", tamper: (file, bytes) => file == "ModuleFixture.AlwaysDrop.dll" ? Flip(bytes) : bytes));

        Assert.Equal("hash_mismatch", refusal.Reason);
        Assert.Equal(1, await h.AuditCount("module.refused", A.Sha256));
        Assert.Empty(h.DiskFiles());
        Assert.Null(await h.D.Read(tx => tx.Modules.Get(A.Sha256)));
        // Control: the untampered bytes through the same path are accepted.
        Assert.Equal(RulesModuleAnswerKind.Known, (await h.Upload(A, "pod-1")).Kind);
        Assert.NotEmpty(h.DiskFiles());
    }

    [Fact]
    public async Task A_mismatched_dependency_hash_is_refused()
    {
        await using var h = new ModuleHarness();
        await h.Registry.Announce(A.Announcement(), "pod-1");

        var refusal = await Assert.ThrowsAsync<HostRefusal>(() =>
            h.Upload(A, "pod-1", tamper: (file, bytes) => file == "ModuleFixture.Dep.dll" ? Flip(bytes) : bytes));

        Assert.Equal("hash_mismatch", refusal.Reason);
        Assert.Empty(h.DiskFiles());
    }

    [Fact]
    public async Task An_upload_that_was_not_announced_is_refused()
    {
        await using var h = new ModuleHarness();

        var refusal = await Assert.ThrowsAsync<HostRefusal>(() => h.Registry.BeginUpload(A.Sha256, "pod-1"));

        Assert.Equal("not_announced", refusal.Reason);
        Assert.Equal(1, await h.AuditCount("module.refused", A.Sha256));
    }

    [Fact]
    public async Task A_second_pod_told_to_send_uploads_after_the_first_committed()
    {
        await using var h = new ModuleHarness();
        Assert.Equal(RulesModuleAnswerKind.Send, (await h.Registry.Announce(A.Announcement(), "pod-1")).Kind);
        Assert.Equal(RulesModuleAnswerKind.Send, (await h.Registry.Announce(A.Announcement(), "pod-2")).Kind);
        Assert.Equal(RulesModuleAnswerKind.Known, (await h.Upload(A, "pod-1")).Kind);

        Assert.Equal(RulesModuleAnswerKind.Known, (await h.Upload(A, "pod-2")).Kind);
    }

    [Fact]
    public async Task A_file_the_announcement_did_not_declare_is_refused()
    {
        await using var h = new ModuleHarness();
        await h.Registry.Announce(A.Announcement(), "pod-1");
        await using var up = await h.Registry.BeginUpload(A.Sha256, "pod-1");

        var refusal = await Assert.ThrowsAsync<HostRefusal>(() =>
            up.Write(B.Sha256, "ModuleFixture.NeverDrop.dll", B.Main, last: true));

        Assert.Equal("undeclared_file", refusal.Reason);
        Assert.Empty(h.DiskFiles());
    }

    [Fact]
    public async Task A_file_under_another_name_is_refused()
    {
        await using var h = new ModuleHarness();
        await h.Registry.Announce(A.Announcement(), "pod-1");
        await using var up = await h.Registry.BeginUpload(A.Sha256, "pod-1");

        var refusal = await Assert.ThrowsAsync<HostRefusal>(() => up.Write(A.Sha256, "../evil.dll", A.Main, last: true));

        Assert.Equal("file_name_mismatch", refusal.Reason);
    }

    [Fact]
    public async Task Main_bytes_of_another_assembly_are_refused()
    {
        await using var h = new ModuleHarness();
        // NeverDrop's bytes announced under another assembly name: the hash matches, the name does not.
        var liar = new FixtureModule("ModuleFixture.Other", [], B.Main);
        await h.Registry.Announce(liar.Announcement(), "pod-1");

        var refusal = await Assert.ThrowsAsync<HostRefusal>(() => h.Upload(liar, "pod-1"));

        Assert.Equal("assembly_mismatch", refusal.Reason);
        Assert.Empty(h.DiskFiles());
    }

    [Fact]
    public async Task An_upload_missing_a_declared_file_is_refused()
    {
        await using var h = new ModuleHarness();
        await h.Registry.Announce(A.Announcement(), "pod-1");
        await using var up = await h.Registry.BeginUpload(A.Sha256, "pod-1");
        await up.Write(A.Sha256, "ModuleFixture.AlwaysDrop.dll", A.Main, last: true);

        var refusal = await Assert.ThrowsAsync<HostRefusal>(() => up.Complete());

        Assert.Equal("incomplete", refusal.Reason);
        Assert.Empty(h.DiskFiles());
    }

    [Fact]
    public async Task An_oversized_upload_is_refused()
    {
        await using var h = new ModuleHarness();
        var small = new RulesModuleRegistry(h.D.Data, () => h.Options, h.Loader, () => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>()))
        { MaxUploadBytes = 1024 };
        await small.Announce(B.Announcement(), "pod-1");
        await using var up = await small.BeginUpload(B.Sha256, "pod-1");

        var refusal = await Assert.ThrowsAsync<HostRefusal>(() => up.Write(B.Sha256, "ModuleFixture.NeverDrop.dll", B.Main, last: true));

        Assert.Equal("too_large", refusal.Reason);
    }

    [Fact]
    public async Task An_upload_lands_content_addressed_under_its_hash()
    {
        await using var h = new ModuleHarness();

        await h.Boot(A, "pod-1");

        Assert.Equal(
            [Path.Combine(A.Sha256, "ModuleFixture.AlwaysDrop.dll"), Path.Combine(A.Sha256, "ModuleFixture.Dep.dll")],
            h.DiskFiles());
        Assert.Equal(A.Main, File.ReadAllBytes(Path.Combine(h.Root, A.Sha256, "ModuleFixture.AlwaysDrop.dll")));
    }

    [Fact]
    public async Task An_upload_records_the_module_row()
    {
        await using var h = new ModuleHarness();

        await h.Boot(A, "pod-1");

        var row = await h.D.Read(tx => tx.Modules.Get(A.Sha256));
        Assert.NotNull(row);
        Assert.Equal(("ModuleFixture.AlwaysDrop", HostContract.Version, "pod-1", ModuleState.Approved),
            (row.Assembly, row.ContractVersion, row.FirstInstance, row.State));
        Assert.Equal(A.Main.Length + A.Deps.Sum(d => d.Bytes.Length), row.Bytes);
        Assert.Contains("ModuleFixture.Dep", row.DepsJson);
    }

    [Fact]
    public async Task An_accepted_upload_is_audited()
    {
        await using var h = new ModuleHarness();

        await h.Boot(A, "pod-1");

        var audit = Assert.Single(await h.Audits("module.accepted"));
        Assert.Equal(A.Sha256, audit.Target);
        Assert.Contains("pod-1", audit.AfterJson);
    }

    [Fact]
    public async Task An_abandoned_upload_leaves_nothing_behind()
    {
        await using var h = new ModuleHarness();
        await h.Registry.Announce(A.Announcement(), "pod-1");
        await using (var up = await h.Registry.BeginUpload(A.Sha256, "pod-1"))
        {
            await up.Write(A.Sha256, "ModuleFixture.AlwaysDrop.dll", A.Main.AsMemory(0, 100), last: false);
            Assert.NotEmpty(h.DiskFiles()); // the stimulus reached the disk
        }

        Assert.Empty(h.DiskFiles());
        Assert.Null(await h.D.Read(tx => tx.Modules.Get(A.Sha256)));
    }

    [Fact]
    public async Task Interrupted_uploads_are_cleaned_at_start_and_modules_kept()
    {
        await using var h = new ModuleHarness();
        await h.Boot(A, "pod-1");
        var modules = h.DiskFiles();
        foreach (var stale in new[] { ".upload-dead", ".delete-dead" })
        {
            Directory.CreateDirectory(Path.Combine(h.Root, stale));
            File.WriteAllBytes(Path.Combine(h.Root, stale, "x.dll"), [1]);
        }
        Assert.Equal(modules.Count + 2, h.DiskFiles().Count);

        Assert.Equal(2, h.Registry.CleanStaleUploads());
        Assert.Equal(modules, h.DiskFiles());
    }
}
