using System.Diagnostics;
using System.Text;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Contracts;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Map2d;
using SourceSharp.MapFormats.Nav;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Nav;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rooms;

namespace Descent.MapForge;

/// <summary>How MapForge links (§9.1 Link).</summary>
public sealed record LinkSettings
{
    /// <summary>With the Source Sharp mod's entities (<c>logic_level_transition</c>, <c>logic_room</c>): what Descent runs.</summary>
    public bool ModEntities { get; init; } = true;
    /// <summary>Write the level's transition keys (<c>up_map</c>/<c>down_map</c>); a library without role rooms links without.</summary>
    public bool Transitions { get; init; } = true;
    /// <summary>Refuse a level whose pack has no navigation (production); off for libraries built without it.</summary>
    public bool RequireNavigation { get; init; }
    public NavCompression NavCompression { get; init; } = LevelNavFromPack.DefaultCompression;
}

/// <summary>
/// One pack, opened for links: its index, library sections and (for a pack of namespaces)
/// the namespace of the library key. What <c>ssmap link</c> does for a level of one library,
/// as library calls: the tools' CLI is a process and is never run (plan §0).
/// </summary>
public sealed class OpenPack : IAsyncDisposable
{
    public required Stream Stream { get; init; }
    public required RoomPackIndex Index { get; init; }
    public required string Library { get; init; }
    public Guid? PackGuid { get; init; }
    public RoomPackNamespace? Space { get; init; }
    public required IReadOnlyList<VmfChunk> Entities { get; init; }
    public required RoomLibraryOptions Options { get; init; }
    /// <summary>The skybox room as the level names it (unqualified), or null.</summary>
    public string? Skybox { get; init; }
    /// <summary>The library's rooms as a level names them, skybox excluded.</summary>
    public required IReadOnlyList<string> Names { get; init; }

    public string PackName(string room) => Space is { } s ? s.Prefix + room : room;

