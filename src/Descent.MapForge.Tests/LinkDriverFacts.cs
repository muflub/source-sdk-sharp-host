using Descent.MapForge;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.MapPool;

namespace Descent.MapForge.Tests;

/// <summary>§9.1 Link + packager, with a real link of the tools' sample pack.</summary>
public sealed class LinkDriverFacts
{
    static PackRecord Pack(long id = 1, string packId = "p") =>
        new(id, Samples.Mod, Samples.Library, 1, packId, "v", "upload", "t", DateTimeOffset.UnixEpoch, 0, "s", null, null, PackState.Ready);

    static (LinkDriver Driver, LevelStorage Storage) Driver()
    {
        var maps = Samples.TempDir();
        var pool = new MapPoolOptions { MapsPath = maps };
        var storage = new LevelStorage(pool);
        return (new LinkDriver(new LinkedMapCompiler(new LinkSettings { Transitions = false }), storage, new ModOptions(), pool,
            new MapPoolLayoutOptions()), storage);
    }

    [Fact]
    public async Task A_real_link_stores_bsp_bz2_map2d_and_yaml_under_levels_named_mod_depth_hash()
    {
        var (driver, storage) = Driver();
        var level = await driver.LinkAsync(Pack(), await Samples.PackCopyAsync(), 4, 99, null, Samples.RoomInfos);
        Assert.Equal($"descent-4-{level.Hash}", level.MapName);
        foreach (var ext in new[] { ".bsp", ".bsp.bz2", ".map2d", ".yaml" })
            Assert.True(File.Exists(storage.LevelFile(level.MapName, ext)), ext);
    }

    [Fact]
    public async Task The_bz2_decompresses_to_the_bsp_byte_for_byte()
    {
        var (driver, storage) = Driver();
        var level = await driver.LinkAsync(Pack(), await Samples.PackCopyAsync(), 2, 5, Samples.SampleLevelPlan(), Samples.RoomInfos);
        var bsp = await File.ReadAllBytesAsync(storage.LevelFile(level.MapName, ".bsp"));
        await using var bz2 = File.OpenRead(storage.LevelFile(level.MapName, ".bsp.bz2"));
        Assert.True(bsp.Length > 0);
        Assert.Equal(bsp, LevelStorage.Decompress(bz2));
    }

    [Fact]
    public async Task The_staging_folder_is_gone_after_a_link()
    {
        var (driver, storage) = Driver();
        await driver.LinkAsync(Pack(), await Samples.PackCopyAsync(), 1, 3, null, Samples.RoomInfos);
        Assert.Empty(Directory.GetFileSystemEntries(storage.StagingDirectory));
    }

    [Fact]
    public async Task Relinking_the_same_inputs_writes_the_same_bsp()
    {
        var (a, sa) = Driver();
        var (b, sb) = Driver();
        var pack = await Samples.PackCopyAsync();
        var la = await a.LinkAsync(Pack(), pack, 1, 3, null, Samples.RoomInfos);
        var lb = await b.LinkAsync(Pack(), pack, 1, 3, null, Samples.RoomInfos);
        Assert.Equal(la.MapName, lb.MapName);
        Assert.Equal(await File.ReadAllBytesAsync(sa.LevelFile(la.MapName, ".bsp")), await File.ReadAllBytesAsync(sb.LevelFile(lb.MapName, ".bsp")));
    }

    [Fact]
    public void The_hash_changes_with_the_seed() =>
        Assert.NotEqual(Driver().Driver.HashOf(Pack(), 1, 1, 0), Driver().Driver.HashOf(Pack(), 1, 2, 0));

    [Fact]
    public void The_hash_changes_with_the_pack_version() =>
        Assert.NotEqual(Driver().Driver.HashOf(Pack(id: 1), 1, 1, 0), Driver().Driver.HashOf(Pack(id: 2), 1, 1, 0));

    [Fact]
    public void The_hash_changes_with_the_depth_and_difficulty()
    {
        var d = Driver().Driver;
        Assert.NotEqual(d.HashOf(Pack(), 1, 1, 0), d.HashOf(Pack(), 2, 1, 0));
        Assert.NotEqual(d.HashOf(Pack(), 1, 1, 0), d.HashOf(Pack(), 1, 1, 1));
    }

    [Fact]
    public void The_hash_changes_with_the_linker_identity()
    {
        var pool = new MapPoolOptions { MapsPath = Samples.TempDir() };
        var s = new LevelStorage(pool);
        var a = new LinkDriver(new LinkedMapCompiler(new LinkSettings { Transitions = false }), s, new ModOptions(), pool, new MapPoolLayoutOptions());
        var b = new LinkDriver(new LinkedMapCompiler(), s, new ModOptions(), pool, new MapPoolLayoutOptions());
        Assert.NotEqual(a.HashOf(Pack(), 1, 1, 0), b.HashOf(Pack(), 1, 1, 0));
    }
}
