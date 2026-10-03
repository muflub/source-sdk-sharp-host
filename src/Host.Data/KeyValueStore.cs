using System.Text;
using Microsoft.Data.Sqlite;
using SourceSharp.Host.Abstractions;

namespace SourceSharp.Host.Data;

/// <summary>
/// D-H17 on its own SQLite file. The service is the only writer, so one connection under one lock
/// serves every call, and an owner's keys, once loaded, are read from memory. Every write commits
/// before the cache changes and before the call returns: an answered Set survives a restart.
/// </summary>
public sealed class KeyValueStore : IKeyValueStore, IAsyncDisposable
{
    readonly StoreOptions _o;
    readonly SqliteConnection _db;
    readonly SemaphoreSlim _lock = new(1, 1);
    readonly Dictionary<StoreOwner, Loaded> _cache = [];
    readonly LinkedList<StoreOwner> _lru = new(); // most recently used first
    long _cachedBytes;
    long _nextVersion;

    sealed class Loaded(LinkedListNode<StoreOwner> node)
    {
        public SortedDictionary<string, StoreValue> Keys { get; } = new(StringComparer.Ordinal);
        public long Bytes { get; set; }
        public LinkedListNode<StoreOwner> Node { get; } = node;
    }

    public KeyValueStore(StoreOptions options)
    {
        _o = options;
        var memory = options.Path == ":memory:";
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = options.Path, Pooling = !memory, DefaultTimeout = 30 }.ToString());
        _db.Open();
        Exec(memory ? "" : "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=30000;");
        Exec("""
            CREATE TABLE IF NOT EXISTS kv (scope INTEGER NOT NULL, owner TEXT NOT NULL, key TEXT NOT NULL,
                value BLOB NOT NULL, version INTEGER NOT NULL, updated_unix_ms INTEGER NOT NULL,
                PRIMARY KEY (scope, owner, key)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS meta (name TEXT PRIMARY KEY, value INTEGER NOT NULL);
            INSERT OR IGNORE INTO meta (name, value) VALUES ('next_version', 1);
            """);
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE name = 'next_version'";
        _nextVersion = Convert.ToInt64(cmd.ExecuteScalar());
    }

    void Exec(string sql)
    {
        if (sql.Length == 0) return;
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Owners held in memory, for facts.</summary>
    public int CachedOwners => _cache.Count;

    public async Task<StoreValue?> Get(StoreOwner owner, string key, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try { return Load(owner).Keys.GetValueOrDefault(key); }
        finally { _lock.Release(); }
    }

    public async Task<long> Set(StoreOwner owner, string key, byte[] value, long? expectedVersion, CancellationToken ct = default)
    {
        CheckKey(key);
        if (value.Length > _o.MaxValueBytes)
            throw new HostRefusal(RefusalCode.InvalidArgument, "value_too_large", $"{value.Length} bytes; the limit is {_o.MaxValueBytes}");
        await _lock.WaitAsync(ct);
        try
        {
            var loaded = Load(owner);
            var current = loaded.Keys.GetValueOrDefault(key);
            if (expectedVersion is { } expected && (current?.Version ?? 0) != expected)
            {
                if (current is not null && current.Value.AsSpan().SequenceEqual(value)) return current.Version;
                throw new HostRefusal(RefusalCode.FailedPrecondition, "version_mismatch",
                    $"{owner}/{key} is at version {current?.Version ?? 0}, not {expected}");
            }
            var (maxKeys, maxBytes) = owner.Scope == StoreScope.Town ? (_o.TownMaxKeys, _o.TownMaxBytes) : (_o.MaxKeys, _o.MaxBytes);
            if (current is null && loaded.Keys.Count >= maxKeys)
                throw HostRefusal.Exhausted("store_full", $"{owner} holds {loaded.Keys.Count} keys; the limit is {maxKeys}");
            var bytes = loaded.Bytes - (current?.Value.Length ?? 0) + value.Length;
            if (bytes > maxBytes)
                throw HostRefusal.Exhausted("store_full", $"{owner} would hold {bytes} bytes; the limit is {maxBytes}");

            var version = _nextVersion;
            using (var tx = _db.BeginTransaction())
            {
                Write(tx, "INSERT INTO kv (scope, owner, key, value, version, updated_unix_ms) VALUES ($s, $o, $k, $v, $n, $t) "
                          + "ON CONFLICT (scope, owner, key) DO UPDATE SET value = $v, version = $n, updated_unix_ms = $t",
                    ("$s", (int)owner.Scope), ("$o", owner.Id), ("$k", key), ("$v", value), ("$n", version), ("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                Write(tx, "UPDATE meta SET value = $n WHERE name = 'next_version'", ("$n", version + 1));
                tx.Commit();
            }
            _nextVersion = version + 1;
            loaded.Keys[key] = new StoreValue(value, version);
            Resize(loaded, bytes);
            return version;
        }
        finally { _lock.Release(); }
    }

    public async Task<bool> Delete(StoreOwner owner, string key, long? expectedVersion, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var loaded = Load(owner);
            if (!loaded.Keys.TryGetValue(key, out var current)) return false;
            if (expectedVersion is { } expected && current.Version != expected)
                throw new HostRefusal(RefusalCode.FailedPrecondition, "version_mismatch", $"{owner}/{key} is at version {current.Version}, not {expected}");
            using (var tx = _db.BeginTransaction())
            {
                Write(tx, "DELETE FROM kv WHERE scope = $s AND owner = $o AND key = $k", ("$s", (int)owner.Scope), ("$o", owner.Id), ("$k", key));
                tx.Commit();
            }
            loaded.Keys.Remove(key);
            Resize(loaded, loaded.Bytes - current.Value.Length);
            return true;
        }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyList<StoreKeyInfo>> List(StoreOwner owner, string prefix, string? after, int take, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            return Load(owner).Keys
                .Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal) && (after is null || string.CompareOrdinal(kv.Key, after) > 0))
                .Take(Math.Max(take, 1))
                .Select(kv => new StoreKeyInfo(kv.Key, kv.Value.Version, kv.Value.Value.Length))
                .ToList();
        }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyList<string>> Owners(StoreScope scope, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT owner FROM kv WHERE scope = $s";
            cmd.Parameters.AddWithValue("$s", (int)scope);
            var owners = new List<string>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) owners.Add(r.GetString(0));
            return owners;
        }
        finally { _lock.Release(); }
    }

    public async Task<int> Drop(StoreOwner owner, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            int n;
            using (var tx = _db.BeginTransaction())
            {
                n = Write(tx, "DELETE FROM kv WHERE scope = $s AND owner = $o", ("$s", (int)owner.Scope), ("$o", owner.Id));
                tx.Commit();
            }
            Evict(owner);
            return n;
        }
        finally { _lock.Release(); }
    }

    public async Task<int> Copy(StoreOwner from, StoreOwner to, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var source = Load(from).Keys.ToList();
            var version = _nextVersion;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            using (var tx = _db.BeginTransaction())
            {
                Write(tx, "DELETE FROM kv WHERE scope = $s AND owner = $o", ("$s", (int)to.Scope), ("$o", to.Id));
                foreach (var (key, v) in source)
                    Write(tx, "INSERT INTO kv (scope, owner, key, value, version, updated_unix_ms) VALUES ($s, $o, $k, $v, $n, $t)",
                        ("$s", (int)to.Scope), ("$o", to.Id), ("$k", key), ("$v", v.Value), ("$n", version++), ("$t", now));
                Write(tx, "UPDATE meta SET value = $n WHERE name = 'next_version'", ("$n", version));
                tx.Commit();
            }
            _nextVersion = version;
            Evict(to); // loaded afresh from the file on its next use
            return source.Count;
        }
        finally { _lock.Release(); }
    }

    void CheckKey(string key)
    {
        if (string.IsNullOrEmpty(key))
            throw new HostRefusal(RefusalCode.InvalidArgument, "bad_key", "a key is not empty");
        if (Encoding.UTF8.GetByteCount(key) > _o.MaxKeyBytes)
            throw new HostRefusal(RefusalCode.InvalidArgument, "key_too_long", $"the limit is {_o.MaxKeyBytes} bytes of UTF-8");
    }

    int Write(SqliteTransaction tx, string sql, params (string Name, object Value)[] args)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        return cmd.ExecuteNonQuery();
    }

    Loaded Load(StoreOwner owner)
    {
        if (_cache.TryGetValue(owner, out var hit))
        {
            _lru.Remove(hit.Node);
            _lru.AddFirst(hit.Node);
            return hit;
        }
        var loaded = new Loaded(_lru.AddFirst(owner));
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = "SELECT key, value, version FROM kv WHERE scope = $s AND owner = $o";
            cmd.Parameters.AddWithValue("$s", (int)owner.Scope);
            cmd.Parameters.AddWithValue("$o", owner.Id);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var value = (byte[])r.GetValue(1);
                loaded.Keys[r.GetString(0)] = new StoreValue(value, r.GetInt64(2));
                loaded.Bytes += value.Length;
            }
        }
        _cache[owner] = loaded;
        _cachedBytes += loaded.Bytes;
        Trim(owner);
        return loaded;
    }

    void Resize(Loaded loaded, long bytes)
    {
        _cachedBytes += bytes - loaded.Bytes;
        loaded.Bytes = bytes;
        Trim(loaded.Node.Value);
    }

    /// <summary>Drops least recently used owners, never <paramref name="keep"/>, until the cache fits.</summary>
    void Trim(StoreOwner keep)
    {
        while (_cachedBytes > _o.CacheBytes && _lru.Last is { } last && last.Value != keep)
            Evict(last.Value);
    }

    void Evict(StoreOwner owner)
    {
        if (!_cache.Remove(owner, out var loaded)) return;
        _lru.Remove(loaded.Node);
        _cachedBytes -= loaded.Bytes;
    }

    /// <summary>Q14: a consistent copy of the store, taken under the lock so nothing half-written is copied.</summary>
    public async Task BackupAsync(string path, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (File.Exists(path)) File.Delete(path);
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "VACUUM INTO $path";
            cmd.Parameters.AddWithValue("$path", path);
            cmd.ExecuteNonQuery();
        }
        finally { _lock.Release(); }
    }

    bool _disposed;

    /// <summary>Idempotent: the container disposes it once per registration (concrete and interface).</summary>
    public async ValueTask DisposeAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            await _db.DisposeAsync();
        }
        finally { _lock.Release(); }
    }
}
