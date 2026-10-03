using Microsoft.Extensions.Time.Testing;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Testing;

namespace SourceSharp.Host.Data.Tests;

/// <summary>D-H18: level copies in the claims and instance rows.</summary>
public class LevelCopyFacts
{
    [Fact]
    public void A_fork_shares_the_hash_of_what_it_forked()
    {
        var clock = new FakeTimeProvider();
        var fork = LevelIds.Fork("L1", clock);
        Assert.Equal(("L1", "L1"), (LevelIds.HashOf(fork), LevelIds.HashOf(LevelIds.Fork(fork, clock))));
        Assert.NotEqual("L1", fork);
    }

    [Fact]
    public async Task Claims_on_a_level_count_every_copy_and_claims_on_a_copy_count_only_it()
    {
        await using var d = new TestData();
        await d.Write(tx => tx.LevelClaims.Set("c1", 1, "L1"));
        await d.Write(tx => tx.LevelClaims.Set("c2", 1, "L1~X"));
        await d.Write(tx => tx.LevelClaims.Set("c3", 1, "L10")); // a different hash with the same prefix
        Assert.Equal((2, 1), (await d.Read(tx => tx.LevelClaims.ClaimsOn("L1")), await d.Read(tx => tx.LevelClaims.ClaimsOnCopy("L1~X"))));
    }

    [Fact]
    public async Task An_instance_keeps_the_copy_it_runs()
    {
        await using var d = new TestData();
        var row = new InstanceRecord("i1", InstanceKind.Level, InstanceState.Live, 1, null, "L1", null, null, null, 0, "h", null, null, null, 1,
            d.Clock.GetUtcNow(), null, null, null, null, null, null, null, 0, 27015, "L1~X");
        await d.Write(tx => tx.Instances.Add(row));
        Assert.Equal("L1~X", (await d.Read(tx => tx.Instances.Get("i1")))!.LevelCopy);
    }

    [Fact]
    public async Task A_row_from_before_copies_runs_the_original()
    {
        await using var d = new TestData();
        await d.Write(tx => tx.Instances.Add(new InstanceRecord("i1", InstanceKind.Level, InstanceState.Live, 1, null, "L1", null, null, null, 0, "h",
            null, null, null, 1, d.Clock.GetUtcNow(), null, null, null, null, null, null, null, 0, 27015)));
        Assert.Equal("L1", (await d.Read(tx => tx.Instances.Get("i1")))!.LevelCopy);
    }
}
