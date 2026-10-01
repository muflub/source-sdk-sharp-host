using System.Text.Json.Nodes;
using Descent.MapForge;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.MapPool;
using SourceSharp.Host.Testing;

namespace Descent.MapForge.Tests;

/// <summary>
/// §9.4 with the real validator: a corrupt, a wrong-mod, a wrong-library and a lint-failing
/// pack are each rejected with the file gone, each after the valid pack is accepted.
/// </summary>
public sealed class PackValidationFacts : IAsyncLifetime
{
    readonly TestData _db = new();
    PackCatalog _catalog = null!;
    string _maps = null!;

    public Task InitializeAsync()
    {
        _maps = Samples.TempDir();
        var options = new ServiceOptions { MapPool = { MapsPath = _maps } };
        _catalog = new PackCatalog(_db.Data, new PackValidator(Samples.Mod, 15, new LinkSettings { Transitions = false }),
            new LevelStorage(options.MapPool), options);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    async Task<PackUploadResult> Upload(string? pack = null, string mod = Samples.Mod, string library = Samples.Library, string? json = Samples.ManifestJson)
    {
        await using var s = File.OpenRead(pack ?? await Samples.PackCopyAsync());
        return await _catalog.UploadAsync(mod, library, s, json, "fact", PackCatalog.SourceUpload, "admin@localhost");
    }

    IEnumerable<string> FilesUnder(string mod) =>
        Directory.Exists(Path.Combine(_maps, "packs", mod)) ? Directory.GetFiles(Path.Combine(_maps, "packs", mod), "*", SearchOption.AllDirectories) : [];

    [Fact]
    public async Task The_baked_sample_pack_is_accepted_as_version_1()
    {
        var r = await Upload();
        Assert.True(r.Accepted, string.Join("; ", r.Inspection.Problems));
        Assert.Equal(1, r.Pack.Version);
        Assert.True(File.Exists(_catalog.PathOf(r.Pack)));
    }

    [Fact]
    public async Task An_accepted_pack_records_its_sha256_and_lint_report()
    {
        var path = await Samples.PackCopyAsync();
        var r = await Upload(path);
        await using var f = File.OpenRead(path);
        Assert.Equal(MapIdentity.Sha256(f), r.Pack.Sha256);
        Assert.True(JsonNode.Parse(r.Pack.LintJson!)!["ok"]!.GetValue<bool>());
        Assert.True(r.Inspection.TrialLink > TimeSpan.Zero);
    }

    [Fact]
    public async Task An_accepted_pack_writes_an_audit_row()
    {
        await Upload();
        Assert.Equal(1, await _db.Read(tx => tx.Audit.Count(new AuditQuery(Action: "pack.added"))));
    }

    [Fact]
    public async Task A_corrupt_pack_is_rejected_with_its_file_gone()
    {
        Assert.True((await Upload()).Accepted);
        var junk = Path.Combine(Samples.TempDir(), "junk.roompack");
        await File.WriteAllBytesAsync(junk, [.. Enumerable.Range(0, 4096).Select(i => (byte)(i * 7))]);
        var r = await Upload(junk);
        Assert.Equal(PackState.Rejected, r.Pack.State);
        Assert.Contains(r.Inspection.Problems, p => p.StartsWith(PackValidator.Corrupt));
        Assert.Single(FilesUnder(Samples.Mod), f => f.EndsWith(".roompack"));
        Assert.DoesNotContain(FilesUnder(Samples.Mod), f => f.EndsWith(".uploading"));
    }

    [Fact]
    public async Task A_pack_for_another_mod_is_rejected_with_its_file_gone()
    {
        Assert.True((await Upload()).Accepted);
        var r = await Upload(mod: "othermod");
        Assert.Equal(PackState.Rejected, r.Pack.State);
        Assert.Contains(r.Inspection.Problems, p => p.StartsWith(PackValidator.WrongMod));
        Assert.Empty(FilesUnder("othermod"));
    }

    [Fact]
    public async Task A_pack_whose_namespace_is_not_the_library_is_rejected_with_its_file_gone()
    {
        Assert.True((await Upload()).Accepted);
        var r = await Upload(library: "caves");
        Assert.Equal(PackState.Rejected, r.Pack.State);
        Assert.Contains(r.Inspection.Problems, p => p.StartsWith(PackValidator.WrongLibrary));
        Assert.Empty(Directory.GetFiles(Path.Combine(_maps, "packs", Samples.Mod, "caves")));
    }

    [Fact]
    public async Task A_pack_that_fails_lint_is_rejected_with_its_file_gone()
    {
        Assert.True((await Upload()).Accepted);
        var r = await Upload(json: Samples.ManifestJson.Replace("\"corridor\"", "\"lava\""));
        Assert.Equal(PackState.Rejected, r.Pack.State);
        Assert.Contains(r.Inspection.Problems, p => p.StartsWith(PackValidator.Lint) && p.Contains("lava"));
        Assert.Single(FilesUnder(Samples.Mod), f => f.EndsWith(".roompack"));
    }

    [Fact]
    public async Task A_rejected_pack_keeps_its_report()
    {
        var r = await Upload(mod: "othermod");
        var row = await _db.Read(tx => tx.Library.GetPack(r.Pack.Id));
        Assert.Contains(PackValidator.WrongMod, row!.LintJson);
    }

    [Fact]
    public async Task A_pack_without_library_json_and_no_previous_version_is_rejected()
    {
        Assert.True((await Upload()).Accepted);
        var fresh = new PackCatalog(_db.Data, new PackValidator(Samples.Mod, 15, new LinkSettings { Transitions = false }),
            new LevelStorage(new MapPoolOptions { MapsPath = Samples.TempDir() }), new ServiceOptions());
        await using var s = File.OpenRead(await Samples.PackCopyAsync());
        var r = await fresh.UploadAsync(Samples.Mod, "crypt2", s, null, null, PackCatalog.SourceUpload, "t");
        Assert.False(r.Accepted);
    }

    [Fact]
    public async Task A_second_upload_without_library_json_inherits_the_previous_versions()
    {
        Assert.True((await Upload()).Accepted);
        var r = await Upload(json: null);
        Assert.True(r.Accepted, string.Join("; ", r.Inspection.Problems));
        Assert.Equal(2, r.Pack.Version);
    }

    [Fact]
    public async Task A_valid_upload_after_a_rejection_is_the_next_ready_version()
    {
        Assert.Equal(1, (await Upload()).Pack.Version);
        Assert.False((await Upload(mod: Samples.Mod, json: "{")).Accepted);
        Assert.Equal(2, (await Upload()).Pack.Version);
    }

    [Fact]
    public async Task The_pack_s_rooms_are_kept_for_the_rules_module_layout()
    {
        var r = await Upload();
        Assert.Equal(["corner", "cross", "end", "hall", "tee"], _catalog.RoomsOf(r.Pack).Select(x => x.Name));
    }

    [Fact]
    public async Task An_upload_over_the_size_limit_is_refused_and_leaves_no_file()
    {
        var options = new ServiceOptions { MapPool = { MapsPath = Samples.TempDir() }, Admin = { MaxUploadBytes = 1000 } };
        var small = new PackCatalog(_db.Data, new PackValidator(Samples.Mod, 15), new LevelStorage(options.MapPool), options);
        await using var s = File.OpenRead(await Samples.PackCopyAsync());
        var e = await Assert.ThrowsAsync<HostRefusal>(() => small.UploadAsync(Samples.Mod, Samples.Library, s, Samples.ManifestJson, null, "upload", "t"));
        Assert.Equal("too_large", e.Reason);
        Assert.Empty(Directory.GetFiles(options.MapPool.MapsPath, "*", SearchOption.AllDirectories));
    }
}
