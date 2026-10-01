using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Testing;
using P = SourceSharp.Host.Proto;

namespace SourceSharp.Host.Sdk.Tests;

public class CharacterFacts
{
    const string Account = "76561198000000001";

    static async Task<(HostSdk Sdk, HostCharacter Character)> HubWithCharacter(SdkHarness h, HostSdkOptions? o = null)
    {
        await h.Instance("hub-1", InstanceKind.Hub);
        var sdk = h.Sdk("hub-1", o);
        var c = await sdk.Pumped(sdk.Characters.Create(Account, "scout", "Ann"));
        return (sdk, c.Value);
    }

    static async Task<ICharacterLease> Leased(HostSdk sdk, string id) => (await sdk.Pumped(sdk.Characters.Lease(id))).Value;

    [Fact]
    public async Task The_hub_creates_a_character_and_lists_it()
    {
        await using var h = await SdkHarness.Start();
        var (sdk, c) = await HubWithCharacter(h);
        var list = await sdk.Pumped(sdk.Characters.List(Account));
        Assert.Equal([(c.Id, "scout", "Ann")], list.Value.Select(x => (x.Id, x.ClassName, x.Name)));
    }

    [Fact]
    public async Task GetSheet_returns_the_opaque_sheet()
    {
        await using var h = await SdkHarness.Start();
        var (sdk, c) = await HubWithCharacter(h);
        var s = await sdk.Pumped(sdk.Characters.GetSheet(c.Id));
        Assert.Equal(c.Sheet.Data, s.Value.Sheet.Data);
        Assert.Equal(1u, s.Value.Sheet.SchemaVersion);
    }

    [Fact]
    public async Task The_hub_deletes_a_character()
    {
        await using var h = await SdkHarness.Start();
        var (sdk, c) = await HubWithCharacter(h);
        var r = await sdk.Pumped(sdk.Characters.Delete(c.Id));
        Assert.True(r.Ok, r.ToString());
        Assert.Empty((await sdk.Pumped(sdk.Characters.List(Account))).Value);
    }

    [Fact]
    public async Task A_level_may_not_list_characters_and_is_told_so_typed()
    {
        await using var h = await SdkHarness.Start();
        await h.Instance("lvl-1", InstanceKind.Level, 3);
        var sdk = h.Sdk("lvl-1");
        var r = await sdk.Pumped(sdk.Characters.List(Account));
        Assert.Equal(HostError.NotAHub, r.Error);
    }

    [Fact]
    public async Task A_lease_carries_the_character_and_a_second_lease_is_the_same_object()
    {
        await using var h = await SdkHarness.Start();
        var (sdk, c) = await HubWithCharacter(h);
        var a = await Leased(sdk, c.Id);
        var b = await Leased(sdk, c.Id);
        Assert.Same(a, b);
        Assert.Equal((c.Id, c.Version, true), (a.Character.Id, a.Version, a.IsHeld));
        Assert.Same(a, sdk.Characters.Held(c.Id));
    }

    [Fact]
    public async Task A_lease_held_elsewhere_is_refused_typed()
    {
        await using var h = await SdkHarness.Start();
        var (hub, c) = await HubWithCharacter(h);
        await Leased(hub, c.Id);
        await h.Instance("lvl-1", InstanceKind.Level, 3);
        var lvl = h.Sdk("lvl-1");
        var r = await lvl.Pumped(lvl.Characters.Lease(c.Id));
        Assert.Equal(HostError.AlreadyLeased, r.Error);
    }

    [Fact]
    public async Task Checkpoints_track_the_version()
    {
        await using var h = await SdkHarness.Start();
        var (sdk, c) = await HubWithCharacter(h);
        var lease = await Leased(sdk, c.Id);
        var first = await sdk.Pumped(lease.Checkpoint(c.Sheet));
        var second = await sdk.Pumped(lease.Checkpoint(c.Sheet));
        Assert.True(first.Ok && second.Ok, $"{first} / {second}");
        Assert.True(second.Value.Version > first.Value.Version);
        Assert.Equal(second.Value.Version, lease.Version);
        Assert.Equal(lease.Version, (await h.Data.ReadAsync((tx, _) => tx.Characters.Get(c.Id)))!.Version);
    }

