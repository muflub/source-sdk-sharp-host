namespace SourceSharp.Host.Steam;

/// <summary>
/// Callback sink for game-server notifications. Split by area; implement the individual
/// area interfaces you care about, or <see cref="ISteamGameServerEvents"/> (which inherits
/// all of them) to pass a single sink to the host. All handlers run on the host's pump
/// thread: never block in them, and never call blocking host APIs from them (enqueue work
/// instead). Exceptions thrown by a handler are caught and reported to
/// <see cref="ISteamGameServerDiagnosticsEvents.OnHostError"/>; the pump survives.
/// </summary>
public interface ISteamGameServerEvents
    : ISteamGameServerLifecycleEvents,
      ISteamGameServerClientEvents,
      ISteamGameServerStatsEvents,
      ISteamGameServerAuthEvents,
      ISteamGameServerDiagnosticsEvents
{
}

/// <summary>Server session lifecycle: state machine, logon result, policy, restart hint.</summary>
public interface ISteamGameServerLifecycleEvents
{
    /// <summary>Fires on every <see cref="GameServerState"/> transition. <paramref name="detail"/> carries init error text when faulted.</summary>
    void OnStateChanged(GameServerState state, string detail) { }

    /// <summary>Steam acknowledged the server's identity (server appears in the browser once advertising is on).</summary>
    void OnLoggedOn(SteamId serverSteamId, bool secure) { }

    void OnLoggedOff() { }

    /// <summary>GSPolicyResponse_t — Steam told us whether this server is VAC-secure.</summary>
    void OnPolicyResponse(bool secure) { }

    /// <summary>The server asked for a restart (WasRestartRequested).</summary>
    void OnRestartRequested() { }
}

/// <summary>Per-client connection decisions and client-group replies (GSClient* callbacks).</summary>
public interface ISteamGameServerClientEvents
{
    /// <summary>GSClientApprove_t — Steam approved <paramref name="player"/> (owner differs when the license is borrowed).</summary>
    void OnClientApproved(SteamId player, SteamId owner) { }

    /// <summary>GSClientDeny_t — Steam refused a connection attempt.</summary>
    void OnClientDenied(SteamId player, DenyReason reason, string optionalText) { }

    /// <summary>GSClientKick_t — Steam asks the server to kick a player.</summary>
    void OnClientKickRequested(SteamId player, DenyReason reason) { }

    /// <summary>GSClientGroupStatus_t — reply to RequestUserGroupStatus.</summary>
    void OnGroupStatus(SteamId user, SteamId group, bool isMember, bool isOfficer) { }
}

/// <summary>Server and per-user stats/achievement notifications.</summary>
public interface ISteamGameServerStatsEvents
{
    /// <summary>GSClientAchievementStatus_t — a client's achievement state changed.</summary>
    void OnAchievementStatus(SteamId player, string achievement, bool unlocked) { }

    /// <summary>GSGameplayStats_t — reply to GetGameplayStats.</summary>
    void OnGameplayStats(GameplayStats stats) { }

    /// <summary>GSStatsUnloaded_t — cached stats for a user were evicted; request them again before use.</summary>
    void OnStatsUnloaded(SteamId player) { }
}

/// <summary>Auth-session validation verdicts (ValidateAuthTicketResponse_t).</summary>
public interface ISteamGameServerAuthEvents
{
    /// <summary>ValidateAuthTicketResponse_t — ticket-validation verdict for an in-flight auth.</summary>
    void OnAuthValidation(AuthValidation validation) { }
}

/// <summary>Diagnostics: Steam-side warnings and host-internal failures.</summary>
public interface ISteamGameServerDiagnosticsEvents
{
    /// <summary>Steam posted a warning through the debug hook (null when the hook is not installed).</summary>
    void OnSteamWarning(int msgType, string message) { }

    /// <summary>Anything the host could not handle: handler exceptions, native call failures, call-result timeouts.</summary>
    void OnHostError(Exception error) { }
}
