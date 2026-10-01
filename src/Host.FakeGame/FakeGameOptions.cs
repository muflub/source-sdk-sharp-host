using System.Net;
using Microsoft.Extensions.Configuration;
using SourceSharp.Host.Relay;

namespace SourceSharp.Host.FakeGame;

/// <summary>The fake's own knobs (FAKEGAME_* environment). The game API's are DESCENT_*, the relay's RELAY_*.</summary>
public sealed class FakeGameOptions
{
    /// <summary>FakeGameControl gRPC (h2c). Loopback by default; see <see cref="ControlAnyAddress"/>.</summary>
    public string ControlListen { get; set; } = "127.0.0.1:5020";
    /// <summary>Live scripts reach the control port from outside the pod: bind 0.0.0.0 on the same port.</summary>
    public bool ControlAnyAddress { get; set; }
    public int FrameHz { get; set; } = 60;
    public int HeartbeatMs { get; set; } = 5000;
    public int LeaseTtlMs { get; set; } = 30_000;
    /// <summary>The SDK's main-thread stall limit: Hang stops pumping, the heartbeats stop after this.</summary>
    public int MainStallMs { get; set; } = 15_000;
    /// <summary>A UDP player silent this long has left (the toy protocol has no disconnect).</summary>
    public int PlayerIdleMs { get; set; } = 10_000;
    /// <summary>The class a new character on the hub gets.</summary>
    public string ClassName { get; set; } = "scout";
    /// <summary>
    /// Each event is also written to stderr as it happens, so a pod's log shows why it stalled
    /// even after the pod is gone. The process turns it on; in-process facts leave it off.
    /// </summary>
    public bool EchoEvents { get; set; }

    public IPEndPoint Control
    {
        get
        {
            var ep = IPEndPoint.Parse(ControlListen);
            return ControlAnyAddress ? new IPEndPoint(IPAddress.Any, ep.Port) : ep;
        }
    }
}

/// <summary>Everything the fake reads at start, from a game pod's arguments and environment.</summary>
public sealed record FakeGameConfig(FakeGameOptions Fake, RelayOptions Relay, int GamePort, string Map, string InstanceId,
    IReadOnlyList<string> Args, Func<string, string?> Env)
{
    /// <summary>What the fake overrode in its environment (logged once at start).</summary>
    public string? Note { get; init; }

    /// <summary>
    /// <paramref name="args"/> as the pod passes them to the engine (<c>-port</c>, <c>+map</c>,
    /// <c>+descent_instance</c>); <paramref name="env"/> the process environment.
    /// </summary>
    public static FakeGameConfig From(IReadOnlyList<string> args, IReadOnlyDictionary<string, string?> env)
    {
        static IConfiguration Prefixed(IReadOnlyDictionary<string, string?> env, string prefix) =>
            new ConfigurationBuilder().AddInMemoryCollection(env
                .Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(kv => new KeyValuePair<string, string?>(kv.Key[prefix.Length..].Replace("__", ":"), kv.Value))).Build();

        var fake = new FakeGameOptions();
        Prefixed(env, "FAKEGAME_").Bind(fake);
        var relay = new RelayOptions();
        Prefixed(env, "RELAY_").Bind(relay);
        string? note = null;
        if (relay.Mode != "Loopback")
        {
            // Interpose hooks the engine's recvfrom/sendto inside Host.Launcher; this is a managed
            // process with a managed engine stand-in, so nothing can be interposed: always Loopback.
            note = $"RELAY_Mode={relay.Mode} ignored: the fake game relays in Loopback mode";
            relay.Mode = "Loopback";
        }
        relay.Validate();

        string? Arg(string name)
        {
            for (var i = 0; i + 1 < args.Count; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }
        var port = int.TryParse(Arg("-port"), out var p) ? p : IPEndPoint.Parse(relay.EngineEndpoint).Port;
        var instance = Arg("+descent_instance") ?? env.GetValueOrDefault("DESCENT_INSTANCE_ID") ?? "fake";
        return new FakeGameConfig(fake, relay, port, Arg("+map") ?? "fake_map", instance, args,
            k => env.TryGetValue(k, out var v) ? v : null) { Note = note };
    }
}