    /// <summary>Opens a pack for <paramref name="library"/>. Throws <see cref="MapCompileFailure"/> when the pack cannot give it.</summary>
    /// <param name="requireNamespace">Refuse a plain pack (§9.4: the namespace must equal the library key).</param>
    public static async Task<OpenPack> OpenAsync(string path, string library, bool requireNamespace, CancellationToken ct)
    {
        var stream = File.OpenRead(path);
        try
        {
            var index = await RoomPack.ReadIndexAsync(stream, ct);
            var guid = await RoomNavPack.ReadPackIdAsync(stream, index, ct);
            var entities = await RoomPack.ReadLibraryEntitiesAsync(stream, index, ct);
            var options = await RoomPack.ReadLibraryOptionsAsync(stream, index, ct);
            var skybox = await RoomPack.ReadLibrarySkyboxAsync(stream, index, ct);
            var namespaces = await RoomPack.ReadNamespacesAsync(stream, index, ct);
            RoomPackNamespace? space = null;
            IReadOnlyList<string> names;
            if (namespaces is null)
            {
                if (requireNamespace)
                    throw new MapCompileFailure($"the pack holds one library without a namespace; bake it with the namespace '{library}'", "");
                names = [.. index.Entries.Select(e => e.Name)];
            }
            else
            {
                space = RoomPackNamespaces.Find(namespaces, library)
                        ?? throw new MapCompileFailure(
                            $"the pack's namespaces are {string.Join(", ", namespaces.Select(n => n.Key))}; it has none named '{library}'", "");
                names = [.. index.Entries.Skip(space.FirstRoom).Take(space.RoomCount).Select(e => e.Name[space.Prefix.Length..])];
                options = options with { NameKeys = space.NameKeys };
                skybox = skybox is { } sky && sky.StartsWith(space.Prefix, StringComparison.Ordinal) ? sky[space.Prefix.Length..] : null;
            }
            if (skybox is not null && index.Find(space is null ? skybox : space.Prefix + skybox) is null)
                throw new MapCompileFailure($"the pack names skybox room '{skybox}' but does not hold it", "");
            return new OpenPack
            {
                Stream = stream, Index = index, Library = library, PackGuid = guid, Space = space, Entities = entities,
                Options = options, Skybox = skybox, Names = [.. names.Where(n => n != skybox)],
            };
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    /// <summary>Every room's definition (turn 0, no navigation): what the tools' generator lays out.</summary>
    public async Task<IReadOnlyList<RoomDefinition>> DefinitionsAsync(CancellationToken ct)
    {
        var rooms = await RoomPack.LoadRoomsAsync(Stream, Index, [.. Names.Select(n => new RoomPackRequest(PackName(n), [0]))], ct);
        return [.. rooms.Select((r, i) => r.Definition with { Name = Names[i] })];
    }

    public ValueTask DisposeAsync() => Stream.DisposeAsync();
}

/// <summary>Links a level from an <see cref="OpenPack"/>: map, level map and navigation.</summary>
public static class PackLinker
{
    /// <summary>
    /// Links <paramref name="level"/> (a level of the pack's one library) and writes
    /// <c>&lt;map&gt;.bsp</c>, <c>.map2d</c> and <c>.nav3d</c> into <paramref name="outputDirectory"/>.
    /// Throws <see cref="MapCompileFailure"/> with the tools' words.
    /// </summary>
    public static async Task<(string Bsp, string? Map2d, string? Nav3d, string Log)> LinkAsync(
        OpenPack pack, LevelGrid level, byte[] levelBytes, string mapName, string outputDirectory, LinkSettings settings, CancellationToken ct)
    {
        var log = new StringBuilder();
        try
        {
            var key = pack.Library;
            var keyed = new LevelGrid(level.Name, level.Library, level.Rows, level.Columns, level.Cells)
            {
                Transitions = level.Transitions, Aliases = level.Aliases, Libraries = [new LevelLibrary(key, level.Library)],
            };
            var resolved = LevelLibraries.Resolve(keyed, [pack.Names]);
            if (!resolved.Placed.Any()) throw new MapCompileFailure("the level places no room", "");

            var prefix = key + LevelLibraries.Separator;
            var turns = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
            var first = new List<string>();
            foreach (var (_, _, cell) in resolved.Placed)
            {
                if (!turns.TryGetValue(cell.Room, out var t)) { turns[cell.Room] = t = []; first.Add(cell.Room); }
                t.Add(cell.Rotation);
            }
            var requests = first.Select(r => new RoomPackRequest(pack.PackName(r[prefix.Length..]), turns[r]) { Navigation = true }).ToList();
            if (pack.Skybox is { } sky && !turns.ContainsKey(prefix + sky))
                requests.Add(new RoomPackRequest(pack.PackName(sky), [0]));
            var loaded = await RoomPack.LoadRoomsAsync(pack.Stream, pack.Index, requests, ct);

            var grid = loaded[0].Definition;
            var library = new RoomLibrary(grid.Kit, grid.CellSize) { Options = pack.Options, LibraryEntities = pack.Entities, SkyboxRoom = pack.Skybox };
            foreach (var room in loaded)
            {
                if (pack.Space is { } space) library.Add(room.Definition.Name[space.Prefix.Length..], room, 0);
                else library.Add(room);
            }
            var set = LevelLibraries.Combine(resolved, [library]);
            foreach (var w in set.Warnings) log.AppendLine($"warning: {w}");
            var rooms = set.Rooms;
            var packId = RoomCompileIds.LevelPackId([(key, pack.PackGuid)]);

            var navOptions = LevelNavFromPack.IdOptions(true, settings.NavCompression);
            var navLayout = resolved.ToLayout(n => rooms.Find(n)?.Definition, rooms.CellSize, rooms.Kit);
            var navPlan = LevelNavFromPack.Plan(navLayout, resolved.Columns, resolved.Rows, rooms.Get, packId, levelBytes, navOptions, true, rooms.SourceOf);
            if (navPlan.Warning is { } navWarning)
            {
                if (settings.RequireNavigation) throw new MapCompileFailure($"navigation: {navWarning}", log.ToString());
                log.AppendLine($"warning: {navWarning}");
            }

            await using var content = new ContentFileSystem([]);
            var context = new VbspContext(VbspOptions.Default, content) { MapBase = mapName.ToLowerInvariant() };
            var layout = resolved.ToLayout(n => rooms.Find(n)?.Definition, rooms.CellSize, rooms.Kit);
            var options = new LevelLinkOptions { ModEntities = settings.ModEntities };
            var link = await LevelLinker.LinkAsync(layout, rooms, context, options, ct);
            foreach (var w in link.NameWarnings.Concat(link.LightingWarnings).Concat(link.AreaWarnings).Concat(link.EntityBudget?.Warnings ?? []))
                log.AppendLine($"warning: {w}");
            if (link.EntityBudget is { } budget) log.AppendLine(budget.Headroom);

            var mapPlan = LevelMapBuilder.Plan(layout, resolved.Columns, resolved.Rows, rooms.Get);
            if (mapPlan.Warning is { } mapWarning) log.AppendLine($"warning: {mapWarning}");
            if (navPlan.WritesNavigation) RoomCompileIds.Stamp(link.Bsp, navPlan.PackId, navPlan.LevelId);

            Directory.CreateDirectory(outputDirectory);
            using var buffer = new MemoryStream();
            await BspFile.SaveAsync(link.Bsp, buffer, BspWriteMode.Canonical, ct);
            var bytes = buffer.ToArray();
            var bspPath = Path.Combine(outputDirectory, mapName + ".bsp");
            await File.WriteAllBytesAsync(bspPath, bytes, ct);
            log.AppendLine($"linked {mapName}: {link.Plan.Layout.Rooms.Count} rooms, {link.Vis.ClusterCount} clusters, {bytes.Length} bytes");

            string? map2dPath = null;
            if (mapPlan.WritesMap)
            {
                map2dPath = Path.Combine(outputDirectory, mapName + Map2dFormat.Extension);
                await File.WriteAllBytesAsync(map2dPath, Map2dWriter.Write(mapPlan.Build(BspMapChecksum.Compute(bytes))), ct);
            }
            string? navPath = null;
            if (navPlan.WritesNavigation)
            {
                var nav = await navPlan.BuildAsync(ct);
                navPath = Path.Combine(outputDirectory, mapName + Nav3dFormat.Extension);
                await File.WriteAllBytesAsync(navPath, Nav3dWriter.Write(nav, settings.NavCompression), ct);
            }
            return (bspPath, map2dPath, navPath, log.ToString());
        }
        catch (Exception e) when (e is LinkException or RoomLintException or MapCompileException or LevelFileException
                                      or ArgumentException or IOException or InvalidDataException or InvalidOperationException)
        {
            throw new MapCompileFailure(e.Message, log.Append(e.Message).ToString());
        }
    }
}
