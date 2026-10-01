using SourceSharp.Host.Abstractions;

namespace SourceSharp.Host.Instances;

/// <summary>
/// Plan §7.1 + D-H10/D-H11, pure: the pod a game instance runs in (one container: the launcher
/// hosts the engine and the relay), from config and its row.
/// The token goes into the Secret only; the pod refers to it by name.
/// </summary>
public static class GamePodBuilder
{
    public const string SecretKey = "token";
    public const string GameVolume = "game";
    public const string ContentVolume = "content";
    public const string MapsOverlayVolume = "maps-overlay";
    public const string TmpVolume = "tmp";

    public static string PodName(InstanceKind kind, string instanceId) =>
        $"descent-{KindLabel(kind)}-{instanceId.ToLowerInvariant()}";

    public static string SecretName(string podName) => podName + "-token";

    public static string KindLabel(InstanceKind kind) => kind == InstanceKind.Hub ? "hub" : "level";

    /// <summary>The map a level runs: "&lt;mod&gt;-&lt;depth&gt;-&lt;hash&gt;" (Q18); the hub runs the town map.</summary>
    public static string MapName(ModOptions mod, InstanceRecord row) =>
        row.Kind == InstanceKind.Hub ? mod.TownMap : $"{mod.Name}-{row.Depth}-{row.LevelHash}";

    public static PodSpec Build(InstanceOptions o, ModOptions mod, InstanceRecord row, string token)
    {
        if (row.Kind == InstanceKind.Level && string.IsNullOrEmpty(row.LevelHash))
            throw new InvalidOperationException($"level instance {row.Id} has no level hash");

        var name = PodName(row.Kind, row.Id);
        var secret = SecretName(name);
        var map = MapName(mod, row);
        var modDir = $"/game/mods/{mod.Name}";
        var hub = row.Kind == InstanceKind.Hub;

        var labels = new Dictionary<string, string>
        {
            [GamePodLabels.App] = GamePodLabels.AppValue,
            [GamePodLabels.Instance] = row.Id,
            [GamePodLabels.Kind] = KindLabel(row.Kind),
        };

        var init = new List<ContainerSpec>();
        if (!o.SkipModInit)
            init.Add(new ContainerSpec("mod", o.ModImage.ToString(),
                ["sh", "-c", "cp -a /game/. /out/"], [], [], [],
                [new MountSpec(GameVolume, "/out")]));
        if (!hub && !o.SkipLevelFetch)
            init.Add(new ContainerSpec("fetch-level", string.IsNullOrEmpty(o.FetchLevelImage) ? o.EngineImage : o.FetchLevelImage,
                ["/opt/descent/fetch-level.sh", map], [],
                [new EnvVar("DESCENT_INTERNAL_MAPS", o.InternalMapsUrl)], [],
                [new MountSpec(MapsOverlayVolume, "/game/maps-overlay")]));

        List<string> args =
        [
            .. o.Args,
            "-game", modDir, "-console", "-port", o.GamePort.ToString(), "-ip", "0.0.0.0",
            "+maxplayers", (hub ? o.HubMaxPlayers : o.LevelMaxPlayers).ToString(),
            "+map", map, "+descent_instance", row.Id,
        ];

        var game = new ContainerSpec("game", o.EngineImage,
            ["/opt/descent/game-entrypoint.sh"], args,
            [
                new EnvVar("DESCENT_SERVICE", o.ServiceAddress),
                // The entrypoint tells SourceSharp's Bootstrap which mods.yaml entry to load
                // (SOURCESHARP_MOD); without it Bootstrap looks for "descent" and refuses (H0a on k3s).
                new EnvVar("DESCENT_MOD", mod.Name),
                new EnvVar("DESCENT_INSTANCE_ID", row.Id),
                new EnvVar("DESCENT_INSTANCE_TOKEN", FromSecret: new SecretKeyRef(secret, SecretKey)),
                // The SDK dials PeerInfo here; since D-H11 the launcher serves it in the same process.
                new EnvVar("DESCENT_SIDECAR_INFO", $"127.0.0.1:{o.InfoPort}"),
                // The launcher binds LAUNCHER_* / RELAY_* with .NET configuration: the key after the
                // prefix must match the property name (EngineDir), so no SHOUTING_SNAKE here.
                new EnvVar("LAUNCHER_EngineDir", o.EngineDir),
                new EnvVar("RELAY_Listen", $"0.0.0.0:{o.RelayPort}"),
                new EnvVar("RELAY_InfoListen", $"127.0.0.1:{o.InfoPort}"),
                new EnvVar("RELAY_Health", $"0.0.0.0:{o.HealthPort}"),
                new EnvVar("RELAY_EngineEndpoint", $"127.0.0.1:{o.GamePort}"),
                new EnvVar("RELAY_Mode", o.RelayMode),
                .. o.ExtraEnv.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new EnvVar(kv.Key, kv.Value)),
                new EnvVar("TMPDIR", "/tmp/game"),
                new EnvVar("SOURCESHARP_DEVAPI_DIR", "/tmp/devapi"),
            ],
            // D-H10/D-H11: the launcher's relay (h2c) and health are the only ports; the
            // engine's UDP 27015 never leaves the pod, the relay reaches it on loopback.
            [new PortSpec("relay", o.RelayPort), new PortSpec("health", o.HealthPort)],
            [
                new MountSpec(GameVolume, "/game"),
                new MountSpec(ContentVolume, "/game/content", ReadOnly: true),
                new MountSpec(MapsOverlayVolume, $"{modDir}/maps/pool"),
                new MountSpec(TmpVolume, "/tmp"),
            ],
            hub ? o.HubResources : o.LevelResources,
            Readiness: new ProbeSpec(HttpPath: "/healthz", HttpPort: o.HealthPort, PeriodSeconds: 2),
            // restartPolicy Never: a failed liveness probe ends the pod, which the manager sees as crashed.
            Liveness: new ProbeSpec(HttpPath: "/healthz", HttpPort: o.HealthPort, PeriodSeconds: 10, FailureThreshold: 3));

        return new PodSpec(
            name, labels, init, [game],
            [
                new VolumeSpec(GameVolume),
                new VolumeSpec(ContentVolume, o.ContentClaim, ReadOnly: true),
                new VolumeSpec(MapsOverlayVolume),
                new VolumeSpec(TmpVolume),
            ],
            new Dictionary<string, string>(o.NodeSelector),
            (int)o.ReapGrace.TotalSeconds,
            new SecretSpec(secret, new Dictionary<string, string> { [SecretKey] = token }),
            [.. o.Tolerations]);
    }
}
