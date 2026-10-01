using System.Text.Json.Nodes;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.MapPool;
using SourceSharp.Host.Testing;

namespace SourceSharp.Host.MapPool.Tests;

/// <summary>§9.4 assignment: activate, pin, roll back, retire, the old-levels policy, GC.</summary>
public sealed class PackCatalogFacts : IAsyncLifetime
{
    internal const string Mod = "descent", Lib = "crypt";
    readonly TestData _db = new();
    internal ServiceOptions Options = null!;
    internal PackCatalog Catalog = null!;

    public Task InitializeAsync()
    {
        Options = new ServiceOptions { MapPool = { MapsPath = Path.Combine(Path.GetTempPath(), "lane-m-" + Guid.NewGuid().ToString("N")) } };
        Catalog = new PackCatalog(_db.Data, new FakePackValidator(Mod), new LevelStorage(Options.MapPool), Options);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    async Task<PackRecord> Upload(string library = Lib, string source = PackCatalog.SourceUpload, string[]? rooms = null)
    {
        using var s = new MemoryStream(FakePack.Bytes(library, rooms ?? ["start", "hall", "stairs"]));
        var r = await Catalog.UploadAsync(Mod, library, s, "{}", null, source, "admin@localhost");
        Assert.True(r.Accepted, string.Join("; ", r.Inspection.Problems));
        return r.Pack;
    }

    Task<PackAssignment?> At(int depth) => _db.Read(tx => tx.Library.Assignment(Mod, depth));

    Task<LevelRecord> Level(PackRecord p, int depth, LevelState state, string hash) => _db.Write(tx =>
        tx.Levels.Add(new LevelRecord(hash, p.Id, depth, p.Library, 1, 0, state, tx.Now, state == LevelState.HandedOut ? "inst-1" : null, 10, null, "fake")));

    Task<LevelRecord?> LevelAt(string hash) => _db.Read(tx => tx.Levels.Get(hash));

    [Fact]
    public async Task A_valid_upload_becomes_the_next_version()
    {
        Assert.Equal(1, (await Upload()).Version);
        Assert.Equal(2, (await Upload()).Version);
    }

    [Fact]
    public async Task A_new_version_is_not_used_until_assigned()
    {
        var v1 = await Upload();
        await Catalog.ActivateAsync(Mod, [1, 2], v1.Id, "admin");
        var v2 = await Upload();
        Assert.Equal(v1.Id, (await At(1))!.PackId);
        Assert.NotEqual(v1.Id, v2.Id);
    }

    [Fact]
    public async Task Activate_assigns_the_version_to_each_depth_pinned_by_default()
    {
        var v1 = await Upload();
        await Catalog.ActivateAsync(Mod, [3, 4], v1.Id, "admin");
        Assert.Equal((v1.Id, true), ((await At(3))!.PackId, (await At(3))!.Pinned));
        Assert.Equal(v1.Id, (await At(4))!.PackId);
        Assert.Null(await At(5));
    }

    [Fact]
    public async Task Activate_is_audited_with_the_before_and_after_matrix()
    {
        var v1 = await Upload();
        var v2 = await Upload();
        await Catalog.ActivateAsync(Mod, [1], v1.Id, "admin");
        await Catalog.ActivateAsync(Mod, [1], v2.Id, "admin");
        var last = (await _db.Read(tx => tx.Audit.List(new AuditQuery(Action: "pack.activate")))).OrderBy(a => a.Id).Last();
        Assert.Equal(v1.Id, JsonNode.Parse(last.BeforeJson!)![0]!["PackId"]!.GetValue<long>());
        Assert.Equal(v2.Id, JsonNode.Parse(last.AfterJson!)![0]!["PackId"]!.GetValue<long>());
    }

    [Fact]
    public async Task Drain_keeps_the_old_versions_ready_levels_ready()
    {
        var v1 = await Upload();
        var v2 = await Upload();
        await Catalog.ActivateAsync(Mod, [1], v1.Id, "admin");
        await Level(v1, 1, LevelState.Ready, "aaaaaaaaaaaaaaaa");
        await Catalog.ActivateAsync(Mod, [1], v2.Id, "admin", policy: OldLevelsPolicy.Drain);
        Assert.Equal(LevelState.Ready, (await LevelAt("aaaaaaaaaaaaaaaa"))!.State);
    }

    [Fact]
    public async Task Retire_now_retires_the_old_versions_ready_levels_at_once()
    {
        var v1 = await Upload();
        var v2 = await Upload();
        await Catalog.ActivateAsync(Mod, [1], v1.Id, "admin");
        await Level(v1, 1, LevelState.Ready, "aaaaaaaaaaaaaaaa");
        Assert.Equal(LevelState.Ready, (await LevelAt("aaaaaaaaaaaaaaaa"))!.State);
        await Catalog.ActivateAsync(Mod, [1], v2.Id, "admin", policy: OldLevelsPolicy.Retire);
        Assert.Equal(LevelState.Retired, (await LevelAt("aaaaaaaaaaaaaaaa"))!.State);
    }

    [Fact]
    public async Task A_handed_out_level_keeps_its_state_and_version_across_an_assignment_change()
    {
        var v1 = await Upload();
        var v2 = await Upload();
        await Catalog.ActivateAsync(Mod, [1], v1.Id, "admin");
        var before = await Level(v1, 1, LevelState.HandedOut, "bbbbbbbbbbbbbbbb");
        await Catalog.ActivateAsync(Mod, [1], v2.Id, "admin", policy: OldLevelsPolicy.Retire);
        Assert.Equal(before, await LevelAt("bbbbbbbbbbbbbbbb"));
    }

    [Fact]
    public async Task Retire_of_an_unassigned_version_is_accepted()
    {
        var v1 = await Upload();
        Assert.Equal(PackState.Retired, (await Catalog.RetireAsync(v1.Id, "admin")).State);
    }

    [Fact]
    public async Task Retire_of_an_assigned_version_is_refused()
    {
        var v1 = await Upload();
        await Catalog.ActivateAsync(Mod, [2], v1.Id, "admin");
        var e = await Assert.ThrowsAsync<HostRefusal>(() => Catalog.RetireAsync(v1.Id, "admin"));
        Assert.Equal("pack_assigned", e.Reason);
        Assert.Equal(PackState.Ready, (await _db.Read(tx => tx.Library.GetPack(v1.Id)))!.State);
    }

    [Fact]
    public async Task A_retired_version_cannot_be_activated()
    {
        var v1 = await Upload();
        await Catalog.RetireAsync(v1.Id, "admin");
        var e = await Assert.ThrowsAsync<HostRefusal>(() => Catalog.ActivateAsync(Mod, [1], v1.Id, "admin"));
        Assert.Equal("pack_not_ready", e.Reason);
    }

    [Fact]
    public async Task Roll_back_activates_an_older_ready_version()
    {
        var v1 = await Upload();
        var v2 = await Upload();
        await Catalog.ActivateAsync(Mod, [1], v2.Id, "admin");
        await Catalog.RollBackAsync(Mod, [1], v1.Id, "admin");
        Assert.Equal(v1.Id, (await At(1))!.PackId);
    }

    [Fact]
    public async Task Roll_back_to_a_version_that_is_not_older_is_refused()
    {
        var v1 = await Upload();
        var v2 = await Upload();
        await Catalog.ActivateAsync(Mod, [1], v1.Id, "admin");
        var e = await Assert.ThrowsAsync<HostRefusal>(() => Catalog.RollBackAsync(Mod, [1], v2.Id, "admin"));
        Assert.Equal("not_older", e.Reason);
    }

    [Fact]
    public async Task Auto_activate_moves_unpinned_depths_to_a_new_bake()
    {
        Options.MapPool.AutoActivateBakes = true;
        var v1 = await Upload();
        await Catalog.ActivateAsync(Mod, [1], v1.Id, "admin", pinned: false);
        var baked = await Upload(source: PackCatalog.SourceBake);
        Assert.Equal(baked.Id, (await At(1))!.PackId);
    }

    [Fact]
    public async Task Auto_activate_leaves_pinned_depths_alone()
    {
        Options.MapPool.AutoActivateBakes = true;
        var v1 = await Upload();
        await Catalog.ActivateAsync(Mod, [1], v1.Id, "admin", pinned: true);
        await Upload(source: PackCatalog.SourceBake);
        Assert.Equal(v1.Id, (await At(1))!.PackId);
    }

    [Fact]
    public async Task Without_auto_activate_a_bake_moves_nothing()
    {
        var v1 = await Upload();
        await Catalog.ActivateAsync(Mod, [1], v1.Id, "admin", pinned: false);
        await Upload(source: PackCatalog.SourceBake);
        Assert.Equal(v1.Id, (await At(1))!.PackId);
    }

    [Fact]
    public async Task Pin_changes_the_flag_and_not_the_version()
    {
        var v1 = await Upload();
        await Catalog.ActivateAsync(Mod, [1], v1.Id, "admin", pinned: false);
        await Catalog.PinAsync(Mod, [1], true, "admin");
        Assert.Equal((v1.Id, true), ((await At(1))!.PackId, (await At(1))!.Pinned));
    }

    [Fact]
    public async Task Gc_deletes_a_retired_unreferenced_versions_file()
    {
        var v1 = await Upload();
        await Catalog.RetireAsync(v1.Id, "admin");
        Assert.True(File.Exists(Catalog.PathOf(v1)));
        Assert.Equal([v1.Id], await Catalog.CollectAsync(Mod));
        Assert.False(File.Exists(Catalog.PathOf(v1)));
    }

    [Fact]
    public async Task Gc_never_deletes_a_pack_a_handed_out_level_references()
    {
        var v1 = await Upload();
        await Level(v1, 1, LevelState.HandedOut, "cccccccccccccccc");
        await Catalog.RetireAsync(v1.Id, "admin");
        Assert.Empty(await Catalog.CollectAsync(Mod));
        Assert.True(File.Exists(Catalog.PathOf(v1)));
    }

    [Fact]
    public async Task Gc_never_deletes_an_assigned_pack()
    {
        var v1 = await Upload();
        await Catalog.ActivateAsync(Mod, [1], v1.Id, "admin");
        Assert.Empty(await Catalog.CollectAsync(Mod));
        Assert.True(File.Exists(Catalog.PathOf(v1)));
    }

    [Fact]
    public async Task A_library_key_that_is_a_path_is_refused_before_anything_is_written()
    {
        using var s = new MemoryStream(FakePack.Bytes(Lib, ["start"]));
        var e = await Assert.ThrowsAsync<HostRefusal>(() => Catalog.UploadAsync(Mod, "../x", s, null, null, "upload", "t"));
        Assert.Equal("bad_key", e.Reason);
        Assert.False(Directory.Exists(Options.MapPool.MapsPath));
    }
}
