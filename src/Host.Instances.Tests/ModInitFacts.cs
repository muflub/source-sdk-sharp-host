using System.Diagnostics;

namespace SourceSharp.Host.Instances.Tests;

/// <summary>The mod init container's command, run under /bin/sh with /game and /out pointed at temp folders.</summary>
public sealed class ModInitFacts : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "modinit-facts-" + Guid.NewGuid().ToString("N"));
    string Game => Path.Combine(_dir, "game");
    string Out => Path.Combine(_dir, "out");

    public ModInitFacts()
    {
        Directory.CreateDirectory(Path.Combine(Game, "mods", "tf2_descent"));
        Directory.CreateDirectory(Out);
        File.WriteAllText(Path.Combine(Game, "mods", "tf2_descent", "gameinfo.txt"), "GameInfo {}");
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    int Run()
    {
        var cmd = GamePodBuilder.ModInitCommand("tf2_descent").Replace("/game/", Game + "/").Replace("/out", Out);
        var psi = new ProcessStartInfo("/bin/sh") { RedirectStandardError = true };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(cmd);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode;
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    UnixFileMode PoolMode() => File.GetUnixFileMode(Path.Combine(Out, "mods", "tf2_descent", "pool"));

    [Fact]
    public void A_pool_the_image_shipped_unenterable_is_made_traversable()
    {
        if (OperatingSystem.IsWindows()) return;
        var pool = Path.Combine(Game, "mods", "tf2_descent", "pool");
        Directory.CreateDirectory(pool);
        File.SetUnixFileMode(pool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead); // 0744, as 178bfc1
        Assert.Equal(0, Run());
        Assert.True(PoolMode().HasFlag(UnixFileMode.OtherExecute), PoolMode().ToString());
        Assert.True(File.Exists(Path.Combine(Out, "mods", "tf2_descent", "gameinfo.txt"))); // and the copy still happened
    }

    [Fact]
    public void A_pool_the_image_did_not_ship_is_created_traversable()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal(0, Run());
        Assert.True(PoolMode().HasFlag(UnixFileMode.OtherExecute), PoolMode().ToString());
    }
}
