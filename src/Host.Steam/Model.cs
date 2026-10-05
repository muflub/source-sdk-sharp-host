namespace SourceSharp.Host.Steam;

/// <summary>
/// A Steam identifier (CSteamID on the native side). The wire format is a plain <see cref="ulong"/>.
/// </summary>
public readonly record struct SteamId(ulong Value) : IComparable<SteamId>
{
    public static readonly SteamId Invalid = default;

    public bool IsValid => Value != 0;

    public int CompareTo(SteamId other) => Value.CompareTo(other.Value);

    public override string ToString() => Value == 0 ? "steamid:invalid" : Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static implicit operator ulong(SteamId id) => id.Value;

    public static explicit operator SteamId(ulong value) => new(value);

    public static bool TryParse(string? text, out SteamId id)
    {
        if (ulong.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var v))
        {
            id = new SteamId(v);
            return true;
        }
        id = Invalid;
        return false;
    }
}

/// <summary>An address returned by the Steam masterserver / GetPublicIP.</summary>
public readonly record struct SteamIpAddress(SteamIpType Type, uint IPv4, byte[]? IPv6)
{
    public static readonly SteamIpAddress Unset = new(SteamIpType.IPv4, 0, null);

    public bool IsSet => Type == SteamIpType.IPv4 ? IPv4 != 0 : IPv6 is { Length: > 0 } b && Array.Exists(b, x => x != 0);

    public override string ToString()
    {
        if (!IsSet)
            return "0.0.0.0";
        if (Type == SteamIpType.IPv4)
        {
            // m_unIPv4 is host order.
            return $"{(IPv4 >> 24) & 0xFF}.{(IPv4 >> 16) & 0xFF}.{(IPv4 >> 8) & 0xFF}.{IPv4 & 0xFF}";
        }
        return $"[ipv6:{Convert.ToHexString(IPv6!)}]";
    }
}

/// <summary>A packet the Steam masterserver updater wants sent: UDP datagram <see cref="Data"/> to IPv4 <see cref="IPv4"/>:port (host byte order).</summary>
public readonly record struct SteamOutgoingPacket(byte[] Data, uint IPv4, ushort Port);

/// <summary>Server configuration supplied to <see cref="SteamGameServerHost.StartAsync(GameServerConfiguration, CancellationToken)"/>.</summary>
public sealed record GameServerConfiguration
{
    /// <summary>App ID this server serves. Required (embedded in steam_appid.txt by default resolution otherwise).</summary>
    public required uint AppId { get; init; }

    /// <summary>IP to bind. Usually 0 (any).</summary>
    public uint BindIp { get; init; }

    /// <summary>Port clients connect to for gameplay.</summary>
    public required ushort GamePort { get; init; }

    /// <summary>
    /// Server browser query port. Pass <see cref="SharedQueryPort"/> to multiplex queries over your own socket
    /// and service them via <see cref="SteamGameServerHost.HandleIncomingPacketAsync"/> / <see cref="SteamGameServerHost.TryGetNextOutgoingPacketAsync"/>.
    /// </summary>
    public required ushort QueryPort { get; init; }

    public GameServerMode Mode { get; init; } = GameServerMode.AuthenticationAndSecure;

    /// <summary>Version string advertised to the masterserver, form x.x.x.x. Only latest-version servers are listed.</summary>
    public string VersionString { get; init; } = "1.0.0.0";

    /// <summary>Pass to <see cref="QueryPort"/> for shared-socket ("GameSocketShare") mode.</summary>
    public const ushort SharedQueryPort = 0xffff;

    // ---- Identity / settings applied before logon ----

    public string Product { get; init; } = "";
    public string GameDescription { get; init; } = "";
    public string ModDir { get; init; } = "";
    public bool DedicatedServer { get; init; } = true;

    /// <summary>Game Server Login Token for a non-anonymous logon. Null/empty → LogOnAnonymous.</summary>
    public string? LoginToken { get; init; }

