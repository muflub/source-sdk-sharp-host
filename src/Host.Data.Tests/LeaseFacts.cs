using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Testing;

namespace SourceSharp.Host.Data.Tests;

/// <summary>Plan §4.3 gate facts.</summary>
public class LeaseFacts
{
    static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    static async Task<(TestData D, string Character)> WithCharacter()
    {
        var d = new TestData();
        var ch = await d.Write(tx => tx.Characters.Create("76561198000000001", "scout", "Ann", false, [1], 1, 1, 0));
        return (d, ch.Id);
    }

    [Fact]
    public async Task A_first_lease_is_granted_and_not_reentrant()
    {
        var (d, ch) = await WithCharacter();
        await using var _ = d;
        var outcome = await d.Write(tx => tx.Leases.Acquire(ch, "inst-a", Ttl));
        var granted = Assert.IsType<LeaseOutcome.Granted>(outcome);
        Assert.False(granted.Reentrant);
        Assert.Equal(32, granted.Lease.Token.Length);
    }

    [Fact]
    public async Task A_second_instance_is_refused_with_the_holder_named()
    {
        var (d, ch) = await WithCharacter();
        await using var _ = d;
        await d.Write(tx => tx.Leases.Acquire(ch, "inst-a", Ttl));
        var outcome = await d.Write(tx => tx.Leases.Acquire(ch, "inst-b", Ttl));
        Assert.Equal("inst-a", Assert.IsType<LeaseOutcome.AlreadyLeased>(outcome).InstanceId);
    }

    [Fact]
    public async Task The_same_instance_gets_the_same_token()
    {
        var (d, ch) = await WithCharacter();
        await using var _ = d;
        var first = (LeaseOutcome.Granted)await d.Write(tx => tx.Leases.Acquire(ch, "inst-a", Ttl));
        var second = (LeaseOutcome.Granted)await d.Write(tx => tx.Leases.Acquire(ch, "inst-a", Ttl));
        Assert.True(second.Reentrant);
        Assert.Equal(first.Lease.Token, second.Lease.Token);
    }

    [Fact]
    public async Task The_current_token_verifies()
    {
        var (d, ch) = await WithCharacter();
        await using var _ = d;
        var g = (LeaseOutcome.Granted)await d.Write(tx => tx.Leases.Acquire(ch, "inst-a", Ttl));
        var lease = await d.Read(tx => tx.Leases.Verify(ch, g.Lease.Token));
        Assert.Equal("inst-a", lease.InstanceId);
    }

    [Fact]
    public async Task A_stale_token_is_refused_after_a_re_lease()
    {
        var (d, ch) = await WithCharacter();
        await using var _ = d;
        var old = (LeaseOutcome.Granted)await d.Write(tx => tx.Leases.Acquire(ch, "inst-a", Ttl));
        await d.Write(tx => tx.Leases.Release(ch, old.Lease.Token));
        await d.Write(tx => tx.Leases.Acquire(ch, "inst-b", Ttl));
        var e = await Assert.ThrowsAsync<HostRefusal>(() => d.Read(tx => tx.Leases.Verify(ch, old.Lease.Token)));
        Assert.Equal("stale_lease", e.Reason);
    }

    [Fact]
    public async Task A_lease_expires_when_the_clock_passes_its_ttl()
    {
        var (d, ch) = await WithCharacter();
        await using var _ = d;
        await d.Write(tx => tx.Leases.Acquire(ch, "inst-a", Ttl));
        Assert.Empty(await d.Read(tx => tx.Leases.Expired()));
        d.Clock.Advance(Ttl + TimeSpan.FromSeconds(1));
        Assert.Equal(ch, Assert.Single(await d.Read(tx => tx.Leases.Expired())).CharacterId);
    }

    [Fact]
    public async Task A_heartbeat_renews_every_lease_of_its_instance()
    {
        var (d, ch) = await WithCharacter();
        await using var _ = d;
        await d.Write(tx => tx.Leases.Acquire(ch, "inst-a", Ttl));
        d.Clock.Advance(TimeSpan.FromSeconds(25));
        Assert.Equal(1, await d.Write(tx => tx.Leases.RenewForInstance("inst-a", Ttl)));
        d.Clock.Advance(TimeSpan.FromSeconds(25));
        Assert.Empty(await d.Read(tx => tx.Leases.Expired()));
    }

    [Fact]
    public async Task Release_with_a_wrong_token_is_refused_and_keeps_the_lease()
    {
        var (d, ch) = await WithCharacter();
        await using var _ = d;
        await d.Write(tx => tx.Leases.Acquire(ch, "inst-a", Ttl));
        await Assert.ThrowsAsync<HostRefusal>(() => d.Write(tx => tx.Leases.Release(ch, new byte[32])));
        Assert.NotNull(await d.Read(tx => tx.Leases.Get(ch)));
    }

    [Fact]
    public async Task A_forced_release_needs_no_token()
    {
        var (d, ch) = await WithCharacter();
        await using var _ = d;
        await d.Write(tx => tx.Leases.Acquire(ch, "inst-a", Ttl));
        Assert.True(await d.Write(tx => tx.Leases.Release(ch, null)));
        Assert.Null(await d.Read(tx => tx.Leases.Get(ch)));
    }
}
