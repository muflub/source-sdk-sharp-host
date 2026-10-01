namespace Descent.Service;

/// <summary>
/// D-H13: the service as one local process for rapid game testing. Everything under one
/// directory, loopback only, no Kubernetes (LocalInstanceHost), no gateway (the control client
/// is a no-op), a boot timeout long enough for a developer to start the game by hand.
/// </summary>
public static class LocalProfile
{
    public static Dictionary<string, string?> Settings(string dir, int gameApiPort = 5001, int adminPort = 5000)
    {
        dir = Path.GetFullPath(dir);
        return new()
        {
            ["Instances:Host"] = "Local",
            ["Instances:LocalDir"] = dir,
            ["Instances:BootTimeout"] = "1.00:00:00",
            ["Instances:VerifyModLabel"] = "false",
            ["Data:Path"] = Path.Combine(dir, "host.db"),
            ["Data:BackupPath"] = Path.Combine(dir, "backups"),
            ["Modules:Path"] = Path.Combine(dir, "modules"),
            ["MapPool:MapsPath"] = Path.Combine(dir, "maps"),
            ["MapPool:LibraryPath"] = Path.Combine(dir, "maps", "library"),
            ["Listen:Admin"] = $"127.0.0.1:{adminPort}",
            ["Listen:Internal"] = "",
            ["Listen:GameApi"] = $"127.0.0.1:{gameApiPort}",
            ["Listen:GatewayApi"] = "127.0.0.1:0",
            ["Listen:FastDl"] = "127.0.0.1:0",
            ["Listen:GatewayControl"] = "",
        };
    }

    /// <summary>`Descent.Service --local [dir]` → the profile's settings as command-line configuration.</summary>
    public static string[] Expand(string[] args)
    {
        var i = Array.IndexOf(args, "--local");
        if (i < 0) return args;
        var dir = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : "bin/local";
        var rest = args.Where((_, j) => j != i && !(j == i + 1 && args[j] == dir)).ToList();
        foreach (var (k, v) in Settings(dir)) rest.Add($"--{k}={v}");
        if (!rest.Any(a => a.StartsWith("--environment"))) rest.AddRange(["--environment", "Development"]);
        return [.. rest];
    }
}
