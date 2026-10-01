using Descent.MapForge;
using SourceSharp.Host.Contracts;
using SourceSharp.Host.Testing;
using SourceSharp.MapTools.Rooms;
using LevelPlan = SourceSharp.Host.Contracts.LevelPlan;

namespace Descent.MapForge.Tests;

/// <summary>
/// §9.1 Layout: <c>IGameRules.GenerateLayout</c> → <see cref="LevelPlan"/> → the <c>level.yaml</c>
/// the tools read. Fixtures under Fixtures/levels are checked in; <c>LANE_M_WRITE_FIXTURES=1</c>
/// rewrites them (and the fact still fails that run, so a rewrite is never silent).
/// </summary>
public sealed class LevelFileFacts
{
    static string Fixture(string name) => RepoFiles.Path_("src", "Descent.MapForge.Tests", "Fixtures", "levels", name);

    static void AssertFixture(string name, string actual)
    {
        var path = Fixture(name);
        if (Environment.GetEnvironmentVariable("LANE_M_WRITE_FIXTURES") == "1")
        {
            File.WriteAllText(path, actual);
            Assert.Fail($"rewrote {name}; run again without LANE_M_WRITE_FIXTURES");
        }
        Assert.True(File.Exists(path), $"fixture {name} is missing");
        Assert.Equal(File.ReadAllBytes(path), System.Text.Encoding.UTF8.GetBytes(actual));
    }

    static LevelPlan FakeRulesPlan() =>
        new FakeGameRules().GenerateLayout(new LayoutKey(Samples.Library, 3, 0, 42, Samples.RoomInfos));

    [Fact]
    public void The_sample_plan_writes_the_checked_in_level_file() =>
        AssertFixture("sample-plan.yaml", LevelFileWriter.Write(Samples.SampleLevelPlan(), Samples.Library, "x", null));

    [Fact]
    public void The_same_plan_writes_the_same_bytes()
    {
        var a = LevelFileWriter.Write(FakeRulesPlan(), Samples.Library, "x", new LevelKeys("descent-2", "descent-4"));
        var b = LevelFileWriter.Write(FakeRulesPlan(), Samples.Library, "x", new LevelKeys("descent-2", "descent-4"));
        Assert.Equal(a, b);
    }

    [Fact]
    public void A_rules_module_plan_with_transitions_writes_the_checked_in_level_file() =>
        AssertFixture("fake-rules-depth3-seed42.yaml", LevelFileWriter.Write(FakeRulesPlan(), Samples.Library, "x", new LevelKeys("descent-2", "descent-4")));

    [Fact]
    public void The_written_file_reads_back_as_the_tools_own_sample_level()
    {
        var ours = LevelYaml.Parse(LevelFileWriter.Write(Samples.SampleLevelPlan(), Samples.Library, "x", null), "x");
        var tools = LevelYaml.Parse(File.ReadAllText(Path.Combine(Samples.Rooms3x3, "levels", "rooms3x3.yaml")), "x");
        Assert.Equal(tools.Cells.Select(c => (c?.Room, c?.Rotation)), ours.Cells.Select(c => (c?.Room, c?.Rotation)));
    }

    [Fact]
    public void The_top_level_writes_up_none()
    {
        var yaml = LevelFileWriter.Write(FakeRulesPlan(), Samples.Library, "x", new LevelKeys(null, "descent-2"));
        Assert.Contains("\nup: none\n", yaml);
        Assert.Contains("\ndown_map: descent-2\n", yaml);
    }

    [Fact]
    public void A_placement_outside_the_grid_is_refused()
    {
        Assert.NotNull(LevelFileWriter.ToGrid(Samples.SampleLevelPlan(), Samples.Library, "x", null));
        var plan = Samples.SampleLevelPlan() with { Placements = [new("end", 3, 0, 0, "up")] };
        Assert.Throws<ArgumentException>(() => LevelFileWriter.ToGrid(plan, Samples.Library, "x", null));
    }

    [Fact]
    public void A_rotation_that_is_not_a_quarter_turn_is_refused()
    {
        var plan = Samples.SampleLevelPlan() with { Placements = [new("end", 0, 0, 45, "up")] };
        Assert.Throws<ArgumentException>(() => LevelFileWriter.ToGrid(plan, Samples.Library, "x", null));
    }

    [Fact]
    public void Two_rooms_in_one_cell_are_refused()
    {
        var plan = Samples.SampleLevelPlan() with { Placements = [new("end", 0, 0, 0, "up"), new("hall", 0, 0, 0, "fill")] };
        Assert.Throws<ArgumentException>(() => LevelFileWriter.ToGrid(plan, Samples.Library, "x", null));
    }

    /// <summary>FakeGameRules lays rooms in a row without looking at sockets: the tools refuse what a player cannot walk.</summary>
    [Fact]
    public async Task A_rules_module_plan_a_player_cannot_walk_is_refused_by_the_tools()
    {
        var pack = await Samples.PackCopyAsync();
        var c = new LinkedMapCompiler(new LinkSettings { Transitions = false });
        var dir = Path.Combine(Path.GetDirectoryName(pack)!, "levels");
        var ok = new SourceSharp.Host.Abstractions.LinkRequest("descent-3-ok", 3, 0, 42, Samples.Library, pack, Samples.SampleLevelPlan(), dir);
        Assert.True(new FileInfo((await c.LinkAsync(ok)).Bsp).Length > 0);
        var e = await Assert.ThrowsAsync<SourceSharp.Host.Abstractions.MapCompileFailure>(() => c.LinkAsync(ok with { MapName = "descent-3-fake", Plan = FakeRulesPlan() }));
        Assert.Contains("EveryRoomReachable", e.Message);
    }
}
