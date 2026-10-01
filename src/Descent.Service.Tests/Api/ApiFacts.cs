using Google.Protobuf;
using Grpc.Core;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Testing;
using P = SourceSharp.Host.Proto;

namespace Descent.Service.Tests.Api;

/// <summary>Plan §6 gate: in-process gRPC facts per RPC, including the refusals.</summary>
public class ApiFacts
{
    [Fact]
    public async Task A_call_without_a_token_is_unauthenticated()
    {
        await using var h = await ApiHarness.Start();
        var e = await Assert.ThrowsAsync<RpcException>(() => h.Characters.GetSheetAsync(new P.GetSheetRequest { CharacterId = "x" }).ResponseAsync);
        Assert.Equal(StatusCode.Unauthenticated, e.StatusCode);
    }

    [Fact]
    public async Task A_call_with_a_wrong_token_is_unauthenticated()
    {
        await using var h = await ApiHarness.Start();
        await h.Instance("hub-1", InstanceKind.Hub);
        var bad = new Metadata { { "x-instance-id", "hub-1" }, { "x-instance-token", "not-it" } };
        var e = await Assert.ThrowsAsync<RpcException>(() => h.Characters.ListAsync(new P.ListCharactersRequest { Account = "1" }, bad).ResponseAsync);
        Assert.Equal(StatusCode.Unauthenticated, e.StatusCode);
    }

    [Fact]
    public async Task The_hub_creates_and_lists_characters()
    {
        await using var h = await ApiHarness.Start();
        var (_, hub) = await h.Instance("hub-1", InstanceKind.Hub);
        await h.Characters.CreateAsync(new P.CreateCharacterRequest { RequestId = h.Req(), Account = "7", ClassName = "heavy", Name = "Hoss" }, hub);
        var list = await h.Characters.ListAsync(new P.ListCharactersRequest { Account = "7" }, hub);
        var c = Assert.Single(list.Characters);
        Assert.Equal(("heavy", "Hoss", 1), (c.ClassName, c.Name, c.Level));
    }

    [Fact]
    public async Task A_level_lists_only_the_characters_that_claim_its_level()
    {
        await using var h = await ApiHarness.Start();
        var (_, hub) = await h.Instance("hub-1", InstanceKind.Hub);
        var (_, lvl) = await h.Instance("lvl-1", InstanceKind.Level, 3);
        await h.Data.WriteAsync((tx, _) => tx.Instances.Update("lvl-1", r => r with { LevelHash = "L3" }));
        var here = await h.Characters.CreateAsync(new P.CreateCharacterRequest { RequestId = h.Req(), Account = "7", ClassName = "scout", Name = "Ann" }, hub);
        var elsewhere = await h.Characters.CreateAsync(new P.CreateCharacterRequest { RequestId = h.Req(), Account = "7", ClassName = "heavy", Name = "Bo" }, hub);
        await h.Data.WriteAsync(async (tx, _) =>
        {
            await tx.LevelClaims.Set(here.Id, 3, "L3");
            await tx.LevelClaims.Set(elsewhere.Id, 3, "L-other");
            return true;
        });
        var r = await h.Characters.ListAsync(new P.ListCharactersRequest { Account = "7" }, lvl);
        Assert.Equal([here.Id], r.Characters.Select(c => c.Id));
    }

    [Fact]
    public async Task A_level_may_not_list_characters()
    {
        await using var h = await ApiHarness.Start();
        var (_, lvl) = await h.Instance("lvl-1", InstanceKind.Level, 3);
        var e = await Assert.ThrowsAsync<RpcException>(() => h.Characters.ListAsync(new P.ListCharactersRequest { Account = "7" }, lvl).ResponseAsync);
        Assert.Equal((StatusCode.PermissionDenied, "not_a_hub"), (e.StatusCode, ApiHarness.Reason(e)));
    }

    [Fact]
    public async Task A_second_instance_cannot_lease_a_leased_character()
    {
        await using var h = await ApiHarness.Start();
        var (_, hub) = await h.Instance("hub-1", InstanceKind.Hub);
        var (_, lvl) = await h.Instance("lvl-1", InstanceKind.Level, 3);
        var (id, _) = await h.LeasedCharacter(hub, hub);
        var e = await Assert.ThrowsAsync<RpcException>(() => h.Characters.LeaseAsync(new P.LeaseRequest { RequestId = h.Req(), CharacterId = id }, lvl).ResponseAsync);
        Assert.Equal("already_leased", ApiHarness.Reason(e));
    }

