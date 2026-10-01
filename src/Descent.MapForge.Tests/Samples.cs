using Descent.MapForge;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Contracts;
using SourceSharp.Host.Testing;

namespace Descent.MapForge.Tests;

/// <summary>
/// The map tools' own <c>samples/rooms-3x3</c> library, read from <c>$(MapToolsRoot)</c> (never
/// copied into this repo, never written: a copy goes to a temp folder with our library.json
/// beside it). It compiles with no Steam install: its gameinfo mounts only its own folder.
/// A missing root is a failure, never a skip.
/// </summary>
public static class Samples
{
    public const string Mod = "descent";
    public const string Library = "crypt";

    public static string MapToolsRoot { get; } = FindRoot();

    static string FindRoot()
    {
        var props = Path.Combine(RepoFiles.Root, "roots.props");
        if (!File.Exists(props)) throw new InvalidOperationException("roots.props is missing: run `make setup`");
        var text = File.ReadAllText(props);
        var start = text.IndexOf("<MapToolsRoot>", StringComparison.Ordinal) + "<MapToolsRoot>".Length;
        var end = text.IndexOf("</MapToolsRoot>", StringComparison.Ordinal);
        var root = text[start..end];
        if (!Directory.Exists(Path.Combine(root, "samples", "rooms-3x3")))
            throw new InvalidOperationException($"{root}samples/rooms-3x3 is missing");
        return root;
    }

    public static string Rooms3x3 => Path.Combine(MapToolsRoot, "samples", "rooms-3x3");

    /// <summary>library.json for rooms-3x3, valid for depths 1..15 (every rule passes).</summary>
    public const string ManifestJson = """
        {
          "mod": "descent",
          "library": "crypt",
          "rooms": {
            "end":    { "tags": ["start"],               "weight": 1, "depths": [1, 15], "markers": ["light", "info_player_start"] },
            "corner": { "tags": ["stairs", "shrine"],    "weight": 2, "depths": [1, 15], "markers": ["light"] },
            "cross":  { "tags": ["junction", "boss_arena"], "weight": 1, "depths": [1, 15], "markers": ["light"] },
            "tee":    { "tags": ["treasure", "junction"], "weight": 1, "depths": [1, 15], "markers": ["light"] },
            "hall":   { "tags": ["corridor"],            "weight": 3, "depths": [1, 15], "markers": ["light"] }
          }
        }
        """;

    /// <summary>A fresh copy of the sample (rooms.vmf, gameinfo, materials) with <paramref name="manifest"/> as library.json.</summary>
    public static LibrarySource CopyLibrary(string? manifest = ManifestJson, Func<string, string>? editVmf = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "lane-m-lib-" + Guid.NewGuid().ToString("N"), Library);
        Directory.CreateDirectory(dir);
        var vmf = File.ReadAllText(Path.Combine(Rooms3x3, "rooms.vmf"));
        File.WriteAllText(Path.Combine(dir, LibrarySource.VmfName), editVmf is null ? vmf : editVmf(vmf));
        File.Copy(Path.Combine(Rooms3x3, "gameinfo.txt"), Path.Combine(dir, "gameinfo.txt"));
        CopyTree(Path.Combine(Rooms3x3, "materials"), Path.Combine(dir, "materials"));
        if (manifest is not null) File.WriteAllText(Path.Combine(dir, LibrarySource.ManifestName), manifest);
        return new LibrarySource(Mod, Library, dir);
    }

    static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(from)) CopyTree(d, Path.Combine(to, Path.GetFileName(d)));
    }

    static readonly SemaphoreSlim BakeLock = new(1, 1);
    static BakeResult? _baked;

    /// <summary>
    /// rooms-3x3 baked once per test run by the tools' room compiler in-process (unlit, for
    /// speed), under the namespace <see cref="Library"/>. Callers copy it before changing it.
    /// </summary>
    public static async Task<BakeResult> BakedAsync()
    {
        await BakeLock.WaitAsync();
        try
        {
            if (_baked is not null) return _baked;
            var lib = CopyLibrary();
            var output = Path.Combine(Path.GetDirectoryName(lib.Directory)!, "out", Library + ".roompack");
            _baked = await RoomsBaker.BakeAsync(lib, new BakeSettings(lib.Directory, null, output, Threads: 2, Light: false));
            if (!_baked.Ok) throw new InvalidOperationException("the sample did not bake:\n" + _baked.Log);
            return _baked;
        }
        finally { BakeLock.Release(); }
    }

    /// <summary>A private copy of the baked pack.</summary>
    public static async Task<string> PackCopyAsync()
    {
        var baked = await BakedAsync();
        var path = Path.Combine(TempDir(), Library + ".roompack");
        File.Copy(baked.PackPath, path);
        return path;
    }

    public static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "lane-m-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    public static IReadOnlyList<RoomInfo> RoomInfos => LibraryManifest.Parse(ManifestJson).RoomInfos();

    /// <summary>The sample's own level (rooms3x3.yaml) as a plan: row 0 south.</summary>
    public static LevelPlan SampleLevelPlan() => new(3, 3,
    [
        new("corner", 0, 0, 90, "fill"), new("corner", 0, 1, 0, "fill"), new("corner", 0, 2, 90, "fill"),
        new("tee", 1, 0, 270, "fill"), new("cross", 1, 1, 0, "fill"), new("tee", 1, 2, 90, "fill"),
        new("end", 2, 0, 270, "up"), new("hall", 2, 1, 90, "fill"), new("corner", 2, 2, 270, "down"),
    ], "end", "corner");
}
