using Microsoft.Extensions.Time.Testing;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Data;

namespace SourceSharp.Host.Testing;

/// <summary>The real stores on SQLite :memory:, with a fake clock (CLAUDE.md, tier 1).</summary>
public sealed class TestData : IAsyncDisposable
{
    public FakeTimeProvider Clock { get; }
    public HostData Data { get; }

    public TestData(FakeTimeProvider? clock = null, string path = ":memory:")
    {
        Clock = clock ?? new FakeTimeProvider(new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero));
        Data = new HostData(new HostDataOptions { Path = path }, Clock);
    }

    public Task<T> Write<T>(Func<IWriteTx, Task<T>> work) => Data.WriteAsync((tx, _) => work(tx));
    public Task Write(Func<IWriteTx, Task> work) => Data.WriteAsync(async (tx, _) => { await work(tx); return true; });
    public Task<T> Read<T>(Func<IReadTx, Task<T>> work) => Data.ReadAsync((tx, _) => work(tx));

    public static NewItem Item(string baseType = "scattergun", int tier = 0, int level = 1, string? forCharacter = null) =>
        new(Seed: 42, BaseType: baseType, Rarity: tier, ItemLevel: level, Count: 1, Identified: true,
            Instance: [1, 2, 3], SchemaVersion: 1, Tier: tier, RolledForLevel: level, ForCharacter: forCharacter);

    public ValueTask DisposeAsync() => Data.DisposeAsync();
}
