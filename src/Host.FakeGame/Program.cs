namespace SourceSharp.Host.FakeGame;

/// <summary>
/// The TIER=fake engine image's process: the pod's arguments (-port, +map, +descent_instance, and
/// -hostlocal like the real game) and environment (DESCENT_*, RELAY_*, FAKEGAME_*). Runs until the
/// service's Shutdown (exit 0), a FakeGameControl Crash (its code), or SIGTERM (0).
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var env = new Dictionary<string, string?>(FakeGameHost.ProcessEnvironment());
        env.TryAdd("FAKEGAME_EchoEvents", "true");
        var host = await FakeGameHost.StartAsync(args, env);
        Console.WriteLine($"fakegame {host.InstanceId}: relay {host.Server.Config.Relay.Listen}, control {host.ControlEndPoint}, engine {host.Server.Backend.EndPoint}");
        using var term = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, c =>
        {
            c.Cancel = true;
            _ = host.StopAsync(0);
        });
        var code = await host.Exited;
        if (!host.Server.Config.Fake.EchoEvents)
            foreach (var e in host.Server.Events) Console.WriteLine($"fakegame event: {e}");
        return code;
    }
}
