using System.Diagnostics;
using SourceSharp.Host.Testing;

namespace SourceSharp.Host.Instances.Tests;

/// <summary>
/// deploy/fetch-level.sh as a level pod's init container runs it, under /bin/sh, with the
/// service's internal endpoint served from a file:// tree: the level's files land in OUT_DIR.
/// </summary>
public sealed class FetchLevelFacts : IDisposable
{
    const string Level = "tf2_descent-1-abc";
    readonly string _dir = Path.Combine(Path.GetTempPath(), "fetch-level-facts-" + Guid.NewGuid().ToString("N"));

    public FetchLevelFacts()
    {
        var maps = Path.Combine(_dir, "service", "internal", "maps");
        Directory.CreateDirectory(maps);
        foreach (var ext in new[] { "bsp", "nav3d", "map2d" }) File.WriteAllText(Path.Combine(maps, $"{Level}.{ext}"), ext);
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } }

    (int Exit, string Err) Run(string? outDir)
    {
        var psi = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(RepoFiles.Path_("deploy", "fetch-level.sh"));
        psi.ArgumentList.Add(Level);
        psi.Environment["SOURCESHARP_HOST_INTERNAL_MAPS"] = "file://" + Path.Combine(_dir, "service");
        psi.Environment.Remove("OUT_DIR");
        if (outDir is not null) psi.Environment["OUT_DIR"] = outDir;
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        _ = stdout.Result;
        return (p.ExitCode, stderr.Result);
    }

    [Fact]
    public void The_levels_three_files_land_in_OUT_DIR()
    {
        var overlay = Path.Combine(_dir, "overlay");
        var r = Run(overlay);
        Assert.True(r.Exit == 0, r.Err);
        Assert.Equal(["bsp", "map2d", "nav3d"], Directory.GetFiles(overlay).Select(f => File.ReadAllText(f)).Order());
        Assert.All(new[] { "bsp", "nav3d", "map2d" }, e => Assert.True(File.Exists(Path.Combine(overlay, $"{Level}.{e}")), e));
    }

    [Fact]
    public void Without_OUT_DIR_it_refuses_rather_than_guess()
    {
        var r = Run(null);
        Assert.NotEqual(0, r.Exit);
        Assert.Contains("OUT_DIR", r.Err);
    }
}
