using System.Text;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Testing;

namespace SourceSharp.Host.Data.Tests;

public class IdempotencyFacts
{
    [Fact]
    public async Task A_duplicated_request_replays_the_stored_bytes_and_runs_once()
    {
        await using var d = new TestData();
        var runs = 0;
        Task<byte[]> Work(IWriteTx tx, CancellationToken _) { runs++; return Task.FromResult(Encoding.UTF8.GetBytes($"run {runs}")); }
        var first = await d.Data.IdempotentAsync("req-1", "inst-a", Work);
        var second = await d.Data.IdempotentAsync("req-1", "inst-a", Work);
        Assert.Equal((1, false, true), (runs, first.Replayed, second.Replayed));
        Assert.Equal(first.Response, second.Response);
    }

    [Fact]
    public async Task A_refused_request_stores_nothing_so_a_retry_runs_again()
    {
        await using var d = new TestData();
        var runs = 0;
        await Assert.ThrowsAsync<HostRefusal>(() => d.Data.IdempotentAsync("req-1", null, (_, _) =>
        {
            runs++;
            throw HostRefusal.Precondition("no_capacity", "full");
        }));
        await d.Data.IdempotentAsync("req-1", null, (_, _) => { runs++; return Task.FromResult(new byte[] { 1 }); });
        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task A_refusal_rolls_back_the_writes_made_before_it()
    {
        await using var d = new TestData();
        await Assert.ThrowsAsync<HostRefusal>(() => d.Data.IdempotentAsync("req-1", null, async (tx, _) =>
        {
            await tx.Items.Mint(TestData.Item(), OwnerKind.World, "inst-a", "inst-a", -1, "test", "req-1");
            throw HostRefusal.Precondition("no_capacity", "full");
        }));
        Assert.Equal(0, await d.Read(tx => tx.Items.Count(new ItemQuery())));
    }

    [Fact]
    public async Task After_retention_the_same_request_is_a_fresh_call()
    {
        await using var d = new TestData();
        var runs = 0;
        Task<byte[]> Work(IWriteTx tx, CancellationToken _) { runs++; return Task.FromResult(new byte[] { (byte)runs }); }
        await d.Data.IdempotentAsync("req-1", null, Work);
        d.Clock.Advance(TimeSpan.FromHours(25));
        Assert.Equal(1, await d.Data.PurgeIdempotencyAsync(d.Clock.GetUtcNow() - TimeSpan.FromHours(24)));
        var again = await d.Data.IdempotentAsync("req-1", null, Work);
        Assert.Equal((2, false), (runs, again.Replayed));
    }

    [Fact]
    public async Task A_missing_request_id_is_refused() =>
        await Assert.ThrowsAsync<HostRefusal>(async () =>
        {
            await using var d = new TestData();
            await d.Data.IdempotentAsync("", null, (_, _) => Task.FromResult(Array.Empty<byte>()));
        });
}

public class CharacterFacts
{
    [Fact]
    public async Task An_update_at_a_stale_version_is_refused()
    {
        await using var d = new TestData();
        var ch = await d.Write(tx => tx.Characters.Create("1", "scout", "Ann", false, [1], 1, 1, 0));
        await d.Write(tx => tx.Characters.Update(ch.Id, ch.Version, c => c with { Level = 2 }));
        var e = await Assert.ThrowsAsync<HostRefusal>(() => d.Write(tx => tx.Characters.Update(ch.Id, ch.Version, c => c with { Level = 3 })));
        Assert.Equal("version_conflict", e.Reason);
    }

    [Fact]
    public async Task Australium_cannot_go_below_zero()
    {
        await using var d = new TestData();
        var ch = await d.Write(tx => tx.Characters.Create("1", "scout", "Ann", false, [1], 1, 1, 0));
        await d.Write(tx => tx.Characters.AddAustralium(ch.Id, 5, "kill", null));
        var e = await Assert.ThrowsAsync<HostRefusal>(() => d.Write(tx => tx.Characters.AddAustralium(ch.Id, -6, "buy", null)));
        Assert.Equal("insufficient_funds", e.Reason);
    }

    [Fact]
    public async Task The_cached_australium_equals_its_ledger()
    {
        await using var d = new TestData();
        var ch = await d.Write(tx => tx.Characters.Create("1", "scout", "Ann", false, [1], 1, 1, 0));
        await d.Write(tx => tx.Characters.AddAustralium(ch.Id, 10, "kill", null));
        await d.Write(tx => tx.Characters.AddAustralium(ch.Id, -4, "buy", "item-1"));
        var cached = (await d.Read(tx => tx.Characters.Get(ch.Id)))!.Australium;
        Assert.Equal((6L, 6L), (cached, await d.Read(tx => tx.Characters.AustraliumLedgerSum(ch.Id))));
    }

