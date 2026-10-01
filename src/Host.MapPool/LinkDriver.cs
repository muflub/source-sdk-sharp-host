using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Contracts;

namespace SourceSharp.Host.MapPool;

/// <summary>A level the driver linked and stored.</summary>
public sealed record StoredLevel(string Hash, string MapName, long Bytes, TimeSpan LinkTime, string Log);

/// <summary>
/// The link driver (plan §9.1 Link): names the level by its hash, links it through
/// <see cref="IMapCompiler"/> into a staging folder, and stores it under <c>levels/</c> with
/// its bz2. The staging folder never outlives the call.
/// </summary>
public sealed class LinkDriver(IMapCompiler compiler, LevelStorage storage, ModOptions mod, MapPoolOptions pool, MapPoolLayoutOptions layout)
{
    public IMapCompiler Compiler => compiler;

    public string HashOf(PackRecord pack, int depth, ulong seed, int difficulty) =>
        MapIdentity.LevelHash(pack.Id, pack.PackId, pack.Library, depth, seed, difficulty, compiler.LinkerIdentity);

    public async Task<StoredLevel> LinkAsync(PackRecord pack, string packPath, int depth, ulong seed, LevelPlan? plan,
        IReadOnlyList<RoomInfo> rooms, CancellationToken ct = default)
    {
        var hash = HashOf(pack, depth, seed, layout.Difficulty);
        var name = MapIdentity.MapName(mod.Name, depth, hash);
        var staging = storage.StagingFor(name);
        try
        {
            var request = new LinkRequest(name, depth, layout.Difficulty, seed, pack.Library, packPath, plan, staging)
            {
                UpMap = depth == 1 ? mod.TownMap : $"{mod.Name}-{depth - 1}",
                DownMap = depth < pool.Depths ? $"{mod.Name}-{depth + 1}" : null,
                GeneratedRows = layout.Rows,
                GeneratedColumns = layout.Columns,
                Rooms = rooms,
            };
            var files = await compiler.LinkAsync(request, ct);
            var bytes = await storage.StoreAsync(files, ct);
            return new StoredLevel(hash, name, bytes, files.LinkTime, files.Log);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }
}
