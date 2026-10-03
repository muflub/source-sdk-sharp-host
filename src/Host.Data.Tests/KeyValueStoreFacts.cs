using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Data;

namespace SourceSharp.Host.Data.Tests;

/// <summary>D-H17: the key/value store on its own SQLite file, behind its cache.</summary>
public class KeyValueStoreFacts
{
    static readonly StoreOwner Lvl = new(StoreScope.Level, "lvl-a");
    static readonly StoreOwner Inst = new(StoreScope.Instance, "inst-a");

    static KeyValueStore New(StoreOptions? o = null) => new(o ?? new StoreOptions { Path = ":memory:" });

    static async Task<HostRefusal> Refused(Func<Task> call) => await Assert.ThrowsAsync<HostRefusal>(call);

    [Fact]
    public void The_store_file_defaults_to_beside_host_db() =>
        Assert.Equal(("/data/store.db", ":memory:", "/x/s.db"),
            (new StoreOptions().ResolvePath("/data/host.db"), new StoreOptions().ResolvePath(":memory:"), new StoreOptions { Path = "/x/s.db" }.ResolvePath("/data/host.db")));

    [Fact]
    public async Task A_set_value_reads_back_with_its_version()
    {
        await using var s = New();
        var v = await s.Set(Lvl, "k", [1, 2], null);
        var got = await s.Get(Lvl, "k");
        Assert.Equal([1, 2], got!.Value);
        Assert.Equal(v, got.Version);
    }

    [Fact]
    public async Task A_missing_key_reads_as_null()
    {
        await using var s = New();
        Assert.Null(await s.Get(Lvl, "nope"));
    }

    [Fact]
    public async Task Scopes_with_the_same_id_are_separate_owners()
    {
        await using var s = New();
        await s.Set(new StoreOwner(StoreScope.Level, "x"), "k", [1], null);
        Assert.Null(await s.Get(new StoreOwner(StoreScope.Instance, "x"), "k"));
    }

    [Fact]
    public async Task Create_only_is_refused_when_the_key_exists()
    {
        await using var s = New();
        Assert.True(await s.Set(Lvl, "k", [1], 0) > 0); // accepted while absent
        Assert.Equal("version_mismatch", (await Refused(() => s.Set(Lvl, "k", [2], 0))).Reason);
    }

    [Fact]
    public async Task Compare_and_set_with_a_stale_version_is_refused()
    {
        await using var s = New();
        var v1 = await s.Set(Lvl, "k", [1], null);
        await s.Set(Lvl, "k", [2], v1);
        Assert.Equal("version_mismatch", (await Refused(() => s.Set(Lvl, "k", [3], v1))).Reason);
        Assert.Equal([2], (await s.Get(Lvl, "k"))!.Value);
    }

    [Fact]
    public async Task A_retried_compare_and_set_that_landed_answers_its_version()
    {
        await using var s = New();
        var v1 = await s.Set(Lvl, "k", [1], null);
        var v2 = await s.Set(Lvl, "k", [2], v1);
        Assert.Equal(v2, await s.Set(Lvl, "k", [2], v1));
    }

    [Fact]
    public async Task A_recreated_key_never_takes_an_old_version()
    {
        await using var s = New();
        var old = await s.Set(Lvl, "k", [1], null);
        await s.Delete(Lvl, "k", null);
        await s.Set(Lvl, "k", [9], null);
        Assert.Equal("version_mismatch", (await Refused(() => s.Set(Lvl, "k", [3], old))).Reason);
    }

    [Fact]
    public async Task Deleting_a_missing_key_answers_false()
    {
        await using var s = New();
        Assert.False(await s.Delete(Lvl, "k", 5));
    }

    [Fact]
    public async Task Deleting_with_a_stale_version_is_refused_and_keeps_the_key()
    {
        await using var s = New();
        var v1 = await s.Set(Lvl, "k", [1], null);
        await s.Set(Lvl, "k", [2], null);
        Assert.Equal("version_mismatch", (await Refused(() => s.Delete(Lvl, "k", v1))).Reason);
        Assert.NotNull(await s.Get(Lvl, "k"));
    }

    [Fact]
    public async Task A_value_over_the_limit_is_refused()
    {
        await using var s = New(new StoreOptions { Path = ":memory:", MaxValueBytes = 4 });
        await s.Set(Lvl, "k", new byte[4], null);
        Assert.Equal("value_too_large", (await Refused(() => s.Set(Lvl, "k", new byte[5], null))).Reason);
    }

    [Fact]
    public async Task A_key_over_the_limit_is_refused()
    {
        await using var s = New(new StoreOptions { Path = ":memory:", MaxKeyBytes = 4 });
        await s.Set(Lvl, "abcd", [1], null);
        Assert.Equal("key_too_long", (await Refused(() => s.Set(Lvl, "abcdé", [1], null))).Reason);
    }