    [Fact]
    public async Task A_character_creates_its_account()
    {
        await using var d = new TestData();
        await d.Write(tx => tx.Characters.Create("765", "scout", "Ann", false, [1], 1, 1, 0));
        Assert.NotNull(await d.Read(tx => tx.Characters.GetAccount("765")));
    }

    [Fact]
    public async Task A_read_transaction_refuses_a_write() =>
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var d = new TestData();
            await d.Read(tx => tx.Characters.Create("1", "scout", "Ann", false, [1], 1, 1, 0));
        });
}

public class PoolStoreFacts
{
    static LevelRecord Level(string hash, int depth, long pack, DateTimeOffset at) =>
        new(hash, pack, depth, "tiles", 7, 0, LevelState.Ready, at, null, 100, null, null);

    [Fact]
    public async Task A_ready_level_is_handed_out_once_ever()
    {
        await using var d = new TestData();
        await d.Write(tx => tx.Levels.Add(Level("h1", 1, 1, d.Clock.GetUtcNow())));
        await d.Write(tx => tx.Levels.Add(Level("h2", 1, 1, d.Clock.GetUtcNow().AddSeconds(1))));
        var a = await d.Write(tx => tx.Levels.HandOut(1, 1, "inst-a"));
        var b = await d.Write(tx => tx.Levels.HandOut(1, 1, "inst-b"));
        var c = await d.Write(tx => tx.Levels.HandOut(1, 1, "inst-c"));
        Assert.Equal(("h1", "h2"), (a!.Hash, b!.Hash));
        Assert.Null(c);
        Assert.Equal("inst-a", (await d.Read(tx => tx.Levels.Get("h1")))!.HandedTo);
    }

    [Fact]
    public async Task Concurrent_hand_outs_get_different_levels()
    {
        await using var d = new TestData();
        for (var i = 0; i < 4; i++)
        {
            var at = d.Clock.GetUtcNow().AddSeconds(i);
            await d.Write(tx => tx.Levels.Add(Level($"h{i}", 2, 1, at)));
        }
        var got = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => d.Write(tx => tx.Levels.HandOut(2, 1, $"inst-{i}"))));
        Assert.Equal(4, got.Select(l => l!.Hash).Distinct().Count());
    }

    [Fact]
    public async Task A_bake_is_taken_before_a_higher_priority_link()
    {
        await using var d = new TestData();
        await d.Write(tx => tx.MapJobs.Enqueue(MapJobKind.Link, "link-1", 0));
        await d.Write(tx => tx.MapJobs.Enqueue(MapJobKind.Bake, "bake-1", 3));
        Assert.Equal(MapJobKind.Bake, (await d.Write(tx => tx.MapJobs.TakeNext()))!.Kind);
    }

    [Fact]
    public async Task Links_are_taken_by_priority_then_age()
    {
        await using var d = new TestData();
        await d.Write(tx => tx.MapJobs.Enqueue(MapJobKind.Link, "late-3", 3));
        d.Clock.Advance(TimeSpan.FromSeconds(1));
        await d.Write(tx => tx.MapJobs.Enqueue(MapJobKind.Link, "next-1", 1));
        d.Clock.Advance(TimeSpan.FromSeconds(1));
        await d.Write(tx => tx.MapJobs.Enqueue(MapJobKind.Link, "later-1", 1));
        var order = new List<string>();
        for (var i = 0; i < 3; i++) order.Add((await d.Write(tx => tx.MapJobs.TakeNext()))!.Key);
        Assert.Equal(["next-1", "later-1", "late-3"], order);
    }

    [Fact]
    public async Task A_queued_key_is_not_queued_twice()
    {
        await using var d = new TestData();
        var a = await d.Write(tx => tx.MapJobs.Enqueue(MapJobKind.Link, "k", 3));
        var b = await d.Write(tx => tx.MapJobs.Enqueue(MapJobKind.Link, "k", 1));
        Assert.Equal((a.Id, 1), (b.Id, b.Priority));
        Assert.Single(await d.Read(tx => tx.MapJobs.List()));
    }

    [Fact]
    public async Task Pack_versions_count_up_per_mod_and_library()
    {
        await using var d = new TestData();
        PackRecord Pack(string lib, int v) => new(0, "descent", lib, v, "p", "lv", "upload", "admin", d.Clock.GetUtcNow(), 1, "sha", null, null, PackState.Ready);
        Assert.Equal(1, await d.Read(tx => tx.Library.NextVersion("descent", "gravel")));
        await d.Write(tx => tx.Library.AddPack(Pack("gravel", 1)));
        await d.Write(tx => tx.Library.AddPack(Pack("gravel", 2)));
        Assert.Equal((3, 1), (await d.Read(tx => tx.Library.NextVersion("descent", "gravel")), await d.Read(tx => tx.Library.NextVersion("descent", "brick"))));
    }
}

