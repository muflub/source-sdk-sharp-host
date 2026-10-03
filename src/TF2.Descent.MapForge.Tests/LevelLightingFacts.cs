using TF2.Descent.MapForge;
using SourceSharp.Host.Abstractions;

namespace TF2.Descent.MapForge.Tests;

/// <summary>
/// A level baked with light carries HDR lighting as well as LDR, as the town does. The engine
/// enables HDR only for a map with lumps 53, 54 and 55 all present, so an LDR-only level makes
/// every town ↔ level hop flip HDR on the client: a full material reload of 4.4–6.1 s.
/// </summary>
public sealed class LevelLightingFacts
{
    const int Lighting = 8, WorldLights = 15, LeafAmbientIndexHdr = 51, LeafAmbientIndex = 52;
    const int LightingHdr = 53, WorldLightsHdr = 54, LeafAmbientLightingHdr = 55, LeafAmbientLighting = 56;

    static async Task<int[]> LitLevelLumpSizes()
    {
        var baked = await Samples.LitBakedAsync();
        var pack = Path.Combine(Samples.TempDir(), Samples.Library + ".roompack");
        File.Copy(baked.PackPath, pack);
        var request = new LinkRequest("tf2_descent-1-0000000000000007", 1, 0, 7, Samples.Library, pack, Samples.SampleLevelPlan(),
            Path.Combine(Path.GetDirectoryName(pack)!, "levels")) { Rooms = Samples.RoomInfos };
        var files = await new LinkedMapCompiler(new LinkSettings { Transitions = false }).LinkAsync(request);
        return LumpSizes(await File.ReadAllBytesAsync(files.Bsp));
    }

    /// <summary>Each lump's uncompressed size from the header: filelen, or the LZMA fourCC when set.</summary>
    static int[] LumpSizes(byte[] bsp)
    {
        Assert.Equal("VBSP"u8.ToArray(), bsp[..4]);
        var sizes = new int[64];
        for (var i = 0; i < 64; i++)
        {
            var at = 8 + i * 16;
            var len = BitConverter.ToInt32(bsp, at + 4);
            var fourCc = BitConverter.ToInt32(bsp, at + 12);
            sizes[i] = fourCc != 0 ? fourCc : len;
        }
        return sizes;
    }

    [Fact]
    public async Task A_lit_level_has_LDR_lighting()
    {
        var sizes = await LitLevelLumpSizes();
        Assert.True(sizes[Lighting] > 0, "LIGHTING is empty");
        Assert.True(sizes[LeafAmbientLighting] > 0, "LEAF_AMBIENT_LIGHTING is empty");
    }

    [Fact]
    public async Task A_lit_level_has_the_three_lumps_the_engine_needs_for_HDR()
    {
        var sizes = await LitLevelLumpSizes();
        Assert.True(sizes[LightingHdr] > 0, "LIGHTING_HDR is empty");
        Assert.True(sizes[WorldLightsHdr] > 0, "WORLDLIGHTS_HDR is empty");
        Assert.True(sizes[LeafAmbientLightingHdr] > 0, "LEAF_AMBIENT_LIGHTING_HDR is empty");
    }

    [Fact]
    public async Task A_lit_level_has_HDR_lighting_the_same_shape_as_its_LDR_lighting()
    {
        var sizes = await LitLevelLumpSizes();
        Assert.Equal(sizes[Lighting], sizes[LightingHdr]);
        Assert.Equal(sizes[WorldLights], sizes[WorldLightsHdr]);
        Assert.Equal(sizes[LeafAmbientIndex], sizes[LeafAmbientIndexHdr]);
        Assert.Equal(sizes[LeafAmbientLighting], sizes[LeafAmbientLightingHdr]);
    }
}
