using System.Security.Cryptography;
using System.Text;
using SourceSharp.Host.Contracts;

namespace SourceSharp.Host.Abstractions;

/// <summary>
/// A room library's source (plan §9.1): one folder holding <c>rooms.vmf</c> (the rooms,
/// <c>info_room</c> markers as the map tools define them) and <c>library.json</c> (the
/// game's metadata the tools do not know).
/// </summary>
public sealed record LibrarySource(string Mod, string Library, string Directory)
{
    public const string VmfName = "rooms.vmf";
    public const string ManifestName = "library.json";
    public string Vmf => Path.Combine(Directory, VmfName);
    public string Manifest => Path.Combine(Directory, ManifestName);
}

/// <summary>How a bake runs: the content it mounts, where the incremental store lives, where the pack goes.</summary>
/// <param name="GameDirectory">The folder holding the mounted game's <c>gameinfo.txt</c> (the content PVC in the bake Job); its parent is the install directory, as the tools take it.</param>
/// <param name="CacheDirectory">The tools' incremental SQLite store's folder, or null to compile every room.</param>
/// <param name="OutputPath">Where the <c>.roompack</c> is written.</param>
/// <param name="Threads">Rooms compiled at once; null = every core.</param>
/// <param name="Light">Bake the rooms' lighting (the tools' default); off only for the unit tier's speed.</param>
public sealed record BakeSettings(string GameDirectory, string? CacheDirectory, string OutputPath, int? Threads = null, bool Light = true);

/// <summary>What a bake produced.</summary>
public sealed record BakeResult(
    string PackPath, string PackId, string LibraryVersion, int Rooms, int Compiled, int Reused, int Failed,
    TimeSpan Elapsed, string Log)
{
    public bool Ok => Failed == 0;
}

/// <summary>
/// One level to link (§9.2). <see cref="Plan"/> is the rules module's layout
/// (<see cref="IGameRules.GenerateLayout"/>, D-H7); null asks the tools' own generator for a
/// seeded layout of the pack's rooms (the pre-6b fallback, §9.1).
/// </summary>
/// <param name="MapName">The map's name, <c>&lt;mod&gt;-&lt;depth&gt;-&lt;hash&gt;</c>: the files' base name and the engine's map name.</param>
/// <param name="Library">The library key: the pack's namespace, and the level file's library.</param>
/// <param name="PackPath">The <c>.roompack</c> the level links from.</param>
/// <param name="OutputDirectory">Where the files are written, each named after <paramref name="MapName"/>.</param>
public sealed record LinkRequest(
    string MapName, int Depth, int Difficulty, ulong Seed, string Library, string PackPath, LevelPlan? Plan, string OutputDirectory)
{
    /// <summary>The map a party arrives from (the level above, or the town for depth 1); null = the top level.</summary>
    public string? UpMap { get; init; }
    /// <summary>The map the stairs lead to; null = the bottom level.</summary>
    public string? DownMap { get; init; }
    /// <summary>Rows × columns of the tools' generated layout, when <see cref="Plan"/> is null.</summary>
    public int GeneratedRows { get; init; } = 3;
    public int GeneratedColumns { get; init; } = 3;
    /// <summary>The pack's rooms as the game knows them (library.json): the generator keeps to their depth ranges and reads roles from the start / stairs tags.</summary>
    public IReadOnlyList<RoomInfo> Rooms { get; init; } = [];
}

/// <summary>A linked level's files, all in the request's output directory (§9.1 Link).</summary>
public sealed record LevelFiles(
    string MapName, string Bsp, string? Nav3d, string? Map2d, string LevelYaml, TimeSpan LinkTime, string Log)
{
    public IEnumerable<string> All => new[] { Bsp, Nav3d, Map2d, LevelYaml }.OfType<string>();
}

/// <summary>A link or bake that the tools refused, with their words.</summary>
public sealed class MapCompileFailure(string message, string log) : Exception(message)
{
    public string Log { get; } = log;
}

