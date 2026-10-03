using System.Collections.Concurrent;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using SourceSharp.Host.Abstractions;
using P = SourceSharp.Host.Proto;

namespace TF2.Descent.Service.Tests.Api;

/// <summary>D-H17: StoreService over gRPC, through the real auth, against the real store on :memory:.</summary>
public class StoreApiFacts
{
    sealed class Commands : IInstanceCommands
    {
        public ConcurrentQueue<string> Sent { get; } = new();
        public Task<bool> Send(string instanceId, InstanceCommand command, CancellationToken ct = default) { Sent.Enqueue($"{instanceId} {command}"); return Task.FromResult(true); }
    }

    static async Task<(ApiHarness H, Commands Sent)> Start()
    {
        var sent = new Commands();
        var h = await ApiHarness.Start(b => b.Services.AddSingleton<IInstanceCommands>(sent));
        return (h, sent);
    }

    static async Task<Metadata> Level(ApiHarness h, string id, string levelId)
    {
        var (_, auth) = await h.Instance(id, InstanceKind.Level, 1);
        await h.Data.WriteAsync((tx, _) => tx.Instances.Update(id, r => r with { LevelHash = LevelIds.HashOf(levelId), LevelId = levelId }));
        return auth;
    }

    static P.StoreRef Ref(P.StoreScope scope, string levelId = "") => new() { Scope = scope, LevelId = levelId };

    static P.StoreSetRequest Put(P.StoreRef r, string key, params byte[] value) => new() { Ref = r, Key = key, Value = ByteString.CopyFrom(value) };

    static async Task<string> Refused(Func<Task> call) => ApiHarness.Reason(await Assert.ThrowsAsync<RpcException>(call));

    [Fact]
    public async Task A_fresh_instance_of_the_same_level_copy_reads_what_the_last_one_wrote()
    {
        var (h, _) = await Start();
        await using var _ = h;
        await h.Store.SetAsync(Put(Ref(P.StoreScope.Level), "boss", 1), await Level(h, "lvl-a", "L1~X"));
        var got = await h.Store.GetAsync(new P.StoreGetRequest { Ref = Ref(P.StoreScope.Level), Key = "boss" }, await Level(h, "lvl-b", "L1~X"));
        Assert.True(got.Found);
        Assert.Equal([1], got.Value.ToByteArray());
    }

    [Fact]
    public async Task Another_copy_of_the_same_level_has_its_own_data()
    {
        var (h, _) = await Start();
        await using var _ = h;
        await h.Store.SetAsync(Put(Ref(P.StoreScope.Level), "boss", 1), await Level(h, "lvl-a", "L1"));
        var got = await h.Store.GetAsync(new P.StoreGetRequest { Ref = Ref(P.StoreScope.Level), Key = "boss" }, await Level(h, "lvl-b", "L1~X"));
        Assert.False(got.Found);
    }

    [Fact]
    public async Task A_level_naming_another_level_is_refused()
    {
        var (h, _) = await Start();
        await using var _ = h;
        var lvl = await Level(h, "lvl-a", "L1");
        Assert.Equal("not_your_level", await Refused(() => h.Store.SetAsync(Put(Ref(P.StoreScope.Level, "L2"), "k", 1), lvl).ResponseAsync));
    }

    [Fact]
    public async Task Town_writes_a_claimed_levels_arrival_and_the_level_reads_it()
    {
        var (h, sent) = await Start();
        await using var _ = h;
        var (_, hub) = await h.Instance("hub-1", InstanceKind.Hub);
        var lvl = await Level(h, "lvl-a", "L1~X");
        await h.Data.WriteAsync((tx, _) => tx.LevelClaims.Set("char-1", 1, "L1~X"));
        await h.Store.SetAsync(Put(Ref(P.StoreScope.Level, "L1~X"), "arrival/char-1", 7), hub);
        var got = await h.Store.GetAsync(new P.StoreGetRequest { Ref = Ref(P.StoreScope.Level), Key = "arrival/char-1" }, lvl);
        Assert.Equal([7], got.Value.ToByteArray());
        Assert.Contains("lvl-a StoreChanged { Scope = Level, Owner = L1~X, Key = arrival/char-1 }", sent.Sent);
    }

    [Fact]
    public async Task Town_naming_a_level_nothing_claims_or_runs_is_refused()
    {
        var (h, _) = await Start();
        await using var _ = h;
        var (_, hub) = await h.Instance("hub-1", InstanceKind.Hub);
        Assert.Equal("no_level", await Refused(() => h.Store.SetAsync(Put(Ref(P.StoreScope.Level, "L9"), "k", 1), hub).ResponseAsync));
    }

    [Fact]
    public async Task A_level_writing_its_own_data_tells_nobody()
    {
        var (h, sent) = await Start();
        await using var _ = h;
        await h.Instance("hub-1", InstanceKind.Hub);
        await h.Store.SetAsync(Put(Ref(P.StoreScope.Level), "k", 1), await Level(h, "lvl-a", "L1"));
        Assert.Empty(sent.Sent);
    }

    [Fact]
    public async Task Instance_data_is_the_callers_own()
    {
        var (h, _) = await Start();
        await using var _ = h;
        var (_, a) = await h.Instance("lvl-a", InstanceKind.Level, 1);
        var (_, b) = await h.Instance("lvl-b", InstanceKind.Level, 1);
        await h.Store.SetAsync(Put(Ref(P.StoreScope.Instance), "k", 1), a);
        Assert.True((await h.Store.GetAsync(new P.StoreGetRequest { Ref = Ref(P.StoreScope.Instance), Key = "k" }, a)).Found);
        Assert.False((await h.Store.GetAsync(new P.StoreGetRequest { Ref = Ref(P.StoreScope.Instance), Key = "k" }, b)).Found);
    }

