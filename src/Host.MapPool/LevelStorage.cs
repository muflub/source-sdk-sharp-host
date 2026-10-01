using System.Text.RegularExpressions;
using ICSharpCode.SharpZipLib.BZip2;
using SourceSharp.Host.Abstractions;

namespace SourceSharp.Host.MapPool;

/// <summary>
/// The <c>maps</c> volume (plan §9.1 Link, Q18): <c>levels/&lt;mod&gt;-&lt;depth&gt;-&lt;hash&gt;.{bsp,bsp.bz2,nav3d,map2d,yaml}</c>
/// and <c>packs/&lt;mod&gt;/&lt;library&gt;/&lt;version&gt;.roompack</c>. A level's files are written
/// once and never change (§9.4: levels are immutable).
/// </summary>
public sealed partial class LevelStorage(MapPoolOptions options)
{
    public string Root { get; } = options.MapsPath;
    public string LevelsDirectory => Path.Combine(Root, "levels");
    public string PacksDirectory => Path.Combine(Root, "packs");
    public string StagingDirectory => Path.Combine(Root, "staging");

    /// <summary>What a level's files are called: the four the linker writes, and the bz2 fastdl serves.</summary>
    public static readonly IReadOnlyList<string> LevelExtensions = [".bsp", ".bsp.bz2", ".nav3d", ".map2d", ".yaml"];

    /// <summary>The extensions the internal endpoint serves to game pods' init containers (Q18).</summary>
    public static readonly IReadOnlyList<string> InternalExtensions = [".bsp", ".nav3d", ".map2d"];

    [GeneratedRegex(@"^(?<mod>[a-z0-9_]+)-(?<depth>[1-9][0-9]{0,2})-(?<hash>[0-9a-f]{16})$")]
    private static partial Regex MapNamePattern();

    /// <summary>A level's map name, <c>&lt;mod&gt;-&lt;depth&gt;-&lt;hash&gt;</c>, taken apart; false for anything else.</summary>
    public static bool TryParseMapName(string name, out string mod, out int depth, out string hash)
    {
        var m = MapNamePattern().Match(name);
        mod = m.Success ? m.Groups["mod"].Value : "";
        depth = m.Success ? int.Parse(m.Groups["depth"].Value) : 0;
        hash = m.Success ? m.Groups["hash"].Value : "";
        return m.Success;
    }

    public string LevelFile(string mapName, string extension) => Path.Combine(LevelsDirectory, mapName + extension);

    public string PackFile(string mod, string library, int version) =>
        Path.Combine(PacksDirectory, mod, library, $"{version}.roompack");

    public string StagingFor(string mapName) => Path.Combine(StagingDirectory, mapName + "-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>
    /// Moves a link's files into <c>levels/</c> and writes the BSP's bz2 beside it; returns the
    /// bytes stored. Each file lands by rename, so a reader sees a whole file or none.
    /// </summary>
    public async Task<long> StoreAsync(LevelFiles files, CancellationToken ct = default)
    {
        Directory.CreateDirectory(LevelsDirectory);
        var bz2Temp = LevelFile(files.MapName, ".bsp.bz2.writing");
        await using (var input = File.OpenRead(files.Bsp))
        await using (var output = File.Create(bz2Temp))
        await using (var bz = new BZip2OutputStream(output) { IsStreamOwner = false })
            await input.CopyToAsync(bz, ct);
        File.Move(bz2Temp, LevelFile(files.MapName, ".bsp.bz2"), overwrite: true);

        long bytes = new FileInfo(LevelFile(files.MapName, ".bsp.bz2")).Length;
        foreach (var (source, ext) in new[] { (files.Bsp, ".bsp"), (files.Nav3d, ".nav3d"), (files.Map2d, ".map2d"), (files.LevelYaml, ".yaml") })
        {
            if (source is null) continue;
            var target = LevelFile(files.MapName, ext);
            File.Move(source, target, overwrite: true);
            bytes += new FileInfo(target).Length;
        }
        return bytes;
    }

    /// <summary>Deletes every file of a level; returns how many there were.</summary>
    public int DeleteLevel(string mapName)
    {
        var n = 0;
        foreach (var ext in LevelExtensions)
        {
            var f = LevelFile(mapName, ext);
            if (File.Exists(f)) { File.Delete(f); n++; }
        }
        return n;
    }

    public bool LevelExists(string mapName) => File.Exists(LevelFile(mapName, ".bsp.bz2"));

    /// <summary>bzip2-decompresses a stream (for facts and the admin's download check).</summary>
    public static byte[] Decompress(Stream bz2)
    {
        using var input = new BZip2InputStream(bz2);
        using var ms = new MemoryStream();
        input.CopyTo(ms);
        return ms.ToArray();
    }
}
