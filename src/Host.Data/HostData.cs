using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SourceSharp.Host.Abstractions;

namespace SourceSharp.Host.Data;

public sealed class HostDataOptions
{
    /// <summary>A file path, or ":memory:" for a private in-memory database (tests).</summary>
    public string Path { get; set; } = "host.db";
    public string DefaultActor { get; set; } = "service";
}

/// <summary>
/// The single writer of plan §4.2: every write is queued on a Channel and run, one at a
/// time, on one long-lived connection inside one transaction. Microsoft.Data.Sqlite's
/// async is synchronous underneath, so a burst of Reveals from many pods queues here
/// instead of contending on the file lock. Reads open their own connection (WAL gives
/// them a snapshot); an in-memory database has one connection, so its reads queue too.
/// </summary>
public sealed class HostData : IHostData, IAsyncDisposable
{
    readonly TimeProvider _clock;
    readonly HostDataOptions _options;
    readonly SqliteConnection _writeConnection;
    readonly string _readConnectionString;
    readonly bool _memory;
    readonly Channel<Func<Task>> _queue = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });
    readonly Task _pump;
    long _queued;

    public HostData(HostDataOptions options, TimeProvider clock)
    {
        _options = options;
        _clock = clock;
        _memory = options.Path == ":memory:";
        var cs = _memory
            ? new SqliteConnectionStringBuilder { DataSource = ":memory:" }.ToString()
            : new SqliteConnectionStringBuilder { DataSource = options.Path, Pooling = true, DefaultTimeout = 30 }.ToString();
        _readConnectionString = cs;
        _writeConnection = new SqliteConnection(cs);
        _writeConnection.Open();
        using (var cmd = _writeConnection.CreateCommand())
        {
            cmd.CommandText = _memory
                ? "PRAGMA foreign_keys=OFF;"
                : "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=30000; PRAGMA foreign_keys=OFF;";
            cmd.ExecuteNonQuery();
        }
        using (var db = NewContext(_writeConnection))
            db.Database.Migrate(); // migrations, not EnsureCreated: an existing host.db is upgraded in place (§12)
        _pump = Task.Run(Pump);
    }

    /// <summary>Writes waiting on the writer: the DB write-queue depth metric (§12).</summary>
    public long QueueDepth => Interlocked.Read(ref _queued);

    HostDbContext NewContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<HostDbContext>().UseSqlite(connection).Options);

    async Task Pump()
    {
        await foreach (var work in _queue.Reader.ReadAllAsync())
        {
            Interlocked.Decrement(ref _queued);
            await work();
        }
    }

    Task<T> Enqueue<T>(Func<Task<T>> work, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Increment(ref _queued);
        if (!_queue.Writer.TryWrite(async () =>
            {
                if (ct.IsCancellationRequested) { tcs.TrySetCanceled(ct); return; }
                try { tcs.TrySetResult(await work()); }
                catch (Exception e) { tcs.TrySetException(e); }
            }))
        {
            Interlocked.Decrement(ref _queued);
            throw new ObjectDisposedException(nameof(HostData));
        }
        return tcs.Task;
    }

    public Task<T> WriteAsync<T>(Func<IWriteTx, CancellationToken, Task<T>> work, CancellationToken ct = default) =>
        WriteAsActorAsync(null, work, ct);

    public Task<T> WriteAsActorAsync<T>(string? actor, Func<IWriteTx, CancellationToken, Task<T>> work, CancellationToken ct = default) =>
        Enqueue(async () =>
        {
            await using var db = NewContext(_writeConnection);
            await using var tx = await db.Database.BeginTransactionAsync(CancellationToken.None);
            var result = await work(new Tx(db, _clock, actor ?? _options.DefaultActor, writable: true), ct);
            await tx.CommitAsync(CancellationToken.None);
            return result;
        }, ct);

    public Task<IdempotentResult> IdempotentAsync(string requestId, string? instanceId,
        Func<IWriteTx, CancellationToken, Task<byte[]>> work, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(requestId))
            throw new HostRefusal(RefusalCode.InvalidArgument, "missing_request_id", "every mutating request carries request_id");
        return Enqueue(async () =>
        {
            await using var db = NewContext(_writeConnection);
            var hit = await db.Idempotency.AsNoTracking().FirstOrDefaultAsync(r => r.RequestId == requestId, CancellationToken.None);
            if (hit is not null)
                return new IdempotentResult(hit.Response, Replayed: true);
            await using var tx = await db.Database.BeginTransactionAsync(CancellationToken.None);
            var response = await work(new Tx(db, _clock, instanceId ?? _options.DefaultActor, writable: true), ct);
            db.ChangeTracker.Clear();
            db.Idempotency.Add(new IdempotencyRow { RequestId = requestId, InstanceId = instanceId, Response = response, At = _clock.GetUtcNow() });
            await db.SaveChangesAsync(CancellationToken.None);
            await tx.CommitAsync(CancellationToken.None);
            return new IdempotentResult(response, Replayed: false);
        }, ct);
    }

    public Task<T> ReadAsync<T>(Func<IReadTx, CancellationToken, Task<T>> work, CancellationToken ct = default)
    {
        if (_memory)
            return Enqueue(async () =>
            {
                await using var db = NewContext(_writeConnection);
                return await work(new Tx(db, _clock, _options.DefaultActor, writable: false), ct);
            }, ct);
        return ReadOnOwnConnection(work, ct);
    }

    async Task<T> ReadOnOwnConnection<T>(Func<IReadTx, CancellationToken, Task<T>> work, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(_readConnectionString);
        await connection.OpenAsync(ct);
        // One snapshot for the whole read. Deferred: EF's BeginTransaction is BEGIN IMMEDIATE on
        // SQLite, which takes the write lock, so a read held open stalled the writer and every
        // other read for the busy timeout ("database is locked", found by h4 on k3s).
        await using var sqliteTx = connection.BeginTransaction(deferred: true);
        await using var db = NewContext(connection);
        await db.Database.UseTransactionAsync(sqliteTx, ct);
        return await work(new Tx(db, _clock, _options.DefaultActor, writable: false), ct);
    }

    public Task<byte[]?> IdempotentLookupAsync(string requestId, CancellationToken ct = default) =>
        ReadAsync<byte[]?>(async (tx, _) =>
        {
            var db = ((Tx)tx).Db;
            return (await db.Idempotency.AsNoTracking().FirstOrDefaultAsync(r => r.RequestId == requestId, ct))?.Response;
        }, ct);

    public Task IdempotentStoreAsync(string requestId, string? instanceId, byte[] response, CancellationToken ct = default) =>
        Enqueue(async () =>
        {
            await using var db = NewContext(_writeConnection);
            if (await db.Idempotency.AnyAsync(r => r.RequestId == requestId, CancellationToken.None)) return true;
            db.Idempotency.Add(new IdempotencyRow { RequestId = requestId, InstanceId = instanceId, Response = response, At = _clock.GetUtcNow() });
            await db.SaveChangesAsync(CancellationToken.None);
            return true;
        }, ct);

    /// <summary>§4.4: rows older than the retention are gone; a later call with the same id is a fresh call.</summary>
    public Task<int> PurgeIdempotencyAsync(DateTimeOffset cutoff, CancellationToken ct = default) =>
        Enqueue(async () =>
        {
            await using var db = NewContext(_writeConnection);
            return await db.Idempotency.Where(r => r.At < cutoff).ExecuteDeleteAsync(CancellationToken.None);
        }, ct);

    /// <summary>Q14: a consistent copy of the whole database, taken on the writer so nothing half-written is copied.</summary>
    public Task BackupAsync(string path, CancellationToken ct = default) =>
        Enqueue(async () =>
        {
            if (File.Exists(path)) File.Delete(path);
            await using var cmd = _writeConnection.CreateCommand();
            cmd.CommandText = "VACUUM INTO $path";
            cmd.Parameters.AddWithValue("$path", path);
            await cmd.ExecuteNonQueryAsync(CancellationToken.None);
            return true;
        }, ct);

    /// <summary>The database's size in bytes (page_count × page_size), for the Dashboard.</summary>
    public Task<long> SizeAsync(CancellationToken ct = default) =>
        Enqueue(async () =>
        {
            await using var cmd = _writeConnection.CreateCommand();
            cmd.CommandText = "SELECT page_count * page_size FROM pragma_page_count(), pragma_page_size()";
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(CancellationToken.None));
        }, ct);

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _pump;
        await _writeConnection.DisposeAsync();
    }
}
