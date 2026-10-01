using System.Diagnostics;
using System.Text;
using SourceSharp.Host.Abstractions;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Bsp.Collision;
using SourceSharp.MapTools.Cache.Sqlite;
using SourceSharp.MapTools.Compile.Cache;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Nav;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Phys.Managed;
using SourceSharp.MapTools.Rooms;

namespace Descent.MapForge;

/// <summary>
/// The bake (plan §9.2): a library's rooms compiled into a <c>.roompack</c> under the
/// library key's namespace, as library calls into the tools (what <c>ssmap room -namespace
/// &lt;key&gt; -incremental</c> does, never run as a process). Needs the game's content.
/// </summary>
public static class RoomsBaker
{
    /// <summary>Mounts the game whose <c>gameinfo.txt</c> is in <paramref name="gameDirectory"/>, read-only, as the tools' vbsp does.</summary>
    public static async Task<GameContentMounter.Result> MountAsync(string gameDirectory, CancellationToken ct)
    {
        var dir = Path.GetFullPath(gameDirectory);
        var disk = new ReadOnlyFileSystem(new PhysicalFileSystem("/"));
        var gameInfoPath = VPath.Create(Path.Combine(dir, "gameinfo.txt"));
        var gameInfo = await GameInfo.LoadAsync(disk, gameInfoPath, ct);
        var steam = await SteamFor(dir, ct);
        return await GameContentMounter.MountAsync(
            disk, gameInfo, new GameContentRoots(gameInfoPath.Directory, VPath.Create(Path.GetDirectoryName(dir)!)) { Steam = steam },
            cancellationToken: ct);
    }

    /// <summary>A Steam library locator for <c>|appid_N|</c> gameinfo lines, when the game sits in one (TF2's does).</summary>
    static async Task<ISteamAppLocator?> SteamFor(string gameDirectory, CancellationToken ct)
    {
        for (var d = new DirectoryInfo(gameDirectory); d is not null; d = d.Parent)
            if (d.Name == "steamapps" && d.Parent is { } root && File.Exists(Path.Combine(d.FullName, "libraryfolders.vdf")))
            {
                var folders = await SteamLibraryFolders.LoadAsync(new ReadOnlyFileSystem(new PhysicalFileSystem("/")), VPath.Create(root.FullName), ct);
                return folders;
            }
        return null;
    }