    [Fact]
    public async Task A_write_from_an_instance_that_does_not_hold_the_lease_is_refused()
    {
        await using var h = await ApiHarness.Start();
        var (_, hub) = await h.Instance("hub-1", InstanceKind.Hub);
        var (_, lvl) = await h.Instance("lvl-1", InstanceKind.Level, 3);
        var (id, token) = await h.LeasedCharacter(hub, hub);
        var e = await Assert.ThrowsAsync<RpcException>(() => h.Items.TakeReserveAsync(new P.TakeReserveRequest
        {
            RequestId = h.Req(), CharacterId = id, LeaseToken = token, Wanted = { new P.TierCount { Tier = 0, Count = 1 } },
        }, lvl).ResponseAsync);
        Assert.Equal((StatusCode.PermissionDenied, "wrong_instance"), (e.StatusCode, ApiHarness.Reason(e)));
    }

    [Fact]
    public async Task A_stale_lease_token_is_refused()
    {
        await using var h = await ApiHarness.Start();
        var (_, hub) = await h.Instance("hub-1", InstanceKind.Hub);
        var (id, _) = await h.LeasedCharacter(hub, hub);
        var e = await Assert.ThrowsAsync<RpcException>(() => h.Characters.SetReachedDepthAsync(new P.SetReachedDepthRequest
        {
            RequestId = h.Req(), CharacterId = id, LeaseToken = ByteString.CopyFrom(new byte[32]), Depth = 2,
        }, hub).ResponseAsync);
        Assert.Equal((StatusCode.FailedPrecondition, "stale_lease"), (e.StatusCode, ApiHarness.Reason(e)));
    }

    [Fact]
    public async Task A_duplicated_mutating_call_replays_the_first_answer()
    {
        await using var h = await ApiHarness.Start();
        var (_, hub) = await h.Instance("hub-1", InstanceKind.Hub);
        var (_, lvl) = await h.Instance("lvl-1", InstanceKind.Level, 3);
        var (id, _) = await h.LeasedCharacter(hub, hub);
        await h.Characters.ReleaseAsync(new P.ReleaseRequest { RequestId = h.Req(), CharacterId = id, LeaseToken = (await h.Characters.LeaseAsync(new P.LeaseRequest { RequestId = h.Req(), CharacterId = id }, hub)).Token }, hub);
        var lease = await h.Characters.LeaseAsync(new P.LeaseRequest { RequestId = h.Req(), CharacterId = id }, lvl);
        var req = new P.TakeReserveRequest { RequestId = "same", CharacterId = id, LeaseToken = lease.Token, Wanted = { new P.TierCount { Tier = 0, Count = 3 } } };
        var first = await h.Items.TakeReserveAsync(req, lvl);
        var second = await h.Items.TakeReserveAsync(req, lvl);
        Assert.Equal(first.Items.Select(i => i.Id), second.Items.Select(i => i.Id));
        Assert.Equal(3, await h.Data.ReadAsync((tx, _) => tx.Items.Count(new ItemQuery(OwnerKind: OwnerKind.Reserve))));
    }

    [Fact]
    public async Task A_claim_into_a_full_backpack_is_refused_for_capacity()
    {
        await using var h = await ApiHarness.Start();
        var (_, hub) = await h.Instance("hub-1", InstanceKind.Hub);
        var (lvlRow, lvl) = await h.Instance("lvl-1", InstanceKind.Level, 3);
        var ch = await h.Characters.CreateAsync(new P.CreateCharacterRequest { RequestId = h.Req(), Account = "1", ClassName = "scout", Name = "Ann" }, hub);
        var lease = await h.Characters.LeaseAsync(new P.LeaseRequest { RequestId = h.Req(), CharacterId = ch.Id }, lvl);
        var world = await h.Data.WriteAsync(async (tx, _) =>
        {
            for (var s = 0; s < h.Rules.Backpack; s++)
                await tx.Items.Mint(TestItem(h), OwnerKind.Character, ch.Id, null, s, "test", null);
            return await tx.Items.Mint(TestItem(h), OwnerKind.World, lvlRow.Id, lvlRow.Id, -1, "test", null);
        });
        var e = await Assert.ThrowsAsync<RpcException>(() => h.Items.ClaimAsync(new P.ItemActionRequest
        {
            RequestId = h.Req(), CharacterId = ch.Id, LeaseToken = lease.Token, ItemId = world.Id,
        }, lvl).ResponseAsync);
        Assert.Equal("no_capacity", ApiHarness.Reason(e));
    }