/// <summary>
/// The compiler seam (plan §9.2): both are library calls into <c>SourceSharp.MapTools</c>;
/// nothing shells out. <c>LinkedMapCompiler</c> is the real one, <c>FakeMapCompiler</c> the
/// unit tier's.
/// </summary>
public interface IMapCompiler
{
    /// <summary>Names the linker build: part of every level hash, so a new linker never reuses an old level's name.</summary>
    string LinkerIdentity { get; }

    /// <summary>Bakes a library into a pack (needs content: the bake Job).</summary>
    Task<BakeResult> BakeAsync(LibrarySource library, BakeSettings settings, CancellationToken ct = default);

    /// <summary>Links one level from a pack (needs no content: in the service pod). Throws <see cref="MapCompileFailure"/>.</summary>
    Task<LevelFiles> LinkAsync(LinkRequest request, CancellationToken ct = default);
}

/// <summary>A pack on its way in (§9.4): an upload or a bake's POST, before it is a version.</summary>
/// <param name="Mod">The mod it is for; must be the service's <c>Mod.Name</c>.</param>
/// <param name="Library">The library key it claims: must be the pack's namespace.</param>
/// <param name="Path">The streamed file (<c>*.roompack.uploading</c>).</param>
/// <param name="LibraryJson">The game's metadata for its rooms, or null (then the previous version's, if any).</param>
public sealed record PackCandidate(string Mod, string Library, string Path, string? LibraryJson);

/// <summary>
/// A validation's verdict: every problem found, the rooms as the rules module's
/// <see cref="LayoutKey"/> wants them, and the report the admin sees.
/// </summary>
public sealed record PackInspection(
    IReadOnlyList<string> Problems, string? PackId, string? LibraryVersion, IReadOnlyList<RoomInfo> Rooms,
    string ReportJson, TimeSpan TrialLink)
{
    public bool Ok => Problems.Count == 0;
}

/// <summary>§9.4's validation: opens with the tools' reader, namespace, mod, lint, entity budget, trial link.</summary>
public interface IPackValidator
{
    Task<PackInspection> ValidateAsync(PackCandidate candidate, CancellationToken ct = default);
}

/// <summary>The identities §9.1 names: library version and level hash.</summary>
public static class MapIdentity
{
    /// <summary>
    /// Library version = hash(rooms.vmf + library.json). The VMF enters as its SHA-256, the
    /// digest a pack's namespace records, so an uploaded pack (which carries no VMF) and a
    /// bake of the same source get the same version.
    /// </summary>
    public static string LibraryVersion(string vmfSha256, string? libraryJson) =>
        Hex(Encoding.UTF8.GetBytes($"{vmfSha256.ToLowerInvariant()}\n{Hex(Encoding.UTF8.GetBytes(libraryJson ?? ""), 64)}"), 16);

    public static string LibraryVersionOf(LibrarySource source) =>
        LibraryVersion(Hex(File.ReadAllBytes(source.Vmf), 64), File.Exists(source.Manifest) ? File.ReadAllText(source.Manifest) : null);

    /// <summary>Level hash = hash(pack, tileset, depth, seed, difficulty, linker identity), 16 hex digits.</summary>
    /// <param name="packVersionId">The pack version's row id: a level hash includes the pack version (§9.4).</param>
    /// <param name="packId">The tools' own pack id.</param>
    public static string LevelHash(long packVersionId, string packId, string tileset, int depth, ulong seed, int difficulty, string linkerIdentity) =>
        Hex(Encoding.UTF8.GetBytes($"{packVersionId}|{packId}|{tileset}|{depth}|{seed}|{difficulty}|{linkerIdentity}"), 16);

    /// <summary><c>&lt;mod&gt;-&lt;depth&gt;-&lt;hash&gt;</c> (Q18).</summary>
    public static string MapName(string mod, int depth, string hash) => $"{mod}-{depth}-{hash}";

    public static string Hex(byte[] bytes, int digits) => Convert.ToHexStringLower(SHA256.HashData(bytes))[..digits];

    public static string Sha256(Stream s) => Convert.ToHexStringLower(SHA256.HashData(s));
}
