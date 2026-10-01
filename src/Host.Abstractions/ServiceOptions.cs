using System.Net;

namespace SourceSharp.Host.Abstractions;

/// <summary>
/// The service's configuration (plan H1c): appsettings.json + DESCENT_* environment, bound at the root,
/// validated on start. Runtime-editable values (ModImage, pool K, timers, caps) are
/// overridden from the settings store, never written back to the file.
/// </summary>
public sealed class ServiceOptions
{
    public ModOptions Mod { get; set; } = new();
    public ListenOptions Listen { get; set; } = new();
    public InstanceOptions Instances { get; set; } = new();
    public LeaseOptions Lease { get; set; } = new();
    public ReserveOptions Reserve { get; set; } = new();
    public MapPoolOptions MapPool { get; set; } = new();
    public DataOptions Data { get; set; } = new();
    public ModuleOptions Modules { get; set; } = new();
    public AdminOptions Admin { get; set; } = new();
    public TravelOptions Travel { get; set; } = new();
    public MaintenanceOptions Maintenance { get; set; } = new();
}

public sealed class MaintenanceOptions
{
    /// <summary>D-H4: vendors rolled hourly per hub; the names are the rules module's vendor keys.</summary>
    public List<string> Vendors { get; set; } = ["armory", "medic", "engineer"];
    public TimeSpan VendorRoll { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan Reconcile { get; set; } = TimeSpan.FromHours(24);
    public TimeSpan Backup { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan TerminalItemRetention { get; set; } = TimeSpan.FromDays(30);
}

public sealed class TravelOptions
{
    /// <summary>D-H12: a character's level claims end after this long without a lease anywhere.</summary>
    public TimeSpan SessionIdle { get; set; } = TimeSpan.FromMinutes(30);
}

public sealed class ModOptions
{
    /// <summary>The mod id: map names are "&lt;Name&gt;-&lt;depth&gt;-&lt;hash&gt;" and a mod image's label must equal it.</summary>
    public string Name { get; set; } = "descent";
    public string TownMap { get; set; } = "descent_town";
}

public sealed class ListenOptions
{
    /// <summary>The admin UI. Loopback only (Q20): anything else fails ValidateOnStart.</summary>
    public string Admin { get; set; } = "127.0.0.1:5000";
    /// <summary>/internal (maps to init containers, bake uploads) and /metrics, namespace-only by NetworkPolicy. Empty = off.</summary>
    public string Internal { get; set; } = "";
    public string GameApi { get; set; } = "0.0.0.0:5001";
    public string GatewayApi { get; set; } = "0.0.0.0:5002";
    /// <summary>The public fastdl listener: *.bsp.bz2 and nothing else (Q18).</summary>
    public string FastDl { get; set; } = "0.0.0.0:5004";
    /// <summary>The gateway's control endpoint, as the service dials it.</summary>
    public string GatewayControl { get; set; } = "http://descent-gateway-control:5003";
}

public sealed class ImageRef
{
    public string Registry { get; set; } = "";
    public string Name { get; set; } = "";
    public string Tag { get; set; } = "latest";
    public string? Digest { get; set; }

    public override string ToString()
    {
        var repo = string.IsNullOrEmpty(Registry) ? Name : $"{Registry}/{Name}";
        return string.IsNullOrEmpty(Digest) ? $"{repo}:{Tag}" : $"{repo}@{Digest}";
    }
}

public sealed class PodResources
{
    public string CpuRequest { get; set; } = "1";
    public string MemoryRequest { get; set; } = "1536Mi";
    public string CpuLimit { get; set; } = "1";
    public string MemoryLimit { get; set; } = "1536Mi";
}

public sealed class InstanceOptions
{
    /// <summary>
    /// How the pod's relay hands datagrams to the engine (D-H10 as amended): "Interpose" (default) —
    /// the launcher hooks the engine's recvfrom/sendto, so the engine sees each client's real
    /// ip:port; "Loopback" — a 127.x address per client (the fake tier, whose managed sockets
    /// cannot be hooked).
    /// </summary>
    public string RelayMode { get; set; } = "Interpose";
    /// <summary>TIER=fake: no fetch-level init container (seeded levels have no files; the fake loads no map).</summary>
    public bool SkipLevelFetch { get; set; }
    /// <summary>Extra environment for the game container (TIER=fake: FAKEGAME_* switches). Development only.</summary>
    public Dictionary<string, string> ExtraEnv { get; set; } = [];
    /// <summary>"Kubernetes" (production), or "Local" (D-H13: no pods; the developer's game connects with a connection file).</summary>
    public string Host { get; set; } = "Kubernetes";
    /// <summary>Local mode: where the connection files (local-host.json, local-level-&lt;id&gt;.json) are written.</summary>
    public string LocalDir { get; set; } = "bin/local";

    public string Namespace { get; set; } = "descent";
    /// <summary>This repo's engine image (srcds + entrypoint), or the fake image in TIER=fake.</summary>
    public string EngineImage { get; set; } = "descent-engine:latest";
    /// <summary>The mod image (D-H6): runtime config, never baked into an image.</summary>
    public ImageRef ModImage { get; set; } = new();
    /// <summary>The dedicated-server flag set (§7.1), owned here so the sharp repo can change it without a release.</summary>
    public List<string> Args { get; set; } =
        ["-nohltv", "+tv_enable", "0", "-devapi", "-devapisocket", "-condebug", "-nomessagebox", "-nocrashdialog", "-assertlog"];
    public PodResources LevelResources { get; set; } = new();
    public PodResources HubResources { get; set; } = new() { CpuRequest = "2", CpuLimit = "2", MemoryRequest = "3Gi", MemoryLimit = "3Gi" };
    public int Hubs { get; set; } = 1;
    public int HubMaxPlayers { get; set; } = 32;
    public int LevelMaxPlayers { get; set; } = 4;
    public int MaxLevelPods { get; set; } = 16;
    public int WarmSpares { get; set; }
    public int GamePort { get; set; } = 27015;
    public TimeSpan BootTimeout { get; set; } = TimeSpan.FromSeconds(120);
    public TimeSpan EmptyGrace { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan CorpseGrace { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan ReapGrace { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(5);
    public int SuspectAfterMissed { get; set; } = 3;
    public int ReapAfterMissed { get; set; } = 6;
    public string ContentClaim { get; set; } = "content";
    /// <summary>What a pod dials for the game API.</summary>
    public string ServiceAddress { get; set; } = "descent-service:5001";
    /// <summary>What a pod's init container fetches levels from (/internal/maps).</summary>
    public string InternalMapsUrl { get; set; } = "http://descent-service-internal:5000";
    public Dictionary<string, string> NodeSelector { get; set; } = [];
    /// <summary>TIER=fake: the engine image is Host.FakeGame and the mod init container is skipped.</summary>
    public bool SkipModInit { get; set; }

    // D-H10 + D-H11: the relay is served by the launcher inside the game container (one container per pod).
    /// <summary>PeerRelay gRPC (h2c), the only port the gateway reaches.</summary>
    public int RelayPort { get; set; } = 5010;
    /// <summary>PeerInfo gRPC, loopback only: the game's SDK asks it for a peer's real address.</summary>
    public int InfoPort { get; set; } = 5011;
    /// <summary>The launcher's /healthz: the game container's readiness and liveness probe.</summary>
    public int HealthPort { get; set; } = 5012;
    /// <summary>Where the engine lives in the engine image (the launcher's LAUNCHER_EngineDir).</summary>
    public string EngineDir { get; set; } = "/opt/srcds";
    /// <summary>The image the fetch-level init container runs; empty = the engine image.</summary>
    public string FetchLevelImage { get; set; } = "";
    /// <summary>§7.5: passed through so game pods can be pinned to a node pool.</summary>
    public List<PodToleration> Tolerations { get; set; } = [];
    /// <summary>§7.4: an adopted pod has this long to reopen its stream before it is treated as crashed.</summary>
    public TimeSpan AdoptReconnect { get; set; } = TimeSpan.FromSeconds(60);
    /// <summary>§7.3: hub re-creation backoff doubles from Min to Max; FailureLimit failures within FailureWindow stop it (audited).</summary>
    public TimeSpan HubBackoffMin { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan HubBackoffMax { get; set; } = TimeSpan.FromSeconds(30);
    public int HubFailureLimit { get; set; } = 5;
    public TimeSpan HubFailureWindow { get; set; } = TimeSpan.FromMinutes(5);
    /// <summary>§H1d: read the mod image's org.sourcesharp.mod label from its registry and refuse a mismatch.</summary>
    public bool VerifyModLabel { get; set; } = true;
    /// <summary>Registries reached over plain http besides localhost, 127.0.0.1 and *.localhost.</summary>
    public List<string> InsecureRegistries { get; set; } = [];
    /// <summary>The Kubernetes API: empty = in-cluster config (the service's ServiceAccount), else a kubeconfig path (dev).</summary>
    public string Kubeconfig { get; set; } = "";
    /// <summary>How often the manager's tick runs (creation, boot timeouts, heartbeats, empty grace).</summary>
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(1);
}

public sealed class LeaseOptions
{
    public TimeSpan Ttl { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan ExpirySweep { get; set; } = TimeSpan.FromSeconds(5);
}

public sealed class ReserveOptions
{
    /// <summary>Items per tier, by the rules' tier name (§5.2a). Unnamed tiers get 0.</summary>
    public Dictionary<string, int> Sizes { get; set; } = new()
    {
        ["Stock"] = 12, ["Vintage"] = 12, ["Strange"] = 6, ["Unusual"] = 2, ["Australium"] = 0,
    };
    /// <summary>Refill below this fraction of a tier's size.</summary>
    public double LowWater { get; set; } = 1.0 / 3.0;
    /// <summary>A checkpoint that raises the level by this much over a tier's rolled level sweeps the tier.</summary>
    public int StaleLevels { get; set; } = 2;
}

public sealed class MapPoolOptions
{
    public int Depths { get; set; } = 15;
    public int DefaultPerDepth { get; set; } = 3;
    public Dictionary<int, int> PerDepth { get; set; } = [];
    public int ConcurrentLinks { get; set; } = 2;
    public bool AutoActivateBakes { get; set; }
    public int KeepRetiredPerDepth { get; set; } = 10;
    public int LinkAttempts { get; set; } = 3;
    public string MapsPath { get; set; } = "/maps";
    public string LibraryPath { get; set; } = "/maps/library";
    public int PerDepthFor(int depth) => PerDepth.TryGetValue(depth, out var k) ? k : DefaultPerDepth;
}

public sealed class DataOptions
{
    public string Path { get; set; } = "/data/host.db";
    public string BackupPath { get; set; } = "/data/backups";
    public int HourlyBackups { get; set; } = 24;
    public int DailyBackups { get; set; } = 7;
    public TimeSpan IdempotencyRetention { get; set; } = TimeSpan.FromHours(24);
}

public sealed class ModuleOptions
{
    public string Path { get; set; } = "/data/modules";
    public bool RequireApproval { get; set; }
}

public sealed class AdminOptions
{
    public long MaxUploadBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    public string Actor { get; set; } = "admin@localhost";
}

/// <summary>ValidateOnStart's rules, pure so a fact can drive them.</summary>
public static class ServiceOptionsValidator
{
    public static IReadOnlyList<string> Validate(ServiceOptions o, string environmentName)
    {
        var errors = new List<string>();

        if (!TryParseEndpoint(o.Listen.Admin, out var admin))
            errors.Add($"Listen.Admin '{o.Listen.Admin}' is not ip:port");
        else if (!IPAddress.IsLoopback(admin.Address))
            errors.Add($"Listen.Admin must be a loopback address (Q20), got '{o.Listen.Admin}'");

        foreach (var (name, value) in new[] { ("GameApi", o.Listen.GameApi), ("GatewayApi", o.Listen.GatewayApi), ("FastDl", o.Listen.FastDl) })
            if (!TryParseEndpoint(value, out _))
                errors.Add($"Listen.{name} '{value}' is not ip:port");
        if (o.Listen.Internal.Length > 0 && !TryParseEndpoint(o.Listen.Internal, out _))
            errors.Add($"Listen.Internal '{o.Listen.Internal}' is not ip:port");

        var development = string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase);
        if (!development && o.Instances.EngineImage.Contains("fake", StringComparison.OrdinalIgnoreCase))
            errors.Add($"Instances.EngineImage '{o.Instances.EngineImage}' is the fake game server; it is refused outside Development (§7.6)");
        if (!development && o.Instances.SkipModInit)
            errors.Add("Instances.SkipModInit is a TIER=fake setting; refused outside Development");
        if (!development && o.Instances.SkipLevelFetch)
            errors.Add("Instances.SkipLevelFetch is a TIER=fake setting; refused outside Development");
        if (!development && o.Instances.ExtraEnv.Count > 0)
            errors.Add("Instances.ExtraEnv is a TIER=fake setting; refused outside Development");
        if (o.Instances.RelayMode is not ("Interpose" or "Loopback"))
            errors.Add($"Instances.RelayMode '{o.Instances.RelayMode}' is not Interpose or Loopback");
        if (o.Instances.SkipModInit && o.Instances.RelayMode == "Interpose")
            errors.Add("Instances.RelayMode Interpose needs the launcher's hooks; the fake game (SkipModInit) must use Loopback");

        if (string.IsNullOrWhiteSpace(o.Mod.Name) || o.Mod.Name.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_')))
            errors.Add($"Mod.Name '{o.Mod.Name}' must be lower-case letters, digits or '_'");
        if (o.Instances.MaxLevelPods < 0) errors.Add("Instances.MaxLevelPods must be >= 0");
        if (o.Instances.Hubs < 1) errors.Add("Instances.Hubs must be >= 1");
        if (o.MapPool.Depths < 1) errors.Add("MapPool.Depths must be >= 1");
        if (o.MapPool.ConcurrentLinks < 1) errors.Add("MapPool.ConcurrentLinks must be >= 1");
        if (o.Reserve.LowWater is <= 0 or >= 1) errors.Add("Reserve.LowWater must be in (0, 1)");
        if (o.Lease.Ttl <= o.Instances.HeartbeatInterval) errors.Add("Lease.Ttl must exceed the heartbeat interval");
        return errors;
    }

    /// <summary>ip:port; port 0 (bind any free port) is accepted, which is how facts avoid port races.</summary>
    public static bool TryParseEndpoint(string value, out IPEndPoint endpoint) =>
        IPEndPoint.TryParse(value, out endpoint!) && value.Contains(':');
}
