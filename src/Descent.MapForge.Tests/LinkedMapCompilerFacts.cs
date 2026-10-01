using Descent.MapForge;
using SourceSharp.Host.Abstractions;

namespace Descent.MapForge.Tests;

/// <summary>§9.2: the real compiler links a level from a pack in-process, with no content mounted.</summary>
public sealed class LinkedMapCompilerFacts
{
    static readonly LinkSettings Plain = new() { Transitions = false };

    static async Task<LinkRequest> Request(ulong seed = 7, bool plan = true)
    {
        var pack = await Samples.PackCopyAsync();
        return new LinkRequest($"descent-1-{seed:x16}", 1, 0, seed, Samples.Library, pack, plan ? Samples.SampleLevelPlan() : null,
            Path.Combine(Path.GetDirectoryName(pack)!, "levels")) { Rooms = Samples.RoomInfos };
    }

    [Fact]
    public async Task A_plan_links_to_a_bsp_whose_header_is_VBSP()
    {
        var files = await new LinkedMapCompiler(Plain).LinkAsync(await Request());
        var head = new byte[4];
        await using (var f = File.OpenRead(files.Bsp)) await f.ReadExactlyAsync(head);
        Assert.Equal("VBSP"u8.ToArray(), head);
    }

    [Fact]
    public async Task A_link_writes_the_level_map_beside_the_bsp()
    {
        var files = await new LinkedMapCompiler(Plain).LinkAsync(await Request());
        Assert.NotNull(files.Map2d);
        Assert.True(new FileInfo(files.Map2d!).Length > 0);
        Assert.Equal(Path.GetDirectoryName(files.Bsp), Path.GetDirectoryName(files.Map2d));
    }

    [Fact]
    public async Task A_link_keeps_its_level_yaml_beside_the_bsp_naming_the_library_key()
    {
        var files = await new LinkedMapCompiler(Plain).LinkAsync(await Request());
        Assert.Equal(files.MapName + ".yaml", Path.GetFileName(files.LevelYaml));
        Assert.Contains("library: crypt.vmf\n", await File.ReadAllTextAsync(files.LevelYaml));
    }

    [Fact]
    public async Task Without_a_plan_the_tools_generator_lays_out_the_level_from_the_seed()
    {
        var files = await new LinkedMapCompiler(Plain).LinkAsync(await Request(seed: 11, plan: false));
        var yaml = await File.ReadAllTextAsync(files.LevelYaml);
        Assert.Contains("seed 11", yaml);
        Assert.True(new FileInfo(files.Bsp).Length > 0);
    }

    [Fact]
    public async Task Two_seeds_without_a_plan_give_two_layouts()
    {
        var c = new LinkedMapCompiler(Plain);
        var a = await File.ReadAllTextAsync((await c.LinkAsync(await Request(seed: 1, plan: false))).LevelYaml);
        var b = await File.ReadAllTextAsync((await c.LinkAsync(await Request(seed: 2, plan: false))).LevelYaml);
        Assert.NotEqual(Grid(a), Grid(b));
    }

    static string Grid(string yaml) => yaml[yaml.IndexOf("grid:", StringComparison.Ordinal)..];

    [Fact]
    public async Task A_plan_placing_a_room_the_pack_lacks_is_refused_with_the_tools_words()
    {
        var r = await Request();
        var plan = r.Plan! with { Placements = [.. r.Plan!.Placements.Skip(1), new("no_such_room", 0, 0, 0, "fill")] };
        var e = await Assert.ThrowsAsync<MapCompileFailure>(() => new LinkedMapCompiler(Plain).LinkAsync(r with { Plan = plan }));
        Assert.Contains("no_such_room", e.Message);
    }

    [Fact]
    public async Task A_pack_without_the_library_key_is_refused()
    {
        var r = await Request();
        var e = await Assert.ThrowsAsync<MapCompileFailure>(() => new LinkedMapCompiler(Plain).LinkAsync(r with { Library = "caves" }));
        Assert.Contains("none named 'caves'", e.Message);
    }

    [Fact]
    public void The_linker_identity_names_the_tools_build()
    {
        Assert.StartsWith("ssmap:", new LinkedMapCompiler().LinkerIdentity);
        Assert.NotEqual(new LinkedMapCompiler(Plain).LinkerIdentity, new LinkedMapCompiler().LinkerIdentity);
    }
}