    [Fact]
    public async Task An_empty_key_is_refused()
    {
        await using var s = New();
        Assert.Equal("bad_key", (await Refused(() => s.Set(Lvl, "", [1], null))).Reason);
    }

    [Fact]
    public async Task A_new_key_past_the_key_count_is_refused_but_an_overwrite_is_not()
    {
        await using var s = New(new StoreOptions { Path = ":memory:", MaxKeys = 2 });
        await s.Set(Lvl, "a", [1], null);
        await s.Set(Lvl, "b", [1], null);
        Assert.Equal("store_full", (await Refused(() => s.Set(Lvl, "c", [1], null))).Reason);
        await s.Set(Lvl, "b", [2], null);
    }

    [Fact]
    public async Task Bytes_past_the_owner_total_are_refused()
    {
        await using var s = New(new StoreOptions { Path = ":memory:", MaxBytes = 10 });
        await s.Set(Lvl, "a", new byte[6], null);
        Assert.Equal("store_full", (await Refused(() => s.Set(Lvl, "b", new byte[5], null))).Reason);
        await s.Set(Lvl, "a", new byte[10], null); // replacing counts the new size, not both
    }

    [Fact]
    public async Task Town_has_its_own_limits()
    {
        await using var s = New(new StoreOptions { Path = ":memory:", MaxKeys = 1, TownMaxKeys = 3 });
        await s.Set(StoreOwner.Town, "a", [1], null);
        await s.Set(StoreOwner.Town, "b", [1], null);
        await s.Set(StoreOwner.Town, "c", [1], null);
        Assert.Equal("store_full", (await Refused(() => s.Set(StoreOwner.Town, "d", [1], null))).Reason);
    }

    [Fact]
    public async Task An_answered_set_survives_reopening_the_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kv-{Guid.NewGuid():N}.db");
        try
        {
            long v;
            await using (var s = New(new StoreOptions { Path = path })) v = await s.Set(Lvl, "k", [7], null);
            await using (var s = New(new StoreOptions { Path = path }))
            {
                var got = await s.Get(Lvl, "k");
                Assert.Equal([7], got!.Value);
                Assert.Equal(v, got.Version);
            }
        }
        finally { foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*")) File.Delete(f); }
    }

    [Fact]
    public async Task Versions_keep_rising_across_a_reopen()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kv-{Guid.NewGuid():N}.db");
        try
        {
            long old;
            await using (var s = New(new StoreOptions { Path = path }))
            {
                old = await s.Set(Lvl, "k", [1], null);
                await s.Delete(Lvl, "k", null); // the highest version is gone from kv
            }
            await using (var s = New(new StoreOptions { Path = path }))
                Assert.True(await s.Set(Lvl, "k", [2], null) > old);
        }
        finally { foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*")) File.Delete(f); }
    }

    [Fact]
    public async Task The_cache_drops_the_least_recently_used_owner_and_reads_it_back()
    {
        await using var s = New(new StoreOptions { Path = ":memory:", CacheBytes = 10 });
        await s.Set(Lvl, "k", new byte[6], null);
        await s.Set(Inst, "k", new byte[6], null); // 12 bytes: Lvl, used least recently, goes
        Assert.Equal(1, s.CachedOwners);
        Assert.Equal(6, (await s.Get(Lvl, "k"))!.Value.Length);
    }

    [Fact]
    public async Task List_answers_prefixed_keys_in_order_a_page_at_a_time()
    {
        await using var s = New();
        foreach (var k in new[] { "b/2", "a/1", "b/1", "b/3" }) await s.Set(Lvl, k, [1], null);
        var first = await s.List(Lvl, "b/", null, 2);
        var second = await s.List(Lvl, "b/", first[^1].Key, 2);
        Assert.Equal(["b/1", "b/2", "b/3"], first.Concat(second).Select(k => k.Key));
    }

    [Fact]
    public async Task Drop_removes_one_owner_only()
    {
        await using var s = New();
        await s.Set(Lvl, "a", [1], null);
        await s.Set(Lvl, "b", [1], null);
        await s.Set(new StoreOwner(StoreScope.Level, "lvl-b"), "a", [1], null);
        Assert.Equal(2, await s.Drop(Lvl));
        Assert.Equal(["lvl-b"], await s.Owners(StoreScope.Level));
    }

    [Fact]
    public async Task Copy_replaces_the_target_with_the_source_at_new_versions()
    {
        await using var s = New();
        var to = new StoreOwner(StoreScope.Level, "lvl-a~1");
        var v = await s.Set(Lvl, "a", [1], null);
        await s.Set(to, "stale", [9], null);
        Assert.Equal(1, await s.Copy(Lvl, to));
        var got = await s.Get(to, "a");
        Assert.Equal([1], got!.Value);
        Assert.True(got.Version > v);
        Assert.Null(await s.Get(to, "stale"));
    }
}
