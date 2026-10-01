using System.Diagnostics;
using System.Text;
using SourceSharp.Host.Abstractions;
using SourceSharp.MapTools.Rooms;

namespace Descent.MapForge;

/// <summary>
/// The real <see cref="IMapCompiler"/> (plan §9.2): links in-process with the tools'
/// <see cref="LevelLinker"/> (no content needed, so it runs in the service pod) and bakes with
/// the tools' room compiler (content needed: the bake Job).
/// </summary>
public sealed class LinkedMapCompiler(LinkSettings? settings = null) : IMapCompiler
{
    public LinkSettings Settings { get; } = settings ?? new LinkSettings();

    /// <summary>The tools' assembly build (its deterministic module id) and the MapForge link settings.</summary>
    public string LinkerIdentity { get; } =
        $"ssmap:{typeof(LevelLinker).Assembly.ManifestModule.ModuleVersionId:N}/mapforge:{typeof(LinkedMapCompiler).Assembly.ManifestModule.ModuleVersionId:N}"
        + $"/mod{(settings ?? new LinkSettings()).ModEntities}/tr{(settings ?? new LinkSettings()).Transitions}";

    public Task<BakeResult> BakeAsync(LibrarySource library, BakeSettings bake, CancellationToken ct = default) =>
        RoomsBaker.BakeAsync(library, bake, ct);

    public async Task<LevelFiles> LinkAsync(LinkRequest r, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        await using var pack = await OpenPack.OpenAsync(r.PackPath, r.Library, requireNamespace: false, ct);
        var yaml = await LevelFileAsync(pack, r, Settings, ct);
        LevelGrid level;
        try { level = LevelYaml.Parse(yaml, r.MapName); }
        catch (LevelFileException e) { throw new MapCompileFailure($"level.yaml: {e.Message}", yaml); }

        Directory.CreateDirectory(r.OutputDirectory);
        var yamlPath = Path.Combine(r.OutputDirectory, r.MapName + ".yaml");
        var bytes = Encoding.UTF8.GetBytes(yaml);
        await File.WriteAllBytesAsync(yamlPath, bytes, ct);
        var (bsp, map2d, nav, log) = await PackLinker.LinkAsync(pack, level, bytes, r.MapName, r.OutputDirectory, Settings, ct);
        return new LevelFiles(r.MapName, bsp, nav, map2d, yamlPath, sw.Elapsed, log);
    }

    /// <summary>
    /// The level file of a request: the rules module's plan when there is one (D-H7), else the
    /// tools' <see cref="LevelGenerator"/> over the pack's rooms, seeded (the pre-6b fallback).
    /// </summary>
    public static async Task<string> LevelFileAsync(OpenPack pack, LinkRequest r, LinkSettings settings, CancellationToken ct)
    {
        var keys = settings.Transitions ? new LevelKeys(r.UpMap, r.DownMap) : null;
        if (r.Plan is { } plan)
        {
            try { return LevelFileWriter.Write(plan, r.Library, r.MapName, keys); }
            catch (ArgumentException e) { throw new MapCompileFailure($"layout: {e.Message}", ""); }
        }

        var infos = r.Rooms.ToDictionary(x => x.Name, StringComparer.Ordinal);
        var defs = (await pack.DefinitionsAsync(ct))
            .Where(d => !infos.TryGetValue(d.Name, out var i) || (r.Depth >= i.MinDepth && r.Depth <= i.MaxDepth))
            .ToList();
        var roles = defs.Select(d => infos.TryGetValue(d.Name, out var i)
            ? i.Tags.Contains("start") ? RoomRole.Up : i.Tags.Contains("stairs") ? RoomRole.Down : RoomRole.None
            : RoomRole.None).ToList();
        var withRoles = keys is not null && roles.Any(x => x != RoomRole.None);
        var options = new LevelGeneratorOptions(r.GeneratedRows, r.GeneratedColumns, r.Seed);
        try
        {
            var grid = LevelGenerator.Generate(defs, options, r.MapName, LevelFileWriter.LibraryFile(r.Library), null,
                withRoles ? new LayoutTransitions(roles) { NoUp = keys!.UpMap is null, NoDown = keys.DownMap is null }
                          : new LayoutTransitions(defs.Select(_ => RoomRole.None).ToList()));
            if (withRoles) grid = grid.WithTransitions(LevelFileWriter.Transitions(keys!));
            return LevelYaml.Write(grid, LevelGenerator.Header(options, grid));
        }
        catch (Exception e) when (e is LinkException or ArgumentException or InvalidOperationException)
        {
            throw new MapCompileFailure($"layout (seed {r.Seed}): {e.Message}", "");
        }
    }
}