    [Fact]
    public async Task A_checkpoint_refused_by_a_rule_names_the_rule()
    {
        await using var h = await SdkHarness.Start();
        var (sdk, c) = await HubWithCharacter(h);
        var lease = await Leased(sdk, c.Id);
        var jump = FakeGameRules.SheetCodec.Write(new FakeGameRules.Sheet("scout", "Ann", 30, 1, 0, false, false));
        var r = await sdk.Pumped(lease.Checkpoint(new OpaquePayload(jump.Data, 1)));
        Assert.Equal((HostError.Rejected, "level_jump"), (r.Error, r.Refusal!.Reason));
        Assert.True(lease.IsHeld);
    }

    [Fact]
    public async Task SetReachedDepth_writes_under_the_lease()
    {
        await using var h = await SdkHarness.Start();
        var (sdk, c) = await HubWithCharacter(h);
        var lease = await Leased(sdk, c.Id);
        var r = await sdk.Pumped(lease.SetReachedDepth(4));
        Assert.Equal(4, r.Value.ReachedDepth);
        Assert.Equal(r.Value.Version, lease.Version);
    }

    [Fact]
    public async Task Release_frees_the_character_at_the_service()
    {
        await using var h = await SdkHarness.Start();
        var (sdk, c) = await HubWithCharacter(h);
        var lease = await Leased(sdk, c.Id);
        var r = await sdk.Pumped(lease.Release());
        Assert.True(r.Ok, r.ToString());
        Assert.Null(await h.Data.ReadAsync((tx, _) => tx.Leases.Get(c.Id)));
        Assert.True(lease.IsReleased);
        Assert.Null(sdk.Characters.Held(c.Id));
    }

    [Fact]
    public async Task A_released_lease_refuses_every_write_locally_and_sends_nothing()
    {
        await using var h = await SdkHarness.Start();
        var (sdk, c) = await HubWithCharacter(h);
        var lease = await Leased(sdk, c.Id);
        await sdk.Pumped(lease.Release());
        var calls = sdk.Metrics.RpcCalls;
        var r = await sdk.Pumped(lease.Checkpoint(c.Sheet));
        var d = await sdk.Pumped(sdk.Items.Claim(lease, "01NOITEM"));
        Assert.Equal((HostError.LeaseReleased, true), (r.Error, r.Refusal!.Local));
        Assert.Equal(HostError.LeaseReleased, d.Error);
        Assert.Equal(calls, sdk.Metrics.RpcCalls);
    }

    [Fact]
    public async Task A_stale_lease_is_a_typed_result_and_raises_LeaseLost()
    {
        await using var h = await SdkHarness.Start();
        var (sdk, c) = await HubWithCharacter(h);
        var lease = await Leased(sdk, c.Id);
        var lost = new List<LeaseLostReason>();
        lease.LeaseLost += (_, why, _) => lost.Add(why);
        // Someone else holds it now: forced release (admin), then another instance leases it.
        await h.Data.WriteAsync((tx, _) => tx.Leases.Release(c.Id, null));
        await h.Instance("lvl-1", InstanceKind.Level, 3);
        var other = h.Sdk("lvl-1");
        Assert.True((await other.Pumped(other.Characters.Lease(c.Id))).Ok);

        var r = await sdk.Pumped(lease.Checkpoint(c.Sheet));
        Assert.Equal(HostError.StaleLease, r.Error);
        await sdk.PumpUntil(() => lost.Count > 0, what: "LeaseLost");
        Assert.Equal([LeaseLostReason.Stale], lost);
        Assert.True(lease.IsLost);
        Assert.Equal(HostError.LeaseLost, (await sdk.Pumped(lease.Checkpoint(c.Sheet))).Error);
    }

    [Fact]
    public async Task LeaseRevoked_on_the_stream_raises_LeaseLost()
    {
        await using var h = await SdkHarness.Start();
        var (sdk, c) = await HubWithCharacter(h);
        var lease = await Leased(sdk, c.Id);
        var lost = new List<(LeaseLostReason, string)>();
        lease.LeaseLost += (_, why, detail) => lost.Add((why, detail));
        await sdk.PumpUntil(() => h.Streams.IsOpen("hub-1"), what: "the stream");
        await h.Streams.Send("hub-1", new P.ServerCommand { LeaseRevoked = new P.LeaseRevoked { CharacterId = c.Id, Reason = "admin" } });
        await sdk.PumpUntil(() => lost.Count > 0, what: "LeaseLost");
        Assert.Equal([(LeaseLostReason.Revoked, "admin")], lost);
        Assert.False(lease.IsHeld);
    }