    public string ServerName { get; init; } = "";
    public string MapName { get; init; } = "";
    public bool PasswordProtected { get; init; }
    public int MaxPlayerCount { get; init; }
    public int BotPlayerCount { get; init; }
    public ushort SpectatorPort { get; init; }
    public string SpectatorServerName { get; init; } = "";
    public string GameTags { get; init; } = "";
    public string GameData { get; init; } = "";
    public string Region { get; init; } = "";

    /// <summary>Server browser rule key/values, set after clearing the existing list.</summary>
    public IReadOnlyDictionary<string, string> KeyValues { get; init; } = new Dictionary<string, string>();

    /// <summary>Start advertised on the master server once running (SetAdvertiseServerActive).</summary>
    public bool AdvertiseActive { get; init; } = true;

    /// <summary>
    /// Optional explicit path to steam_api native library (steam_api.dll / libsteam_api.so).
    /// Null → default probing (see <see cref="Native.SteamNativeApi.Load"/>).
    /// </summary>
    public string? NativeLibraryPath { get; init; }

    /// <summary>Timeout applied to async Steam calls (call results) and logon waits.</summary>
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Pump loop sleep when the callback queue is drained.</summary>
    public TimeSpan PumpIdleDelay { get; init; } = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// When set, exported as STEAMAPPTOKEN before init (legacy app token). Prefer LoginToken (GSLT).
    /// Env vars are process-global and must be set before the native library initializes.
    /// </summary>
    public string? AppToken { get; init; }
}

/// <summary>Result of GetServerReputationAsync (GSReputation_t). Ban fields are zero unless Banned.</summary>
public sealed record ServerReputation(
    SteamResult Result,
    uint ReputationScore,
    bool Banned,
    SteamIpAddress BannedIp,
    ushort BannedPort,
    ulong BannedGameId,
    uint BanExpiresUnix)
{
    /// <summary>When the master-server ban expires (UTC); epoch when not banned.</summary>
    public DateTimeOffset BanExpires => DateTimeOffset.FromUnixTimeSeconds(BanExpiresUnix);
}

/// <summary>Result of AssociateWithClanAsync (AssociateWithClanResult_t).</summary>
public sealed record ClanAssociation(SteamResult Result);

/// <summary>Result of ComputeNewPlayerCompatibilityAsync (ComputeNewPlayerCompatibilityResult_t).</summary>
public sealed record PlayerCompatibility(
    SteamResult Result,
    int PlayersThatDontLikeCandidate,
    int PlayersCandidateDoesntLike,
    int ClanPlayersThatDontLikeCandidate,
    SteamId Candidate);

/// <summary>ValidateAuthTicketResponse_t — authoritative ticket-validation verdict for a BeginAuthSession.</summary>
public sealed record AuthValidation(SteamId Player, AuthSessionResponse Response, SteamId OwnerSteamId)
{
    public bool Ok => Response == AuthSessionResponse.Ok;
}

/// <summary>GSGameplayStats_t.</summary>
public sealed record GameplayStats(SteamResult Result, int Rank, uint TotalConnects, uint TotalMinutesPlayed);

/// <summary>GSStatsReceived_t — arrival of a user's stats from the Steam back end.</summary>
public sealed record StatsReceived(SteamResult Result, SteamId Player);

/// <summary>Snapshot of a user's server-side stats/achievements, taken after RequestUserStatsAsync.</summary>
public sealed record UserStatsSnapshot(SteamId Player, SteamResult Result)
{
    public IReadOnlyDictionary<string, int> IntStats { get; init; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, float> FloatStats { get; init; } = new Dictionary<string, float>();
    public IReadOnlyDictionary<string, bool> Achievements { get; init; } = new Dictionary<string, bool>();
}

/// <summary>Outcome of AuthPlayerAsync: immediate BeginAuthSession verdict and/or async validation.</summary>
public sealed record PlayerAuthResult(SteamId Player, BeginAuthSessionResult Immediate, AuthValidation? Validation)
{
    /// <summary>True when the ticket passed immediate checks AND Steam validated the session.</summary>
    public bool Accepted => Immediate == BeginAuthSessionResult.Ok && Validation?.Ok == true;
}
