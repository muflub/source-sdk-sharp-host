namespace SourceSharp.Host.Gateway;

/// <summary>The gateway's configuration (plan §8): appsettings + GATEWAY_* environment, bound at the root.</summary>
public sealed class GatewayOptions
{
    public string GatewayId { get; set; } = Environment.MachineName;
    /// <summary>The public UDP socket (behind the LoadBalancer, externalTrafficPolicy Local).</summary>
    public string Public { get; set; } = "0.0.0.0:27015";
    /// <summary>GatewayControl gRPC, reachable by the service only.</summary>
    public string Control { get; set; } = "0.0.0.0:5003";
    /// <summary>/healthz and /metrics for the kubelet.</summary>
    public string Health { get; set; } = "0.0.0.0:5006";
    /// <summary>The service's gateway API (GatewayHealth, GatewayEvents).</summary>
    public string Service { get; set; } = "http://tf2-descent-service:5002";
    public TimeSpan SessionExpiry { get; set; } = TimeSpan.FromSeconds(60);
    /// <summary>Handshakes (getchallenges) accepted per client IP per second; more are dropped (Q24).</summary>
    public int HandshakesPerSecond { get; set; } = 2;
    public int HoldPackets { get; set; } = 64;
    public int HoldBytes { get; set; } = 64 * 1024;
    public TimeSpan HoldTime { get; set; } = TimeSpan.FromSeconds(15);
    /// <summary>The identity-first handshake (§8.1b). Off until the H0f capture shows the 2013 client
    /// re-sends connect on a second challenge (docs/net-protocol.md, open questions).</summary>
    public bool IdentityFirst { get; set; }
    /// <summary>The connectionless handshake format identity-first parses: Source (default) or Toy (the fake tier).</summary>
    public string Wire { get; set; } = "Source";
    public TimeSpan PingInterval { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan StatsInterval { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>How often expiry, hold and retry deadlines are checked, on the relay's clock.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMilliseconds(250);
    /// <summary>After a PeerRelay stream fails before Opened, the wait before the next attempt (the session holds meanwhile).</summary>
    public TimeSpan StreamRetry { get; set; } = TimeSpan.FromMilliseconds(500);
    /// <summary>One connection per SteamID (plan_steam_auth S-17): a live session keeps its SteamID until it has been
    /// silent this long; until then a second connect for it is refused. Live-tunable (S-21).</summary>
    public TimeSpan PresenceTimeout { get; set; } = TimeSpan.FromSeconds(15);
    /// <summary>Temp bans after repeated handshake failures (S-15), per source IP and per SteamID. Live-tunable.</summary>
    public TempBanOptions TempBan { get; set; } = new();
    /// <summary>The Steam game server in this process (S-1 … S-21).</summary>
    public SteamOptions Steam { get; set; } = new();
}

public sealed class TempBanOptions
{
    public StrikeRule Ip { get; set; } = new();
    public StrikeRule SteamId { get; set; } = new();
}

/// <summary><see cref="Failures"/> within <see cref="Window"/> ban for <see cref="Base"/>, doubling for each ban within
/// <see cref="RepeatWindow"/> of the last, up to <see cref="Cap"/> (S-15).</summary>
public sealed class StrikeRule
{
    public int Failures { get; set; } = 5;
    public TimeSpan Window { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan Base { get; set; } = TimeSpan.FromMinutes(10);
    public TimeSpan Cap { get; set; } = TimeSpan.FromHours(24);
    public TimeSpan RepeatWindow { get; set; } = TimeSpan.FromHours(24);
}

public enum SteamMode { Off, Fake, Steamworks }
public enum A2sAnswerer { Gateway, Library }
public enum ReauthUnanswered { Kick, Trust }

/// <summary>Bound from <c>Steam:*</c> (env <c>GATEWAY_Steam__*</c>). Deploy-time unless marked live (S-21).</summary>
public sealed class SteamOptions
{
    /// <summary>Steamworks (default; the gateway image carries libsteam_api): validate with Steam. Fake: the scriptable
    /// fake Steam (the fake tier). Off: no validation (facts and dev runs without Steam).</summary>
    public SteamMode Mode { get; set; } = SteamMode.Steamworks;
    /// <summary>The mods' steam_appid (S-9).</summary>
    public uint AppId { get; set; } = 243750;
    /// <summary>A GSLT; empty logs on anonymously (S-9, ruled: anonymous first).</summary>
    public string LoginToken { get; set; } = "";
    /// <summary>libsteam_api.so; empty probes next to the gateway.</summary>
    public string NativeLibraryPath { get; set; } = "";
    /// <summary>Who answers A2S on the public socket (S-19): the gateway's responder, or the Steam library.</summary>
    public A2sAnswerer A2s { get; set; } = A2sAnswerer.Gateway;
    /// <summary>How often the library's unprompted packets (heartbeats) are drained to the public socket.</summary>
    public TimeSpan OutgoingTick { get; set; } = TimeSpan.FromMilliseconds(50);
    /// <summary>List the server in Steam's browser (plan_steam_auth S-18) once the service has sent its entry.</summary>
    public bool Advertise { get; set; } = true;
    /// <summary>Between logon attempts while Steam is unreachable.</summary>
    public TimeSpan LogonRetry { get; set; } = TimeSpan.FromSeconds(30);

    // ---- live-tunable from the admin Config page (S-21)
    /// <summary>A Begin with no verdict by then is a timeout (S-3).</summary>
    public TimeSpan AuthTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan ReauthTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public int ReauthAttempts { get; set; } = 2;
    public ReauthUnanswered ReauthUnanswered { get; set; } = ReauthUnanswered.Kick;
    /// <summary>How long after its epoch a claimed ticket matching a valid row is readmitted once (S-11).</summary>
    public TimeSpan ClaimedTrust { get; set; } = TimeSpan.FromHours(6);
    public TimeSpan KickGrace { get; set; } = TimeSpan.FromSeconds(2);
    /// <summary>The reject a client sees while Steam is unavailable (S-8).</summary>
    public string UnavailableReason { get; set; } = "Steam is having issues; try again in a few minutes";
}

/// <summary>ValidateOnStart's rules (and the Config page's), pure so a fact can drive them (S-21).</summary>
public static class GatewayOptionsValidator
{
    public static IReadOnlyList<string> Validate(GatewayOptions o)
    {
        var errors = new List<string>();
        void Positive(string name, TimeSpan v) { if (v <= TimeSpan.Zero) errors.Add($"{name} must be positive, got {v}"); }

        Positive("PresenceTimeout", o.PresenceTimeout);
        foreach (var (kind, r) in new[] { ("Ip", o.TempBan.Ip), ("SteamId", o.TempBan.SteamId) })
        {
            if (r.Failures < 1) errors.Add($"TempBan.{kind}.Failures must be at least 1, got {r.Failures}");
            Positive($"TempBan.{kind}.Window", r.Window);
            Positive($"TempBan.{kind}.Base", r.Base);
            Positive($"TempBan.{kind}.RepeatWindow", r.RepeatWindow);
            if (r.Cap < r.Base) errors.Add($"TempBan.{kind}.Cap ({r.Cap}) must be at least Base ({r.Base})");
        }
        var s = o.Steam;
        Positive("Steam.AuthTimeout", s.AuthTimeout);
        Positive("Steam.ReauthTimeout", s.ReauthTimeout);
        Positive("Steam.ClaimedTrust", s.ClaimedTrust);
        Positive("Steam.OutgoingTick", s.OutgoingTick);
        Positive("Steam.LogonRetry", s.LogonRetry);
        if (s.KickGrace < TimeSpan.Zero) errors.Add($"Steam.KickGrace must not be negative, got {s.KickGrace}");
        if (s.ReauthAttempts < 1) errors.Add($"Steam.ReauthAttempts must be at least 1, got {s.ReauthAttempts}");
        if (string.IsNullOrWhiteSpace(s.UnavailableReason)) errors.Add("Steam.UnavailableReason must not be empty: it is what the client shows");
        if (s.AppId == 0) errors.Add("Steam.AppId must be set");
        return errors;
    }
}