    [Fact]
    public async Task A_lease_whose_heartbeats_cannot_reach_the_host_is_lost_after_its_ttl()
    {
        await using var h = await SdkHarness.Start();
        var o = SdkHarness.FastOptions();
        o.LeaseTtl = TimeSpan.FromMilliseconds(800);
        var (sdk, c) = await HubWithCharacter(h, o);
        var lease = await Leased(sdk, c.Id);
        var lost = new List<LeaseLostReason>();
        lease.LeaseLost += (_, why, _) => lost.Add(why);
        h.Proxy.Block();
        var blocked = DateTime.UtcNow;
        await sdk.PumpUntil(() => lost.Count > 0, what: "LeaseLost");
        Assert.Equal([LeaseLostReason.Unreachable], lost);
        Assert.True(DateTime.UtcNow - blocked >= TimeSpan.FromMilliseconds(500), "lost before the TTL could run out");
        Assert.Equal(HostError.LeaseLost, (await sdk.Pumped(lease.Checkpoint(c.Sheet))).Error);
    }

    [Fact]
    public async Task A_lease_whose_heartbeats_are_acknowledged_outlives_its_ttl()
    {
        await using var h = await SdkHarness.Start();
        var o = SdkHarness.FastOptions();
        o.LeaseTtl = TimeSpan.FromMilliseconds(800);
        var (sdk, c) = await HubWithCharacter(h, o);
        var lease = await Leased(sdk, c.Id);
        var acked = sdk.Metrics.HeartbeatsAcked;
        var until = DateTime.UtcNow + TimeSpan.FromMilliseconds(2000);
        while (DateTime.UtcNow < until) { sdk.Pump(); await Task.Delay(20); }
        Assert.True(sdk.Metrics.HeartbeatsAcked > acked + 5);
        Assert.True(lease.IsHeld);
    }

    [Fact]
    public async Task LeaseLost_after_the_service_expired_the_lease()
    {
        // The instance must outlive the silence (six missed 5 s heartbeats would reap it at 30 s): only the lease expires.
        await using var h = await SdkHarness.Start(fakeServiceClock: true, new() { ["Instances:ReapAfterMissed"] = "100", ["Instances:SuspectAfterMissed"] = "50" });
        var o = SdkHarness.FastOptions();
        o.HeartbeatInterval = TimeSpan.FromHours(1); // one heartbeat at connect, then silence the service can see
        o.LeaseTtl = TimeSpan.FromHours(2);
        var (sdk, c) = await HubWithCharacter(h, o);
        await sdk.PumpUntil(() => sdk.Session.Connected, what: "connected");
        var lease = await Leased(sdk, c.Id);
        var lost = new List<LeaseLostReason>();
        lease.LeaseLost += (_, why, _) => lost.Add(why);

        h.ServiceClock!.Advance(TimeSpan.FromSeconds(31));
        // The service's maintenance worker (on the same fake clock) releases the expired lease and
        // sends LeaseRevoked down the stream: the SDK learns before its next write.
        await sdk.PumpUntil(() => lost.Count > 0, what: "LeaseLost");
        Assert.Equal([LeaseLostReason.Revoked], lost);
        Assert.Null(await h.Data.ReadAsync((tx, _) => tx.Leases.Get(c.Id)));
        var r = await sdk.Pumped(lease.Checkpoint(c.Sheet));
        Assert.False(r.Ok);
    }

    [Fact]
    public async Task A_silent_stream_is_replaced_and_the_new_stream_renews_the_held_lease()
    {
        await using var h = await SdkHarness.Start();
        var o = SdkHarness.FastOptions();
        o.KeepAlivePingDelay = TimeSpan.FromSeconds(1); // the handler's minimum
        o.KeepAlivePingTimeout = TimeSpan.FromSeconds(1);
        var (sdk, c) = await HubWithCharacter(h, o);
        var lease = await Leased(sdk, c.Id);
        await sdk.PumpUntil(() => sdk.Session.Connected, what: "connected");
        var lost = 0;
        lease.LeaseLost += (_, _, _) => lost++;

        // A partition the service never sees: the old connection goes silent but stays open there.
        h.Proxy.Stall();
        var stalledAt = (await h.Data.ReadAsync((tx, _) => tx.Leases.Get(c.Id)))!.Expires;
        await sdk.PumpUntil(() => sdk.Metrics.Reconnects >= 1 && sdk.Session.Connected, what: $"reconnected ({sdk.Session.LastStreamError})");
        await Pumping.Until(() => h.Data.ReadAsync((tx, _) => tx.Leases.Get(c.Id)).Result!.Expires > stalledAt, what: "the lease renewed after the reconnect");

        Assert.Equal(0, lost);
        Assert.True(lease.IsHeld);
        Assert.True((await sdk.Pumped(lease.Checkpoint(c.Sheet))).Ok);
    }
}