    [Fact]
    public async Task A_checkpoint_is_refused_when_a_carried_item_does_not_replay()
    {
        await using var h = await ApiHarness.Start();
        var (_, hub) = await h.Instance("hub-1", InstanceKind.Hub);
        var ch = await h.Characters.CreateAsync(new P.CreateCharacterRequest { RequestId = h.Req(), Account = "1", ClassName = "scout", Name = "Ann" }, hub);
        var lease = await h.Characters.LeaseAsync(new P.LeaseRequest { RequestId = h.Req(), CharacterId = ch.Id }, hub);
        // An item whose instance says rarity 3 but whose seed rolls something else: a forgery.
        var honest = h.Rules.RollItem(new SourceSharp.Host.Contracts.ItemRollContext("scout", 1, 1, 5, 0, "depth1", "test"), 11);
        var forged = honest with { Rarity = 3 };
        await h.Data.WriteAsync((tx, _) => tx.Items.Mint(new NewItem(forged.Seed, forged.BaseType, forged.Rarity, forged.ItemLevel, 1, true,
            FakeGameRules.ItemCodec.Encode(forged), 1, 0, 1), OwnerKind.Character, ch.Id, null, 0, "test", null));
        var e = await Assert.ThrowsAsync<RpcException>(() => h.Characters.CheckpointAsync(new P.CheckpointRequest
        {
            RequestId = h.Req(), CharacterId = ch.Id, LeaseToken = lease.Token, Sheet = ch.Sheet, ExpectedVersion = ch.Version,
        }, hub).ResponseAsync);
        Assert.Equal("replay_mismatch", ApiHarness.Reason(e));
    }

    [Fact]
    public async Task A_checkpoint_names_the_first_failing_rule()
    {
        await using var h = await ApiHarness.Start();
        var (_, hub) = await h.Instance("hub-1", InstanceKind.Hub);
        var ch = await h.Characters.CreateAsync(new P.CreateCharacterRequest { RequestId = h.Req(), Account = "1", ClassName = "scout", Name = "Ann" }, hub);
        var lease = await h.Characters.LeaseAsync(new P.LeaseRequest { RequestId = h.Req(), CharacterId = ch.Id }, hub);
        var jump = FakeGameRules.SheetCodec.Write(new FakeGameRules.Sheet("scout", "Ann", 30, 1, 0, false, false));
        var e = await Assert.ThrowsAsync<RpcException>(() => h.Characters.CheckpointAsync(new P.CheckpointRequest
        {
            RequestId = h.Req(), CharacterId = ch.Id, LeaseToken = lease.Token, ExpectedVersion = ch.Version,
            Sheet = new P.Blob { Data = ByteString.CopyFrom(jump.Data), SchemaVersion = 1 },
        }, hub).ResponseAsync);
        Assert.Equal("level_jump", ApiHarness.Reason(e));
    }

    [Fact]
    public async Task A_valid_checkpoint_updates_the_summary_columns()
    {
        await using var h = await ApiHarness.Start();
        var (_, hub) = await h.Instance("hub-1", InstanceKind.Hub);
        var ch = await h.Characters.CreateAsync(new P.CreateCharacterRequest { RequestId = h.Req(), Account = "1", ClassName = "scout", Name = "Ann" }, hub);
        var lease = await h.Characters.LeaseAsync(new P.LeaseRequest { RequestId = h.Req(), CharacterId = ch.Id }, hub);
        var next = FakeGameRules.SheetCodec.Write(new FakeGameRules.Sheet("scout", "Ann", 3, 900, 2, false, false));
        var r = await h.Characters.CheckpointAsync(new P.CheckpointRequest
        {
            RequestId = h.Req(), CharacterId = ch.Id, LeaseToken = lease.Token, ExpectedVersion = ch.Version,
            Sheet = new P.Blob { Data = ByteString.CopyFrom(next.Data), SchemaVersion = 1 },
        }, hub);
        var after = await h.Characters.GetSheetAsync(new P.GetSheetRequest { CharacterId = ch.Id }, hub);
        Assert.Equal((r.Version, 3, 900L, 2), (after.Version, after.Level, after.Xp, after.ReachedDepth));
    }

