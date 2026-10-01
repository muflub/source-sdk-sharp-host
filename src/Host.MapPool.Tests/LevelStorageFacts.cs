using SourceSharp.Host.Abstractions;
using SourceSharp.Host.MapPool;
using SourceSharp.Host.Testing;

namespace SourceSharp.Host.MapPool.Tests;

public sealed class LevelStorageFacts
{
    [Fact]
    public void A_level_map_name_parses_into_mod_depth_and_hash()
    {
        Assert.True(LevelStorage.TryParseMapName("descent-7-0123456789abcdef", out var mod, out var depth, out var hash));
        Assert.Equal(("descent", 7, "0123456789abcdef"), (mod, depth, hash));
    }

    [Theory]
    [InlineData("descent_town")]
    [InlineData("descent-7-0123456789ABCDEF")]
    [InlineData("descent-0-0123456789abcdef")]
    [InlineData("descent-7-0123456789abcde")]
    [InlineData("../descent-7-0123456789abcdef")]
    [InlineData("Descent-7-0123456789abcdef")]
    public void Anything_else_is_not_a_level_map_name(string name) =>
        Assert.False(LevelStorage.TryParseMapName(name, out _, out _, out _));

    [Fact]
    public async Task Storing_a_link_moves_its_files_and_writes_a_bz2_of_the_bsp()
    {
        var maps = Path.Combine(Path.GetTempPath(), "lane-m-" + Guid.NewGuid().ToString("N"));
        var storage = new LevelStorage(new MapPoolOptions { MapsPath = maps });
        var files = await new FakeMapCompiler().LinkAsync(new LinkRequest("descent-1-0123456789abcdef", 1, 0, 1, "crypt", "", null,
            Path.Combine(maps, "staging", "x")));
        var bsp = await File.ReadAllBytesAsync(files.Bsp);
        var bytes = await storage.StoreAsync(files);
        Assert.False(File.Exists(files.Bsp));
        await using var bz2 = File.OpenRead(storage.LevelFile("descent-1-0123456789abcdef", ".bsp.bz2"));
        Assert.Equal(bsp, LevelStorage.Decompress(bz2));
        Assert.True(bytes > bsp.Length);
    }

    [Fact]
    public async Task Deleting_a_level_removes_all_its_files()
    {
        var maps = Path.Combine(Path.GetTempPath(), "lane-m-" + Guid.NewGuid().ToString("N"));
        var storage = new LevelStorage(new MapPoolOptions { MapsPath = maps });
        var files = await new FakeMapCompiler().LinkAsync(new LinkRequest("descent-1-0123456789abcdef", 1, 0, 1, "crypt", "", null,
            Path.Combine(maps, "staging", "x")));
        await storage.StoreAsync(files);
        Assert.Equal(5, storage.DeleteLevel("descent-1-0123456789abcdef"));
        Assert.Empty(Directory.GetFiles(storage.LevelsDirectory));
    }
}
