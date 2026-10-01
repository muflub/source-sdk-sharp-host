using System.Text.Json;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rooms;

namespace Descent.MapForge;

/// <summary>One lint finding: which rule, which room (null = the library), and what.</summary>
public sealed record LintFinding(string Rule, string? Room, string Message)
{
    public override string ToString() => Room is null ? $"{Rule}: {Message}" : $"{Rule}: room '{Room}': {Message}";
}

/// <summary>What lint knows of one room, from the library source or from a pack's own metadata.</summary>
/// <param name="Role">"up", "down" or null (the tools' <c>room_role</c>); null too when the source cannot say (a pack).</param>
/// <param name="Markers">Every classname and name in the room a marker may refer to.</param>
public sealed record RoomFacts(string Name, string? Role, IReadOnlySet<string> Markers, bool RoleKnown = true);

/// <summary>
/// <c>mapforge lint</c> (plan §9.1): the tools' own <see cref="RoomLinter"/> (model, sealed,
/// sockets) and then the game's rules over <c>library.json</c>. Every rule has a refusing
/// fixture in the facts.
/// </summary>
public static class MapForgeLint
{
    public const string Tools = "tools";
    public const string Manifest = "manifest";
    public const string Mod = "mod";
    public const string UnknownTag = "unknown_tag";
    public const string UnlistedRoom = "unlisted_room";
    public const string UnknownRoom = "unknown_room";
    public const string DepthRange = "depth_range";
    public const string TagCoverage = "tag_coverage";
    public const string Marker = "marker";
    public const string Role = "role";

    /// <summary>
    /// R19's jump volume measured from brushes is not linted: the tools expose no cheap per-room
    /// brush query at 83bbf57 (a room's brushes are only reachable by loading the VMF through
    /// <c>MapFileLoader</c> with content). Named here so a report can say what was not checked.
    /// </summary>
    public const string NotChecked = "R19 jump volume (no brush query in the tools); nav fragment covering every marker (no public POI reader on a pack)";

    /// <summary>
    /// Lints a library source: the tools split it (and, given content, run
    /// <see cref="RoomLinter.CheckModel"/> on every room), then the game rules run.
    /// </summary>
    /// <param name="gameDirectory">A folder with the game's <c>gameinfo.txt</c> for the model check's materials; null skips it.</param>
    public static async Task<IReadOnlyList<LintFinding>> LintSourceAsync(
        string vmfPath, string? manifestJson, string expectedMod, int depths, string? gameDirectory = null, CancellationToken ct = default)
    {
        var findings = new List<LintFinding>();
        RoomLibrarySplit split;
        try
        {
            var bytes = await File.ReadAllBytesAsync(vmfPath, ct);
            var vmf = await VmfDocument.ParseAsync(bytes, ct);
            split = RoomLibraryVmf.SplitLibrary(vmf);
        }
        catch (Exception e) when (e is RoomLibraryException or RoomLintException or ChunkFileException or IOException or LinkException or ArgumentException)
        {
            findings.Add(new LintFinding(Tools, null, e.Message));
            return findings;
        }

        if (gameDirectory is not null)
            findings.AddRange(await ModelCheckAsync(split, gameDirectory, ct));

        var rooms = split.Rooms.Select(r => new RoomFacts(r.Definition.Name, RoleName(r.Role), MarkersOf(r.Document))).ToList();
        findings.AddRange(GameRules(manifestJson, rooms, expectedMod, depths));
        return findings;
    }

    static async Task<IReadOnlyList<LintFinding>> ModelCheckAsync(RoomLibrarySplit split, string gameDirectory, CancellationToken ct)
    {
        var findings = new List<LintFinding>();
        var mounted = await RoomsBaker.MountAsync(gameDirectory, ct);
        await using var content = mounted.Content;
        foreach (var room in split.Rooms)
        {
            try
            {
                var context = new VbspContext(VbspOptions.Default, content) { MapBase = room.Definition.Name };
                var map = await MapFileLoader.LoadAsync(context, room.Document, ct);
                MapFileReader.TakeBounds(map);
                RoomLinter.CheckModel(room.Definition, map);
            }
            catch (Exception e) when (e is RoomLintException or LinkException or ArgumentException or InvalidDataException)
            {
                findings.Add(new LintFinding(Tools, room.Definition.Name, e.Message));
            }
        }
        return findings;
    }

    static string? RoleName(RoomRole role) => role switch { RoomRole.Up => "up", RoomRole.Down => "down", _ => null };