    [Fact]
    public async Task A_forged_reveal_through_the_api_destroys_the_item()
    {
        await using var h = await ApiHarness.Start();
        var (_, hub) = await h.Instance("hub-1", InstanceKind.Hub);
        var (lvlRow, lvl) = await h.Instance("lvl-1", InstanceKind.Level, 3);
        var ch = await h.Characters.CreateAsync(new P.CreateCharacterRequest { RequestId = h.Req(), Account = "1", ClassName = "scout", Name = "Ann" }, hub);
        var lease = await h.Characters.LeaseAsync(new P.LeaseRequest { RequestId = h.Req(), CharacterId = ch.Id }, lvl);
        var reserve = await h.Items.TakeReserveAsync(new P.TakeReserveRequest { RequestId = h.Req(), CharacterId = ch.Id, LeaseToken = lease.Token, Wanted = { new P.TierCount { Tier = 0, Count = 1 } } }, lvl);
        uint noDrop = 0;
        while (h.Rules.KillRoll(lvlRow.Seed, noDrop, "heavy", 3, 1).Drop) noDrop++;
        var e = await Assert.ThrowsAsync<RpcException>(() => h.Items.RevealAsync(new P.RevealRequest
        {
            RequestId = h.Req(), CharacterId = ch.Id, LeaseToken = lease.Token, ItemId = reserve.Items[0].Id, KillSeq = noDrop, RobotTemplate = "heavy",
        }, lvl).ResponseAsync);
        Assert.Equal("forged_reveal", ApiHarness.Reason(e));
        var item = await h.Data.ReadAsync((tx, _) => tx.Items.Get(reserve.Items[0].Id));
        Assert.Equal(ItemState.Destroyed, item!.State);
    }

    [Fact]
    public async Task Booting_refuses_an_unsupported_sdk_naming_both_versions()
    {
        await using var h = await ApiHarness.Start();
        var (_, lvl) = await h.Instance("lvl-1", InstanceKind.Level, 3);
        var e = await Assert.ThrowsAsync<RpcException>(() => h.Instances.BootingAsync(new P.BootingRequest
        {
            RequestId = h.Req(), SdkVersion = "9.0.0", Module = new P.RulesModule { Sha256 = "abc" },
        }, lvl).ResponseAsync);
        Assert.Equal(StatusCode.Unimplemented, e.StatusCode);
        Assert.Contains("9.0.0", e.Status.Detail);
    }

    [Fact]
    public async Task Booting_an_instance_that_is_already_live_is_a_named_refusal()
    {
        await using var h = await ApiHarness.Start();
        var (_, lvl) = await h.Instance("lvl-1", InstanceKind.Level, 3);
        var e = await Assert.ThrowsAsync<RpcException>(() => h.Instances.BootingAsync(new P.BootingRequest
        {
            RequestId = h.Req(), SdkVersion = "1.0.0", Module = new P.RulesModule { Sha256 = "abc", ContractVersion = "1.0.0" },
        }, lvl).ResponseAsync);
        Assert.Equal((StatusCode.FailedPrecondition, "not_booting"), (e.StatusCode, ApiHarness.Reason(e)));
    }

    [Fact]
    public async Task Metrics_time_rpcs_and_count_instances_by_state()
    {
        await using var h = await ApiHarness.Start();
        var (_, hub) = await h.Instance("hub-1", InstanceKind.Hub);
        await h.Characters.ListAsync(new P.ListCharactersRequest { Account = "1" }, hub);
        var text = await h.Service.Http(ListenerRole.Internal).GetStringAsync("/metrics");
        Assert.Contains("descent_rpc_seconds_count{method=\"/descent.host.v1.CharacterService/List\",status=\"OK\"}", text);
        Assert.Contains("descent_instances{kind=\"Hub\",state=\"Live\"} 1", text);
    }

    static NewItem TestItem(ApiHarness h)
    {
        var r = h.Rules.RollItem(new SourceSharp.Host.Contracts.ItemRollContext("scout", 1, 1, 5, 0, "depth1", "test"), (ulong)Random.Shared.NextInt64());
        return new NewItem(r.Seed, r.BaseType, r.Rarity, r.ItemLevel, 1, true, r.Instance, 1, 0, 1);
    }
}
