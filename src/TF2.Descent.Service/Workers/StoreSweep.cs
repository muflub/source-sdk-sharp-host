using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Data;

namespace TF2.Descent.Service.Workers;

/// <summary>
/// D-H17: store data lives as long as its owner, and the owners live in host.db, so their ends are
/// swept across the two files: an instance's keys go once it is terminal (or gone), a level copy's
/// once nothing claims or runs it, a character's once it is deleted. TOWN is never swept. Idempotent,
/// so a sweep cut short by a restart is finished by the next; each sweep that removed anything writes
/// one audit row with the counts.
/// </summary>
public sealed class StoreSweep(IHostData data, KeyValueStore store)
{
    public const string SweptAction = "store.swept";

    public sealed record Result(int Instances, int Levels, int Characters, int Keys);

    public async Task<Result> Run(CancellationToken ct)
    {
        var instances = await store.Owners(StoreScope.Instance, ct);
        var levels = await store.Owners(StoreScope.Level, ct);
        var characters = await store.Owners(StoreScope.Character, ct);
        var ended = await data.ReadAsync(async (tx, _) =>
        {
            var running = await tx.Instances.NonTerminal();
            var live = running.Select(i => i.Id).ToHashSet();
            var copies = running.Select(i => i.LevelCopy).OfType<string>().ToHashSet();
            var gone = new List<StoreOwner>();
            foreach (var id in instances) if (!live.Contains(id)) gone.Add(new StoreOwner(StoreScope.Instance, id));
            foreach (var id in levels)
                if (!copies.Contains(id) && await tx.LevelClaims.ClaimsOnCopy(id) == 0) gone.Add(new StoreOwner(StoreScope.Level, id));
            foreach (var id in characters)
                if (await tx.Characters.Get(id) is null or { Deleted: true }) gone.Add(new StoreOwner(StoreScope.Character, id));
            return gone;
        }, ct);
        var keys = 0;
        foreach (var owner in ended) keys += await store.Drop(owner, ct);
        var result = new Result(ended.Count(o => o.Scope == StoreScope.Instance), ended.Count(o => o.Scope == StoreScope.Level),
            ended.Count(o => o.Scope == StoreScope.Character), keys);
        if (ended.Count > 0)
            await data.WriteAsync(async (tx, _) =>
            {
                await tx.Audit.Write(SweptAction, "store", null, new { result.Instances, result.Levels, result.Characters, result.Keys, owners = ended.Select(o => o.ToString()) });
                return true;
            }, ct);
        return result;
    }

    public Task Backup(string path, CancellationToken ct) => store.BackupAsync(path, ct);
}
