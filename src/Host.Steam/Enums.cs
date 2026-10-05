namespace SourceSharp.Host.Steam;

/// <summary>Mirrors EServerMode from steam_gameserver.h.</summary>
public enum GameServerMode
{
    /// <summary>Don't authenticate user logins and don't list on the server list.</summary>
    NoAuthentication = 1,

    /// <summary>Authenticate users, list on the server list, don't run VAC on clients that connect.</summary>
    Authentication = 2,

    /// <summary>Authenticate users, list on the server list and VAC protect clients.</summary>
    AuthenticationAndSecure = 3,
}

/// <summary>Mirrors EResult from steamclientpublic.h (subset used by game server APIs; raw values).</summary>
public enum SteamResult
{
    None = 0,
    OK = 1,
    Fail = 2,
    NoConnection = 3,
    InvalidPassword = 5,
    LoggedInElsewhere = 6,
    InvalidProtocolVer = 7,
    InvalidParam = 8,
    Busy = 10,
    InvalidState = 11,
    Timeout = 16,
    Banned = 17,
    AccountNotFound = 18,
    InvalidSteamID = 19,
    ServiceUnavailable = 20,
    NotLoggedOn = 21,
    Pending = 22,
    LimitExceeded = 25,
    RateLimitExceeded = 84,
    GSLTDenied = 102,
    GSOwnerDenied = 103,
    GSLTExpired = 106,
}

/// <summary>Mirrors EDenyReason from steamclientpublic.h.</summary>
public enum DenyReason
{
    Invalid = 0,
    InvalidVersion = 1,
    Generic = 2,
    NotLoggedOn = 3,
    NoLicense = 4,
    Cheater = 5,
    LoggedInElseWhere = 6,
    UnknownText = 7,
    IncompatibleAnticheat = 8,
    MemoryCorruption = 9,
    IncompatibleSoftware = 10,
    SteamConnectionLost = 11,
    SteamConnectionError = 12,
    SteamResponseTimedOut = 13,
    SteamValidationStalled = 14,
    SteamOwnerLeftGuestUser = 15,
}

/// <summary>Mirrors EBeginAuthSessionResult — immediate result of BeginAuthSession.</summary>
public enum BeginAuthSessionResult
{
    /// <summary>Ticket is valid for this game and this steamID.</summary>
    Ok = 0,

    /// <summary>Ticket is not valid.</summary>
    InvalidTicket = 1,

    /// <summary>A ticket has already been submitted for this steamID.</summary>
    DuplicateRequest = 2,

    /// <summary>Ticket is from an incompatible interface version.</summary>
    InvalidVersion = 3,

    /// <summary>Ticket is not for this game.</summary>
    GameMismatch = 4,

    /// <summary>Ticket has expired.</summary>
    ExpiredTicket = 5,
}

/// <summary>Mirrors EAuthSessionResponse — delivered asynchronously via ValidateAuthTicketResponse_t.</summary>
public enum AuthSessionResponse
{
    /// <summary>Steam verified the user is online, the ticket is valid and has not been reused.</summary>
    Ok = 0,
    UserNotConnectedToSteam = 1,
    NoLicenseOrExpired = 2,

    /// <summary>The user is VAC banned for this game.</summary>
    VacBanned = 3,
    LoggedInElseWhere = 4,
    VacCheckTimedOut = 5,
    AuthTicketCanceled = 6,
    AuthTicketInvalidAlreadyUsed = 7,
    AuthTicketInvalid = 8,

    /// <summary>The user is banned for this game via the web API (not VAC).</summary>
    PublisherIssuedBan = 9,
    AuthTicketNetworkIdentityFailure = 10,
}

/// <summary>Mirrors EUserHasLicenseForAppResult.</summary>
public enum UserHasLicenseForAppResult
{
    HasLicense = 0,
    DoesNotHaveLicense = 1,

    /// <summary>User has not been authenticated on this server.</summary>
    NoAuth = 2,
}

/// <summary>Mirrors ESteamIPType.</summary>
public enum SteamIpType
{
    IPv4 = 0,
    IPv6 = 1,
}

/// <summary>Lifecycle of a <see cref="SteamGameServerHost"/>.</summary>
public enum GameServerState
{
    Created,
    Starting,
    WaitingForLogon,
    Running,
    LoggingOff,
    Stopped,
    Faulted,
}