    /// <summary>Every entity's classname, targetname and <c>info_poi</c> type in a room's VMF.</summary>
    public static IReadOnlySet<string> MarkersOf(VmfDocument room)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in room.GetChunks(MapFileLoader.EntityChunk))
            foreach (var key in new[] { "classname", "targetname", "poi_type" })
                if (e.GetValue(key) is { Length: > 0 } v) set.Add(v);
        return set;
    }

    /// <summary>The game's rules (§9.1, TF2 plan 6a) over the manifest and the rooms the tools found.</summary>
    public static IReadOnlyList<LintFinding> GameRules(string? manifestJson, IReadOnlyList<RoomFacts> rooms, string expectedMod, int depths)
    {
        var findings = new List<LintFinding>();
        if (manifestJson is null)
        {
            findings.Add(new LintFinding(Manifest, null, "no library.json: the game's tags, depths and markers are unknown"));
            return findings;
        }
        LibraryManifest m;
        try { m = LibraryManifest.Parse(manifestJson); }
        catch (InvalidDataException e) { findings.Add(new LintFinding(Manifest, null, e.Message)); return findings; }

        if (m.Mod != expectedMod)
            findings.Add(new LintFinding(Mod, null, $"library.json names mod '{m.Mod}', not '{expectedMod}'"));

        var byName = rooms.ToDictionary(r => r.Name, StringComparer.Ordinal);
        foreach (var r in rooms.Where(r => !m.Rooms.ContainsKey(r.Name)))
            findings.Add(new LintFinding(UnlistedRoom, r.Name, "the library has the room and library.json has no entry for it"));
        foreach (var name in m.Rooms.Keys.Where(n => !byName.ContainsKey(n)).Order(StringComparer.Ordinal))
            findings.Add(new LintFinding(UnknownRoom, name, "library.json lists a room the library does not hold"));

        foreach (var (name, e) in m.Rooms.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            foreach (var tag in e.Tags.Where(t => !LibraryManifest.KnownTags.Contains(t)))
                findings.Add(new LintFinding(UnknownTag, name, $"tag '{tag}' is not one of {string.Join(", ", LibraryManifest.KnownTags)}"));
            if (e.Depths.Length is < 1 or > 2 || e.MinDepth < 1 || e.MinDepth > e.MaxDepth)
                findings.Add(new LintFinding(DepthRange, name, $"depths [{string.Join(", ", e.Depths)}] is not [min, max] with 1 <= min <= max"));
            if (e.Weight < 1)
                findings.Add(new LintFinding(DepthRange, name, $"weight {e.Weight} is below 1"));
            if (byName.TryGetValue(name, out var facts))
            {
                foreach (var marker in e.Markers.Where(k => !facts.Markers.Contains(k)))
                    findings.Add(new LintFinding(Marker, name, $"marker '{marker}' is not in the room"));
                if (facts.RoleKnown)
                {
                    var hasRoles = rooms.Any(r => r.Role is not null);
                    if (hasRoles && e.Tags.Contains("start") != (facts.Role == "up"))
                        findings.Add(new LintFinding(Role, name, "a library with role rooms tags exactly its room_role up rooms 'start'"));
                    if (hasRoles && e.Tags.Contains("stairs") != (facts.Role == "down"))
                        findings.Add(new LintFinding(Role, name, "a library with role rooms tags exactly its room_role down rooms 'stairs'"));
                }
            }
        }

        // Tag coverage: every depth the library serves (any room's range, within the pool's
        // depths) can place an arrival and stairs, and a boss depth a boss arena.
        var served = Enumerable.Range(1, Math.Max(0, depths))
            .Where(d => m.Rooms.Any(r => byName.ContainsKey(r.Key) && d >= r.Value.MinDepth && d <= r.Value.MaxDepth));
        foreach (var depth in served)
        {
            var at = m.Rooms.Where(r => byName.ContainsKey(r.Key) && depth >= r.Value.MinDepth && depth <= r.Value.MaxDepth).ToList();
            foreach (var need in new[] { "start", "stairs" }.Concat(LibraryManifest.BossDepths.Contains(depth) ? ["boss_arena"] : []))
                if (!at.Any(r => r.Value.Tags.Contains(need)))
                    findings.Add(new LintFinding(TagCoverage, null, $"depth {depth} has no room tagged '{need}'"));
        }
        return findings;
    }

    public static string Report(IReadOnlyList<LintFinding> findings) =>
        JsonSerializer.Serialize(new { ok = findings.Count == 0, notChecked = NotChecked, findings = findings.Select(f => new { f.Rule, f.Room, f.Message }) });
}