    [Fact]
    public async Task Character_data_is_reached_with_the_lease()
    {
        var (h, _) = await Start();
        await using var _ = h;
        var (_, hub) = await h.Instance("hub-1", InstanceKind.Hub);
        var (id, token) = await h.LeasedCharacter(hub, hub);
        var r = new P.StoreRef { Scope = P.StoreScope.Character, CharacterId = id, LeaseToken = token };
        await h.Store.SetAsync(Put(r, "quest", 3), hub);
        Assert.Equal([3], (await h.Store.GetAsync(new P.StoreGetRequest { Ref = r, Key = "quest" }, hub)).Value.ToByteArray());
    }

    [Fact]
    public async Task Character_data_with_a_wrong_token_is_refused()
    {
        var (h, _) = await Start();
        await using var _ = h;
        var (_, hub) = await h.Instance("hub-1", InstanceKind.Hub);
        var (id, _) = await h.LeasedCharacter(hub, hub);
        var r = new P.StoreRef { Scope = P.StoreScope.Character, CharacterId = id, LeaseToken = ByteString.CopyFrom(new byte[32]) };
        Assert.Equal("stale_lease", await Refused(() => h.Store.GetAsync(new P.StoreGetRequest { Ref = r, Key = "quest" }, hub).ResponseAsync));
    }

    [Fact]
    public async Task Character_data_from_an_instance_not_holding_the_lease_is_refused()
    {
        var (h, _) = await Start();
        await using var _ = h;
        var (_, hub) = await h.Instance("hub-1", InstanceKind.Hub);
        var lvl = await Level(h, "lvl-a", "L1");
        var (id, token) = await h.LeasedCharacter(hub, hub);
        var r = new P.StoreRef { Scope = P.StoreScope.Character, CharacterId = id, LeaseToken = token };
        Assert.Equal("wrong_instance", await Refused(() => h.Store.GetAsync(new P.StoreGetRequest { Ref = r, Key = "quest" }, lvl).ResponseAsync));
    }

    [Fact]
    public async Task A_level_writing_town_tells_every_hub()
    {
        var (h, sent) = await Start();
        await using var _ = h;
        var (_, hub) = await h.Instance("hub-1", InstanceKind.Hub);
        await h.Store.SetAsync(Put(Ref(P.StoreScope.Town), "anchors/c1/view", 1), await Level(h, "lvl-a", "L1"));
        Assert.True((await h.Store.GetAsync(new P.StoreGetRequest { Ref = Ref(P.StoreScope.Town), Key = "anchors/c1/view" }, hub)).Found);
        Assert.Contains("hub-1 StoreChanged { Scope = Town, Owner = town, Key = anchors/c1/view }", sent.Sent);
    }

    [Fact]
    public async Task A_stale_compare_and_set_is_refused_with_its_reason()
    {
        var (h, _) = await Start();
        await using var _ = h;
        var (_, a) = await h.Instance("lvl-a", InstanceKind.Level, 1);
        var v = (await h.Store.SetAsync(Put(Ref(P.StoreScope.Instance), "k", 1), a)).Version;
        await h.Store.SetAsync(Put(Ref(P.StoreScope.Instance), "k", 2), a);
        var stale = Put(Ref(P.StoreScope.Instance), "k", 3);
        stale.ExpectedVersion = v;
        Assert.Equal("version_mismatch", await Refused(() => h.Store.SetAsync(stale, a).ResponseAsync));
    }

    [Fact]
    public async Task A_limit_refusal_is_audited()
    {
        var (h, _) = await Start();
        await using var _ = h;
        var (_, a) = await h.Instance("lvl-a", InstanceKind.Level, 1);
        Assert.Equal("value_too_large", await Refused(() => h.Store.SetAsync(Put(Ref(P.StoreScope.Instance), "k", new byte[64 * 1024 + 1]), a).ResponseAsync));
        var audit = await h.Data.ReadAsync((tx, _) => tx.Audit.List(new AuditQuery(Action: TF2.Descent.Service.Api.StoreApi.RefusedAction)));
        Assert.Equal("Instance:lvl-a", Assert.Single(audit).Target);
    }

    [Fact]
    public async Task A_request_without_a_scope_is_refused()
    {
        var (h, _) = await Start();
        await using var _ = h;
        var (_, a) = await h.Instance("lvl-a", InstanceKind.Level, 1);
        Assert.Equal("bad_scope", await Refused(() => h.Store.GetAsync(new P.StoreGetRequest { Ref = new P.StoreRef(), Key = "k" }, a).ResponseAsync));
    }

    [Fact]
    public async Task List_pages_through_a_prefix()
    {
        var (h, _) = await Start();
        await using var _ = h;
        var (_, a) = await h.Instance("lvl-a", InstanceKind.Level, 1);
        foreach (var k in new[] { "q/1", "q/2", "q/3", "x/1" }) await h.Store.SetAsync(Put(Ref(P.StoreScope.Instance), k, 1), a);
        var first = await h.Store.ListAsync(new P.StoreListRequest { Ref = Ref(P.StoreScope.Instance), Prefix = "q/", PageSize = 2 }, a);
        var second = await h.Store.ListAsync(new P.StoreListRequest { Ref = Ref(P.StoreScope.Instance), Prefix = "q/", PageSize = 2, PageToken = first.NextPageToken }, a);
        Assert.Equal(["q/1", "q/2", "q/3"], first.Keys.Concat(second.Keys).Select(k => k.Key));
        Assert.Equal("", second.NextPageToken);
    }
}
