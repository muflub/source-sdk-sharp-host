namespace SourceSharp.Host.Abstractions;

/// <summary>D-H17: whose data a key belongs to.</summary>
public enum StoreScope { Level = 1, Instance, Character, Town }

/// <summary>One owner's keys: a level copy, an instance, a character, or <see cref="Town"/>.</summary>
public readonly record struct StoreOwner(StoreScope Scope, string Id)
{
    public static StoreOwner Town { get; } = new(StoreScope.Town, "town");
    public override string ToString() => $"{Scope}:{Id}";
}

public sealed record StoreValue(byte[] Value, long Version);

public sealed record StoreKeyInfo(string Key, long Version, int Size);

/// <summary>
/// D-H17: bytes a game server keeps across restarts, in their own SQLite file (never host.db, so a
/// store write never waits on the ledger's writer) behind a write-through cache. Versions come
/// from one counter that never repeats, so a deleted and recreated key never matches an old
/// version. Limits refuse with <see cref="HostRefusal"/>.
/// </summary>
public interface IKeyValueStore
{
    Task<StoreValue?> Get(StoreOwner owner, string key, CancellationToken ct = default);

    /// <summary>
    /// Writes <paramref name="value"/>; <paramref name="expectedVersion"/> null is unconditional, 0
    /// creates only, anything else is compare-and-set. A mismatch whose stored value is already
    /// <paramref name="value"/> answers as a success (a retried write that landed).
    /// </summary>
    Task<long> Set(StoreOwner owner, string key, byte[] value, long? expectedVersion, CancellationToken ct = default);

    /// <summary>False when the key was not there; a version mismatch on a present key is refused.</summary>
    Task<bool> Delete(StoreOwner owner, string key, long? expectedVersion, CancellationToken ct = default);

    /// <summary>Keys starting with <paramref name="prefix"/> in ordinal order, after <paramref name="after"/>.</summary>
    Task<IReadOnlyList<StoreKeyInfo>> List(StoreOwner owner, string prefix, string? after, int take, CancellationToken ct = default);

    /// <summary>Every owner of <paramref name="scope"/> holding a key, for the lifetime sweep.</summary>
    Task<IReadOnlyList<string>> Owners(StoreScope scope, CancellationToken ct = default);

    /// <summary>Removes every key of <paramref name="owner"/>; returns how many.</summary>
    Task<int> Drop(StoreOwner owner, CancellationToken ct = default);

    /// <summary>D-H18: replaces <paramref name="to"/>'s keys with a copy of <paramref name="from"/>'s; returns how many.</summary>
    Task<int> Copy(StoreOwner from, StoreOwner to, CancellationToken ct = default);
}
