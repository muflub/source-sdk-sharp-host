using SourceSharp.Host.Contracts;
using SourceSharp.Host.Testing;

namespace SourceSharp.Host.Tests;

public class FakeGameRulesFacts
{
    static readonly ItemRollContext Ctx = new("scout", 10, 3, 15, 1, "depth3", "reserve");

    [Fact]
    public void A_roll_is_a_function_of_its_context_and_seed()
    {
        var r = new FakeGameRules();
        Assert.Equal(r.RollItem(Ctx, 7).Instance, r.RollItem(Ctx, 7).Instance);
    }

    [Fact]
    public void A_rolled_item_replays()
    {
        var r = new FakeGameRules();
        Assert.True(r.Replays(r.RollItem(Ctx, 7)));
    }

    [Fact]
    public void A_tampered_item_does_not_replay()
    {
        var r = new FakeGameRules();
        var item = r.RollItem(Ctx, 7);
        Assert.False(r.Replays(item with { Rarity = item.Rarity + 1 }));
    }

    [Fact]
    public void The_codec_rebuilds_the_item_with_its_context_from_its_bytes()
    {
        var r = new FakeGameRules();
        var item = r.RollItem(Ctx, 7);
        var back = r.Items.Decode(item.Instance, item.SchemaVersion);
        Assert.Equal((item.BaseType, item.Seed, Ctx), (back.BaseType, back.Seed, back.Context));
        Assert.True(r.Replays(back));
    }

    [Fact]
    public void A_changed_item_carries_its_new_state_in_its_bytes()
    {
        var r = new FakeGameRules();
        var strange = r.RollItem(Ctx with { Tier = 2 }, 7);
        Assert.False(strange.Identified);
        var identified = r.Identify(strange, 1).Produced[0];
        Assert.True(r.Items.Decode(identified.Instance, identified.SchemaVersion).Identified);
    }

    [Fact]
    public void A_kill_roll_is_deterministic_and_a_salt_changes_it()
    {
        var a = new FakeGameRules();
        var b = new FakeGameRules { Salt = 1 };
        var rolls = Enumerable.Range(0, 50).Select(k => a.KillRoll(99, (uint)k, "heavy", 3, 2)).ToList();
        Assert.Equal(rolls, Enumerable.Range(0, 50).Select(k => a.KillRoll(99, (uint)k, "heavy", 3, 2)));
        Assert.NotEqual(rolls, Enumerable.Range(0, 50).Select(k => b.KillRoll(99, (uint)k, "heavy", 3, 2)));
        Assert.Contains(rolls, x => x.Drop);
        Assert.Contains(rolls, x => !x.Drop);
    }

    [Fact]
    public void A_checkpoint_that_lowers_the_level_names_the_rule()
    {
        var r = new FakeGameRules();
        var before = FakeGameRules.SheetCodec.Write(new FakeGameRules.Sheet("scout", "Ann", 5, 100, 1, false, false));
        var after = FakeGameRules.SheetCodec.Write(new FakeGameRules.Sheet("scout", "Ann", 4, 100, 1, false, false));
        Assert.Equal("level_decreased", r.ValidateCheckpoint(before, after, new CheckpointEvidence([], 1)).FailedRule);
    }
}