    public static async Task<BakeResult> BakeAsync(LibrarySource source, BakeSettings settings, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var log = new StringBuilder();
        var vmfBytes = await File.ReadAllBytesAsync(source.Vmf, ct);
        var vmfSha = RoomPackNamespaces.Digest(vmfBytes);
        var manifest = File.Exists(source.Manifest) ? await File.ReadAllTextAsync(source.Manifest, ct) : null;
        var version = MapIdentity.LibraryVersion(vmfSha, manifest);

        RoomPackPlan plan;
        NavSettings? nav;
        try
        {
            var vmf = await VmfDocument.ParseAsync(vmfBytes, ct);
            var sources = new[] { new RoomPackSource(source.Library, source.Vmf, vmf, vmfSha) };
            plan = RoomPackCombiner.Plan(sources);
            nav = RoomPackCombiner.NavOf(sources);
        }
        catch (Exception e) when (e is RoomPackSplitException or RoomLibraryException or LinkException or ChunkFileException)
        {
            throw new MapCompileFailure($"{source.Vmf}: {e.Message}", e.Message);
        }
        foreach (var w in plan.Warnings) log.AppendLine($"warning: {w}");

        var mounted = await MountAsync(settings.GameDirectory, ct);
        await using var content = mounted.Content;
        foreach (var skipped in mounted.Skipped) log.AppendLine($"content: skipped {skipped}");
        var resolution = FormatResolution.Resolve(null, null, false, false, mounted.GameInfo);
        var options = VbspOptions.Default with { Format = resolution.Resolved };

        var skyboxRoom = plan.SkyboxRoom is { } skyName ? plan.Rooms.First(r => r.Definition.Name == skyName) : null;
        RoomLightingSettings? lighting = settings.Light
            ? new RoomLightingSettings(StockArgs.ParseVrad(["room"]).Options with { Compliance = options.Compliance })
            {
                Sun = RoomLightingSettings.SunOf(plan.LibraryEntities), DoorLight = true, Skybox = skyboxRoom,
            }
            : null;
        var singletons = lighting is null ? plan.SingletonsSha256 : RoomPackNamespaces.LitSingletonDigest(plan.SingletonsSha256, skyboxRoom);
        var packGuid = RoomCompileIds.CombinedPackId(
            [(source.Library, vmfSha)], ["mapforge", .. lighting is null ? Array.Empty<string>() : [lighting.Describe()]], nav?.ToString());

        await using var cooker = ManagedCollisionCooker.Create(options.Compliance);
        using var hulls = new PropHullCache();
        var packOptions = new RoomNavPackOptions();
        var compile = new RoomLibraryCompileSettings(options, content)
        {
            CollisionCooker = cooker, PropHullCache = hulls, Nav = nav, Lighting = lighting,
            Parallelism = settings.Threads is > 0 ? new CompileParallelism { MaxDegree = settings.Threads.Value } : CompileParallelism.Default,
        };

        SqliteCacheStore? store = null;
        if (settings.CacheDirectory is { } cacheDir)
        {
            Directory.CreateDirectory(cacheDir);
            store = new SqliteCacheStore();
            await store.OpenAsync(Path.Combine(cacheDir, source.Library + ".sscache.db"), ct);
            if (!store.IsUsable) { log.AppendLine("cache: the store did not open; every room compiles"); await store.DisposeAsync(); store = null; }
        }
        await using var ownedStore = store;
        using var cache = store is null ? null : new RoomCompileCache(store, CachePolicy.Default,
            new RoomCacheInputs(options) { Nav = nav, PackOptions = packOptions, Lighting = lighting }, content);

        var rooms = plan.Rooms.ToList();
        var packed = new List<RoomPackItem>();
        int failed = 0, reused = 0;
        ValueTask Report(RoomBuildOutcome outcome, CancellationToken _)
        {
            var name = outcome.Room.Definition.Name;
            if (outcome.Item is { } item)
            {
                packed.Add(item);
                reused += outcome.Reused ? 1 : 0;
                log.AppendLine($"{(outcome.Reused ? "reused" : "compiled")} {name} ({outcome.ClusterCount} clusters)");
                foreach (var w in outcome.NameWarnings.Concat(outcome.NavWarnings).Concat(outcome.MapWarnings)) log.AppendLine($"warning: {w}");
            }
            else
            {
                failed++;
                log.AppendLine($"room '{name}': {outcome.Error?.Message}");
            }
            return ValueTask.CompletedTask;
        }
        await RoomLibraryBuild.BuildAsync(rooms, compile, packOptions, cache, Report, ct);

        var space = plan.Spaces[0];
        var namespaces = new List<RoomPackNamespace>
        {
            new(space.Key, space.Source, space.VmfSha256, singletons, 0, packed.Count) { NameKeys = space.NameKeys },
        };
        var sections = new List<RoomPackSectionData> { RoomCompileIds.Section(packGuid) };
        if (plan.LibraryEntities.Count > 0) sections.Add(RoomLibraryEntities.ToSection(plan.LibraryEntities));
        if (plan.Options.ToSection() is { } optionSection) sections.Add(optionSection);
        if (plan.SkyboxRoom is not null) sections.Add(RoomLibrarySkybox.ToSection(plan.SkyboxRoom));
        sections.Add(RoomPackNamespaces.ToSection(namespaces));

        var output = Path.GetFullPath(settings.OutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var temp = output + ".writing";
        await using (var fs = File.Create(temp))
            await RoomPack.SaveAsync(sections, packed, fs, ct);
        File.Move(temp, output, overwrite: true);
        if (cache is not null) await cache.CommitAsync(ct);
        log.AppendLine($"wrote {output} ({packed.Count} of {rooms.Count} rooms; {packed.Count - reused} compiled, {reused} reused)");
        return new BakeResult(output, packGuid.ToString("D"), version, rooms.Count, packed.Count - reused, reused, failed, sw.Elapsed, log.ToString());
    }
}
