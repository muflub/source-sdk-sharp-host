namespace SourceSharp.Host.Gateway.Relay;

/// <summary>The outcome of a route-table control call; the gRPC service maps it to a status code.</summary>
public enum ControlStatus
{
    Ok,
    /// <summary>The call's version is not newer than the table's: FAILED_PRECONDITION, the service's cue to SyncTable.</summary>
    Stale,
    /// <summary>A level route for a session with no SteamID: PERMISSION_DENIED.</summary>
    Unidentified,
    /// <summary>The SteamID already belongs to another live session (single presence): ALREADY_EXISTS.</summary>
    Conflict,
    /// <summary>A malformed address: INVALID_ARGUMENT.</summary>
    Invalid,
}