public class SessionAndPinFacts
{
    [Fact]
    public async Task A_replaced_pin_is_kept_as_previous_so_old_log_lines_resolve()
    {
        await using var d = new TestData();
        var now = d.Clock.GetUtcNow();
        await d.Write(tx => tx.Sessions.SetPin(new PlayerPin("765", "10.0.0.5:40001", "PortPerPlayer", now, now, [])));
        var pin = await d.Write(tx => tx.Sessions.SetPin(new PlayerPin("765", "10.0.0.5:40002", "PortPerPlayer", now, now, [])));
        Assert.Equal(["10.0.0.5:40001"], pin.Previous);
    }

    [Fact]
    public async Task A_closed_session_is_not_open()
    {
        await using var d = new TestData();
        var now = d.Clock.GetUtcNow();
        await d.Write(tx => tx.Sessions.Upsert(new SessionRecord("s1", "1.2.3.4:5", "765", null, "10.42.0.9:27015", "10.42.0.3:40001", "open", now, now, now)));
        await d.Write(tx => tx.Sessions.Close("s1", "expired"));
        Assert.Empty(await d.Read(tx => tx.Sessions.Open()));
        Assert.NotNull(await d.Read(tx => tx.Sessions.ByPeer("10.42.0.3:40001")));
    }
}

public class AuditFacts
{
    [Fact]
    public async Task An_audit_row_carries_actor_action_and_both_sides()
    {
        await using var d = new TestData();
        await d.Data.WriteAsActorAsync("admin@localhost", (tx, _) => tx.Audit.Write("character.edit", "ch1", new { level = 1 }, new { level = 2 }).ContinueWith(_ => true));
        var row = Assert.Single(await d.Read(tx => tx.Audit.List(new AuditQuery())));
        Assert.Equal(("admin@localhost", "character.edit", "ch1", "{\"level\":1}", "{\"level\":2}"), (row.Actor, row.Action, row.Target, row.BeforeJson, row.AfterJson));
    }
}

public class FileDatabaseFacts
{
    static string TempDb() => Path.Combine(Path.GetTempPath(), $"host-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task A_file_database_runs_in_wal_mode()
    {
        var path = TempDb();
        try
        {
            await using (var d = new TestData(path: path))
                await d.Write(tx => tx.Characters.Create("1", "scout", "Ann", false, [1], 1, 1, 0));
            await using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode";
            Assert.Equal("wal", (string?)await cmd.ExecuteScalarAsync());
        }
        finally { foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*")) File.Delete(f); }
    }

    /// <summary>Holds a read open (its snapshot taken) until <paramref name="release"/> completes.</summary>
    static Task HoldRead(TestData d, TaskCompletionSource entered, Task release) =>
        d.Data.ReadAsync(async (tx, _) =>
        {
            await tx.Characters.ListAll();
            entered.TrySetResult();
            await release;
            return true;
        });

    [Fact]
    public async Task A_write_commits_while_a_read_is_open()
    {
        var path = TempDb();
        try
        {
            await using var d = new TestData(path: path);
            var entered = new TaskCompletionSource();
            var release = new TaskCompletionSource();
            var read = HoldRead(d, entered, release.Task);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var write = d.Write(tx => tx.Characters.Create("1", "scout", "Ann", false, [1], 1, 1, 0));
            var done = await Task.WhenAny(write, Task.Delay(TimeSpan.FromSeconds(5)));
            release.SetResult();
            await read;
            Assert.Same(write, done);
            await write;
        }
        finally { foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*")) File.Delete(f); }
    }

    [Fact]
    public async Task Two_reads_are_open_at_once()
    {
        var path = TempDb();
        try
        {
            await using var d = new TestData(path: path);
            await d.Write(tx => tx.Characters.Create("1", "scout", "Ann", false, [1], 1, 1, 0));
            var release = new TaskCompletionSource();
            TaskCompletionSource first = new(), second = new();
            var a = HoldRead(d, first, release.Task);
            await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var b = HoldRead(d, second, release.Task);
            var done = await Task.WhenAny(second.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            release.SetResult();
            await Task.WhenAll(a, b);
            Assert.Same(second.Task, done);
        }
        finally { foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*")) File.Delete(f); }
    }

    [Fact]
    public async Task A_backup_is_a_database_holding_the_same_rows()
    {
        var path = TempDb();
        var backup = path + ".bak";
        try
        {
            await using (var d = new TestData(path: path))
            {
                await d.Write(tx => tx.Characters.Create("1", "scout", "Ann", false, [1], 1, 1, 0));
                await d.Data.BackupAsync(backup);
            }
            await using var restored = new TestData(path: backup);
            Assert.Single(await restored.Read(tx => tx.Characters.ListAll()));
        }
        finally { foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*")) File.Delete(f); }
    }
}
