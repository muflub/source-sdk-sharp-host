using System.Net;

namespace SourceSharp.Host.Gateway.Steam;

/// <summary>How a <see cref="ISteamAuthServer.BeginAsync"/> ended (plan_steam_auth S-3, S-8, S-11).</summary>
public enum AuthOutcome
{
    /// <summary>Steam validated the ticket for that SteamID (ValidateAuthTicketResponse OK or GSClientApprove).</summary>
    Ok,
    /// <summary>Steam refused the ticket: invalid, cancelled, banned, no license, wrong game, …</summary>
    Denied,
    /// <summary>Steam says the ticket was already used: a replay after a restart (S-11).</summary>
    AlreadyClaimed,
    /// <summary>No verdict within the auth timeout.</summary>
    Timeout,
    /// <summary>The game server is not logged on, so nothing was asked (S-8).</summary>
    Unavailable,
}

/// <summary>A verdict on one session's ticket. <see cref="Reason"/> is for logs and the client's reject text.</summary>
public sealed record AuthVerdict(AuthOutcome Outcome, string Reason, ulong OwnerSteamId = 0)
{
    public bool Ok => Outcome == AuthOutcome.Ok;
    public static AuthVerdict Valid(ulong owner) => new(AuthOutcome.Ok, "ok", owner);
}

/// <summary>The Steam game server's state as the relay needs it. <see cref="Epoch"/> is new on every logon.</summary>
public sealed record SteamAuthStatus(bool LoggedOn, ulong GameServerSteamId, string Epoch)
{
    public static readonly SteamAuthStatus Off = new(false, 0, "");
}

/// <summary>An outgoing datagram the Steam library wants sent from the public socket (S-19).</summary>
public sealed record SteamPacket(byte[] Data, IPEndPoint To);

/// <summary>
/// The Steam game server, as the gateway's relay uses it (plan_steam_auth S-16). Auth sessions are keyed by
/// the relay's <b>session id</b>, never by SteamID alone, so a late <see cref="End"/> for an old session can
/// never end a rejoin's auth (S-13). At most one session per SteamID holds an auth at a time: the relay's
/// presence rule (S-17) guarantees it, and an implementation ends the older one first if it is ever asked.
/// </summary>
public interface ISteamAuthServer : IAsyncDisposable
{
    SteamAuthStatus Status { get; }

    /// <summary>Logs on. Returns once the logon is under way; <see cref="Status"/> says when it completed.</summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>Begins an auth session for <paramref name="sessionId"/> and completes with Steam's verdict.</summary>
    Task<AuthVerdict> BeginAsync(string sessionId, ulong steamId, ReadOnlyMemory<byte> ticket, CancellationToken ct);

    /// <summary>Ends the session's auth (EndAuthSession) at once; a no-op for a session that holds none.</summary>
    void End(string sessionId);

    /// <summary>A session's validated auth was withdrawn by Steam after the fact (S-12): cancelled, kicked, banned.</summary>
    event Action<string, string>? Revoked;

    /// <summary>A datagram from the public socket that is meant for the Steam library (S-19).</summary>
    void HandleIncoming(ReadOnlySpan<byte> packet, IPEndPoint from);

    /// <summary>Everything the Steam library wants sent, in order; empty when it has nothing.</summary>
    Task<IReadOnlyList<SteamPacket>> DrainOutgoingAsync();

    /// <summary>The server-browser entry (plan_steam_auth S-18) for the Steam listing; advertised when <paramref name="advertise"/>.</summary>
    void ApplyListing(SteamListing listing, bool advertise);
}

/// <summary>What the Steam game server lists (S-18). Its player count is its own: the auth sessions it holds.</summary>
public sealed record SteamListing(string Name, string Map, string Product, int MaxPlayers, string Tags, string Region,
                                  IReadOnlyDictionary<string, string> Rules);
