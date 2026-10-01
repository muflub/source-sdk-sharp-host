using System.Diagnostics;
using System.Text.Json;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Contracts;
using SourceSharp.MapTools.Rooms;

namespace Descent.MapForge;

/// <summary>
/// §9.4's validation of a pack on its way in (an upload or a bake's POST), as library calls:
/// it opens with the tools' pack reader; its namespace is the library key; the mod is the
/// service's and library.json names it; every room passes lint from the pack's own metadata;
/// every room fits the engine's entity budget on its own; and a trial link of one small fixed
/// level succeeds. Every problem is reported, not only the first.
/// </summary>
public sealed class PackValidator(string mod, int depths, LinkSettings? link = null) : IPackValidator
{
    readonly LinkSettings _link = link ?? new LinkSettings();

    public const string Corrupt = "corrupt";
    public const string WrongMod = "wrong_mod";
    public const string WrongLibrary = "wrong_library";
    public const string Lint = "lint";
    public const string Budget = "entity_budget";
    public const string TrialLink = "trial_link";

    /// <summary>The trial level: the tools' generator, seed 1, 2 × 2, over the pack's rooms (fixed per library and pack).</summary>
    public const int TrialSeed = 1, TrialSize = 2;

    public async Task<PackInspection> ValidateAsync(PackCandidate c, CancellationToken ct = default)
    {
        var problems = new List<string>();
        var report = new Dictionary<string, object?> { ["mod"] = c.Mod, ["library"] = c.Library };
        if (c.Mod != mod) problems.Add($"{WrongMod}: the pack is for mod '{c.Mod}'; this service hosts '{mod}'");

        OpenPack pack;
        try
        {
            pack = await OpenPack.OpenAsync(c.Path, c.Library, requireNamespace: true, ct);
        }
        catch (MapCompileFailure e)
        {
            problems.Add($"{WrongLibrary}: {e.Message}");
            return Done(problems, report, null, null, [], TimeSpan.Zero);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            problems.Add($"{Corrupt}: the tools' pack reader refused it: {e.Message}");
            return Done(problems, report, null, null, [], TimeSpan.Zero);
        }

        await using (pack)
        {
            var packId = pack.PackGuid?.ToString("D") ?? "";
            var version = MapIdentity.LibraryVersion(pack.Space!.VmfSha256, c.LibraryJson);
            report["packId"] = packId;
            report["libraryVersion"] = version;
            report["rooms"] = pack.Names;

            // Lint from the pack's own metadata: its rooms, their entity classes and local names.
            IReadOnlyDictionary<string, RoomEntityCounts> counts;
            IReadOnlyDictionary<string, RoomNameSummary> names;
            try
            {
                counts = await RoomPack.ReadEntityCountsAsync(pack.Stream, pack.Index, ct);
                names = await RoomPack.ReadNameSummariesAsync(pack.Stream, pack.Index, ct);
            }
            catch (Exception e) when (e is LinkException or InvalidDataException or IOException)
            {
                problems.Add($"{Corrupt}: {e.Message}");
                return Done(problems, report, packId, version, [], TimeSpan.Zero);
            }
            var facts = pack.Names.Select(n =>
            {
                var markers = new HashSet<string>(StringComparer.Ordinal);
                if (counts.TryGetValue(pack.PackName(n), out var cnt)) foreach (var k in cnt.Classes) markers.Add(k.ClassName);
                if (names.TryGetValue(pack.PackName(n), out var nm)) foreach (var l in nm.LocalNames) markers.Add(l);
                return new RoomFacts(n, null, markers, RoleKnown: false);
            }).ToList();
            var findings = MapForgeLint.GameRules(c.LibraryJson, facts, mod, depths);
            report["lint"] = findings.Select(f => f.ToString()).ToList();
            report["notChecked"] = MapForgeLint.NotChecked;
            problems.AddRange(findings.Select(f => $"{Lint}: {f}"));

            // Entity budget: every room must fit the engine's edict budget on its own.
            var reserve = LevelEntityBudget.ReserveFor(new LevelLinkOptions(), pack.Options);
            var table = EntityClassTable.Default;
            var perRoom = new Dictionary<string, long>();
            foreach (var n in pack.Names)
            {
                if (!counts.TryGetValue(pack.PackName(n), out var cnt))
                {
                    problems.Add($"{Budget}: room '{n}' has no entity counts in the pack; rebake it");
                    continue;
                }
                var one = LevelEntityBudget.Check([(n, cnt)], reserve, table);
                perRoom[n] = one.Edicts;
                if (one.Edicts > one.Budget) problems.Add($"{Budget}: room '{n}' alone needs {one.Edicts} edicts of a budget of {one.Budget}");
            }
            report["entities"] = perRoom;

            var rooms = RoomsFor(c.LibraryJson, pack.Names);
            var trial = TimeSpan.Zero;
            if (problems.Count == 0)
            {
                var sw = Stopwatch.StartNew();
                var dir = Path.Combine(Path.GetTempPath(), "mapforge-trial-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var request = new LinkRequest($"{mod}-trial-{c.Library}", 1, 0, TrialSeed, c.Library, c.Path, null, dir)
                    {
                        UpMap = "trial-up", DownMap = "trial-down", GeneratedRows = TrialSize, GeneratedColumns = TrialSize, Rooms = rooms,
                    };
                    var yaml = await LinkedMapCompiler.LevelFileAsync(pack, request, _link, ct);
                    var level = LevelYaml.Parse(yaml, request.MapName);
                    var (_, _, _, log) = await PackLinker.LinkAsync(pack, level, System.Text.Encoding.UTF8.GetBytes(yaml), request.MapName, dir, _link, ct);
                    report["trialLevel"] = yaml;
                    report["trialLog"] = log;
                }
                catch (MapCompileFailure e) { problems.Add($"{TrialLink}: {e.Message}"); }
                catch (LevelFileException e) { problems.Add($"{TrialLink}: {e.Message}"); }
                finally
                {
                    trial = sw.Elapsed;
                    if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                }
                report["trialLinkMs"] = (long)trial.TotalMilliseconds;
            }
            return Done(problems, report, packId, version, rooms, trial);
        }
    }

    static IReadOnlyList<RoomInfo> RoomsFor(string? json, IReadOnlyList<string> names)
    {
        if (json is null) return [];
        try
        {
            var set = names.ToHashSet(StringComparer.Ordinal);
            return LibraryManifest.Parse(json).RoomInfos().Where(r => set.Contains(r.Name)).ToList();
        }
        catch (InvalidDataException) { return []; }
    }

    static PackInspection Done(List<string> problems, Dictionary<string, object?> report, string? packId, string? version,
        IReadOnlyList<RoomInfo> rooms, TimeSpan trial)
    {
        report["ok"] = problems.Count == 0;
        report["problems"] = problems;
        return new PackInspection(problems, packId, version, rooms, JsonSerializer.Serialize(report), trial);
    }
}
