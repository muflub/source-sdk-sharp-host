using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SourceSharp.Host.Steam.Native;

/// <summary>Thrown when the native steam_api library can't be loaded or an expected export is missing.</summary>
public sealed class SteamNativeException : Exception
{
    public SteamNativeException(string message) : base(message) { }
    public SteamNativeException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Native binding for the flat Steamworks game-server API.
/// All exports are resolved once from the loaded library; every delegate here must be called
/// from the pump thread (see <see cref="SteamGameServerHost"/>) unless documented thread-safe.
/// </summary>
internal sealed class SteamNativeApi : IDisposable
{
    // ---- raw delegates (signatures mirror public/steam/steam_api_flat.h + steam_api_internal.h) ----

    internal delegate int GameServerInitV2Delegate(uint ip, ushort gamePort, ushort queryPort, int serverMode, IntPtr versionString, IntPtr checkInterfaceVersions, IntPtr errMsgOut);

    internal delegate void ActionDelegate();
    internal delegate IntPtr IntPtrReturnDelegate();
    internal delegate int PipeReturnDelegate();
    internal delegate void RunFrameDelegate(int hSteamPipe);
    [return: MarshalAs(UnmanagedType.U1)] internal delegate bool GetNextCallbackDelegate(int hSteamPipe, out CallbackMsg msg);
    internal delegate void FreeLastCallbackDelegate(int hSteamPipe);
    [return: MarshalAs(UnmanagedType.U1)] internal delegate bool GetApicallResultDelegate(int hSteamPipe, ulong hAsyncCall, IntPtr pCallback, int cubCallback, int iCallbackExpected, IntPtr bFailedOut);

    internal delegate void SelfStringDelegate(IntPtr self, IntPtr s);
    internal delegate void SelfString2Delegate(IntPtr self, IntPtr key, IntPtr value);
    [return: MarshalAs(UnmanagedType.U1)] internal delegate bool SelfBoolReturnDelegate(IntPtr self);
    internal delegate void SelfBoolDelegate(IntPtr self, [MarshalAs(UnmanagedType.U1)] bool value);
    internal delegate void SelfIntDelegate(IntPtr self, int value);
    internal delegate void SelfUShortDelegate(IntPtr self, ushort value);
    internal delegate ulong SelfULongReturnDelegate(IntPtr self);
    internal delegate ulong SelfULongArgDelegate(IntPtr self, ulong steamId);
    [return: MarshalAs(UnmanagedType.U1)] internal delegate bool SelfTwoUlongBoolDelegate(IntPtr self, ulong a, ulong b);
    internal delegate uint SelfTicketDelegate(IntPtr self, IntPtr pTicket, int cbMaxTicket, IntPtr pcbTicket, IntPtr pSnid);
    internal delegate int SelfBeginAuthDelegate(IntPtr self, IntPtr pTicket, int cbTicket, ulong steamId);
    internal delegate void SelfCancelTicketDelegate(IntPtr self, uint hAuthTicket);
    internal delegate void SelfVoidDelegate(IntPtr self);
    internal delegate int SelfLicenseDelegate(IntPtr self, ulong steamId, uint appId);
    internal delegate ulong SelfStatsRequestDelegate(IntPtr self, ulong steamId);
    internal delegate ulong SelfStatsStoreDelegate(IntPtr self, ulong steamId);
    [return: MarshalAs(UnmanagedType.U1)] internal delegate bool SelfGetUserStatIntDelegate(IntPtr self, ulong steamId, IntPtr name, IntPtr pOut);
    [return: MarshalAs(UnmanagedType.U1)] internal delegate bool SelfGetUserStatFloatDelegate(IntPtr self, ulong steamId, IntPtr name, IntPtr pOut);
    [return: MarshalAs(UnmanagedType.U1)] internal delegate bool SelfGetUserAchDelegate(IntPtr self, ulong steamId, IntPtr name, IntPtr pOut);
    [return: MarshalAs(UnmanagedType.U1)] internal delegate bool SelfSetUserStatIntDelegate(IntPtr self, ulong steamId, IntPtr name, int value);
    [return: MarshalAs(UnmanagedType.U1)] internal delegate bool SelfSetUserStatFloatDelegate(IntPtr self, ulong steamId, IntPtr name, float value);
    [return: MarshalAs(UnmanagedType.U1)] internal delegate bool SelfSetUserAchDelegate(IntPtr self, ulong steamId, IntPtr name);
    internal delegate SteamIPAddressNative GetPublicIPDelegate(IntPtr self);
    [return: MarshalAs(UnmanagedType.U1)] internal delegate bool HandlePacketDelegate(IntPtr self, IntPtr pData, int cbData, uint srcIp, ushort srcPort);
    internal delegate int GetNextPacketDelegate(IntPtr self, IntPtr pOut, int cbMax, IntPtr pNetAdr, IntPtr pPort);
    [return: MarshalAs(UnmanagedType.U1)] internal delegate bool UpdateUserDataDelegate(IntPtr self, ulong steamId, IntPtr playerName, uint score);
    internal delegate void SetWarningHookDelegate(IntPtr self, IntPtr hook);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal unsafe struct SteamIPAddressNative
    {
        public fixed byte Addr[16]; // union: ipv4 low 4 bytes / 16-byte ipv6
        public int Type;            // ESteamIPType
    }

    /// <summary>CallbackMsg_t — offsets are identical under pack(4) and pack(8).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct CallbackMsg
    {
        public int HSteamUser;
        public int ICallback;
        public IntPtr PubParam;
        public int CubParam;
    }

    internal const int MaxSteamErrMsg = 1024;

    private readonly IntPtr _handle;

    /// <summary>4 on Linux/macOS (VALVE_CALLBACK_PACK_SMALL), 8 elsewhere (VALVE_CALLBACK_PACK_LARGE).</summary>
    internal static readonly int CallbackPack =
        OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() ? 4 : 8;

    private SteamNativeApi(IntPtr handle)
    {
        _handle = handle;
        BindAll();
    }

    /// <summary>All interface-version strings the C++ SteamGameServer_InitEx checks, in order, NUL-joined.</summary>
    internal const string InterfaceVersionBlob =
        "SteamUtils011\0" +
        "SteamNetworkingUtils004\0" +
        "SteamGameServer015\0" +
        "SteamGameServerStats001\0" +
        "STEAMHTTP_INTERFACE_VERSION003\0" +
        "STEAMINVENTORY_INTERFACE_V003\0" +
        "SteamNetworking006\0" +
        "SteamNetworkingMessages002\0" +
        "SteamNetworkingSockets013\0" +
        "STEAMUGC_INTERFACE_VERSION021\0" +
        "\0";

    // ---- resolved functions ----
    internal GameServerInitV2Delegate GameServerInitV2 = null!;
    internal ActionDelegate GameServerShutdown = null!;
    internal PipeReturnDelegate GetHSteamPipe = null!;
    internal ActionDelegate ManualDispatchInit = null!;
    internal RunFrameDelegate ManualDispatchRunFrame = null!;
    internal GetNextCallbackDelegate ManualDispatchGetNextCallback = null!;
    internal FreeLastCallbackDelegate ManualDispatchFreeLastCallback = null!;
    internal GetApicallResultDelegate ManualDispatchGetApicallResult = null!;
    internal ActionDelegate ReleaseCurrentThreadMemory = null!;

    internal IntPtrReturnDelegate GetSteamGameServer = null!;       // SteamAPI_SteamGameServer_v015
    internal IntPtrReturnDelegate GetSteamGameServerStats = null!;  // SteamAPI_SteamGameServerStats_v001
    internal IntPtrReturnDelegate GetSteamGameServerUtils = null!;  // SteamAPI_SteamGameServerUtils_v011

    internal SelfStringDelegate SetProduct = null!;
    internal SelfStringDelegate SetGameDescription = null!;
    internal SelfStringDelegate SetModDir = null!;
    internal SelfBoolDelegate SetDedicatedServer = null!;
    internal SelfStringDelegate LogOn = null!;
    internal SelfVoidDelegate LogOnAnonymous = null!;
    internal SelfVoidDelegate LogOff = null!;
    internal SelfBoolReturnDelegate BLoggedOn = null!;
    internal SelfBoolReturnDelegate BSecure = null!;
    internal SelfULongReturnDelegate GetSteamId = null!;
    internal SelfBoolReturnDelegate WasRestartRequested = null!;
    internal SelfIntDelegate SetMaxPlayerCount = null!;
    internal SelfIntDelegate SetBotPlayerCount = null!;
    internal SelfStringDelegate SetServerName = null!;
    internal SelfStringDelegate SetMapName = null!;
    internal SelfBoolDelegate SetPasswordProtected = null!;
    internal SelfUShortDelegate SetSpectatorPort = null!;
    internal SelfStringDelegate SetSpectatorServerName = null!;
    internal SelfVoidDelegate ClearAllKeyValues = null!;
    internal SelfString2Delegate SetKeyValue = null!;
    internal SelfStringDelegate SetGameTags = null!;
    internal SelfStringDelegate SetGameData = null!;
    internal SelfStringDelegate SetRegion = null!;
    internal SelfBoolDelegate SetAdvertiseServerActive = null!;
    internal SelfTicketDelegate GetAuthSessionTicket = null!;
    internal SelfBeginAuthDelegate BeginAuthSession = null!;
    internal SelfULongArgDelegate EndAuthSession = null!;
    internal SelfCancelTicketDelegate CancelAuthTicket = null!;
    internal SelfLicenseDelegate UserHasLicenseForApp = null!;
    internal SelfTwoUlongBoolDelegate RequestUserGroupStatus = null!;
    internal SelfVoidDelegate GetGameplayStats = null!;
    internal SelfULongReturnDelegate GetServerReputation = null!;
    internal GetPublicIPDelegate GetPublicIP = null!;
    internal HandlePacketDelegate HandleIncomingPacket = null!;
    internal GetNextPacketDelegate GetNextOutgoingPacket = null!;
    internal SelfULongArgDelegate AssociateWithClan = null!;
    internal SelfULongArgDelegate ComputeNewPlayerCompatibility = null!;
    internal SelfULongReturnDelegate CreateUnauthenticatedUserConnection = null!;
    internal UpdateUserDataDelegate BUpdateUserData = null!;

    internal SelfStatsRequestDelegate StatsRequestUserStats = null!;
    internal SelfStatsStoreDelegate StatsStoreUserStats = null!;
    internal SelfGetUserStatIntDelegate StatsGetUserStatInt32 = null!;
    internal SelfGetUserStatFloatDelegate StatsGetUserStatFloat = null!;
    internal SelfGetUserAchDelegate StatsGetUserAchievement = null!;
    internal SelfSetUserStatIntDelegate StatsSetUserStatInt32 = null!;
    internal SelfSetUserStatFloatDelegate StatsSetUserStatFloat = null!;
    internal SelfSetUserAchDelegate StatsSetUserAchievement = null!;
    internal SelfSetUserAchDelegate StatsClearUserAchievement = null!;

    internal SetWarningHookDelegate SetWarningMessageHook = null!;

    /// <summary>Loads the library, probing (in order): explicit path, STEAM_SDK_NATIVE, OS default names.</summary>
    public static SteamNativeApi Load(string? explicitPath)
    {
        foreach (var candidate in EnumerateCandidates(explicitPath))
        {
            if (NativeLibrary.TryLoad(candidate, out var handle))
            {
                try
                {
                    return new SteamNativeApi(handle);
                }
                catch (SteamNativeException)
                {
                    NativeLibrary.Free(handle);
                    throw; // found the file but exports missing → library mismatch, report precisely
                }
            }
        }
        throw new SteamNativeException(
            $"Unable to load the Steamworks native library. Tried: {string.Join(", ", EnumerateCandidates(explicitPath))}. " +
            "Set Steam:NativeLibraryPath (the gateway's option), the STEAM_SDK_NATIVE env var, or copy steam_api.dll/libsteam_api.so next to the application. " +
            $"On Linux, steamclient.so must also be discoverable (e.g. in ~/.steam/sdk64 or the app directory).");
    }

    private static IEnumerable<string> EnumerateCandidates(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            yield return explicitPath;

        var fromEnv = Environment.GetEnvironmentVariable("STEAM_SDK_NATIVE");
        if (!string.IsNullOrWhiteSpace(fromEnv))
            yield return fromEnv;

        // App directory first — "copy the redist next to the application" must work on every
        // OS, and bare-soname probing does not search the app directory on Linux.
        string baseDir = AppContext.BaseDirectory;
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(baseDir, "steam_api64.dll");
            yield return Path.Combine(baseDir, "steam_api.dll");
            yield return "steam_api64";
            yield return "steam_api";
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return Path.Combine(baseDir, "libsteam_api.dylib");
            yield return "libsteam_api";
        }
        else
        {
            yield return Path.Combine(baseDir, "libsteam_api.so");
            yield return "libsteam_api.so";
        }
    }

    private SteamNativeApi BindAll()
    {
        GameServerInitV2 = Get<GameServerInitV2Delegate>(nameof(GameServerInitV2), "SteamInternal_GameServer_Init_V2");
        GameServerShutdown = Get<ActionDelegate>(nameof(GameServerShutdown), "SteamGameServer_Shutdown");
        GetHSteamPipe = Get<PipeReturnDelegate>(nameof(GetHSteamPipe), "SteamGameServer_GetHSteamPipe");
        ManualDispatchInit = Get<ActionDelegate>(nameof(ManualDispatchInit), "SteamAPI_ManualDispatch_Init");
        ManualDispatchRunFrame = Get<RunFrameDelegate>(nameof(ManualDispatchRunFrame), "SteamAPI_ManualDispatch_RunFrame");
        ManualDispatchGetNextCallback = Get<GetNextCallbackDelegate>(nameof(ManualDispatchGetNextCallback), "SteamAPI_ManualDispatch_GetNextCallback");
        ManualDispatchFreeLastCallback = Get<FreeLastCallbackDelegate>(nameof(ManualDispatchFreeLastCallback), "SteamAPI_ManualDispatch_FreeLastCallback");
        ManualDispatchGetApicallResult = Get<GetApicallResultDelegate>(nameof(ManualDispatchGetApicallResult), "SteamAPI_ManualDispatch_GetAPICallResult");
        ReleaseCurrentThreadMemory = Get<ActionDelegate>(nameof(ReleaseCurrentThreadMemory), "SteamAPI_ReleaseCurrentThreadMemory");

        GetSteamGameServer = Get<IntPtrReturnDelegate>(nameof(GetSteamGameServer), "SteamAPI_SteamGameServer_v015");
        GetSteamGameServerStats = Get<IntPtrReturnDelegate>(nameof(GetSteamGameServerStats), "SteamAPI_SteamGameServerStats_v001");
        GetSteamGameServerUtils = Get<IntPtrReturnDelegate>(nameof(GetSteamGameServerUtils), "SteamAPI_SteamGameServerUtils_v011");

        const string P = "SteamAPI_ISteamGameServer_";
        SetProduct = Get<SelfStringDelegate>(nameof(SetProduct), P + "SetProduct");
        SetGameDescription = Get<SelfStringDelegate>(nameof(SetGameDescription), P + "SetGameDescription");
        SetModDir = Get<SelfStringDelegate>(nameof(SetModDir), P + "SetModDir");
        SetDedicatedServer = Get<SelfBoolDelegate>(nameof(SetDedicatedServer), P + "SetDedicatedServer");
        LogOn = Get<SelfStringDelegate>(nameof(LogOn), P + "LogOn");
        LogOnAnonymous = Get<SelfVoidDelegate>(nameof(LogOnAnonymous), P + "LogOnAnonymous");
        LogOff = Get<SelfVoidDelegate>(nameof(LogOff), P + "LogOff");
        BLoggedOn = Get<SelfBoolReturnDelegate>(nameof(BLoggedOn), P + "BLoggedOn");
        BSecure = Get<SelfBoolReturnDelegate>(nameof(BSecure), P + "BSecure");
        GetSteamId = Get<SelfULongReturnDelegate>(nameof(GetSteamId), P + "GetSteamID");
        WasRestartRequested = Get<SelfBoolReturnDelegate>(nameof(WasRestartRequested), P + "WasRestartRequested");
        SetMaxPlayerCount = Get<SelfIntDelegate>(nameof(SetMaxPlayerCount), P + "SetMaxPlayerCount");
        SetBotPlayerCount = Get<SelfIntDelegate>(nameof(SetBotPlayerCount), P + "SetBotPlayerCount");
        SetServerName = Get<SelfStringDelegate>(nameof(SetServerName), P + "SetServerName");
        SetMapName = Get<SelfStringDelegate>(nameof(SetMapName), P + "SetMapName");
        SetPasswordProtected = Get<SelfBoolDelegate>(nameof(SetPasswordProtected), P + "SetPasswordProtected");
        SetSpectatorPort = Get<SelfUShortDelegate>(nameof(SetSpectatorPort), P + "SetSpectatorPort");
        SetSpectatorServerName = Get<SelfStringDelegate>(nameof(SetSpectatorServerName), P + "SetSpectatorServerName");
        ClearAllKeyValues = Get<SelfVoidDelegate>(nameof(ClearAllKeyValues), P + "ClearAllKeyValues");
        SetKeyValue = Get<SelfString2Delegate>(nameof(SetKeyValue), P + "SetKeyValue");
        SetGameTags = Get<SelfStringDelegate>(nameof(SetGameTags), P + "SetGameTags");
        SetGameData = Get<SelfStringDelegate>(nameof(SetGameData), P + "SetGameData");
        SetRegion = Get<SelfStringDelegate>(nameof(SetRegion), P + "SetRegion");
        SetAdvertiseServerActive = Get<SelfBoolDelegate>(nameof(SetAdvertiseServerActive), P + "SetAdvertiseServerActive");
        GetAuthSessionTicket = Get<SelfTicketDelegate>(nameof(GetAuthSessionTicket), P + "GetAuthSessionTicket");
        BeginAuthSession = Get<SelfBeginAuthDelegate>(nameof(BeginAuthSession), P + "BeginAuthSession");
        EndAuthSession = Get<SelfULongArgDelegate>(nameof(EndAuthSession), P + "EndAuthSession");
        CancelAuthTicket = Get<SelfCancelTicketDelegate>(nameof(CancelAuthTicket), P + "CancelAuthTicket");
        UserHasLicenseForApp = Get<SelfLicenseDelegate>(nameof(UserHasLicenseForApp), P + "UserHasLicenseForApp");
        RequestUserGroupStatus = Get<SelfTwoUlongBoolDelegate>(nameof(RequestUserGroupStatus), P + "RequestUserGroupStatus");
        GetGameplayStats = Get<SelfVoidDelegate>(nameof(GetGameplayStats), P + "GetGameplayStats");
        GetServerReputation = Get<SelfULongReturnDelegate>(nameof(GetServerReputation), P + "GetServerReputation");
        GetPublicIP = Get<GetPublicIPDelegate>(nameof(GetPublicIP), P + "GetPublicIP");
        HandleIncomingPacket = Get<HandlePacketDelegate>(nameof(HandleIncomingPacket), P + "HandleIncomingPacket");
        GetNextOutgoingPacket = Get<GetNextPacketDelegate>(nameof(GetNextOutgoingPacket), P + "GetNextOutgoingPacket");
        AssociateWithClan = Get<SelfULongArgDelegate>(nameof(AssociateWithClan), P + "AssociateWithClan");
        ComputeNewPlayerCompatibility = Get<SelfULongArgDelegate>(nameof(ComputeNewPlayerCompatibility), P + "ComputeNewPlayerCompatibility");
        CreateUnauthenticatedUserConnection = Get<SelfULongReturnDelegate>(nameof(CreateUnauthenticatedUserConnection), P + "CreateUnauthenticatedUserConnection");
        BUpdateUserData = Get<UpdateUserDataDelegate>(nameof(BUpdateUserData), P + "BUpdateUserData");

        const string S = "SteamAPI_ISteamGameServerStats_";
        StatsRequestUserStats = Get<SelfStatsRequestDelegate>(nameof(StatsRequestUserStats), S + "RequestUserStats");
        StatsStoreUserStats = Get<SelfStatsStoreDelegate>(nameof(StatsStoreUserStats), S + "StoreUserStats");
        StatsGetUserStatInt32 = Get<SelfGetUserStatIntDelegate>(nameof(StatsGetUserStatInt32), S + "GetUserStatInt32");
        StatsGetUserStatFloat = Get<SelfGetUserStatFloatDelegate>(nameof(StatsGetUserStatFloat), S + "GetUserStatFloat");
        StatsGetUserAchievement = Get<SelfGetUserAchDelegate>(nameof(StatsGetUserAchievement), S + "GetUserAchievement");
        StatsSetUserStatInt32 = Get<SelfSetUserStatIntDelegate>(nameof(StatsSetUserStatInt32), S + "SetUserStatInt32");
        StatsSetUserStatFloat = Get<SelfSetUserStatFloatDelegate>(nameof(StatsSetUserStatFloat), S + "SetUserStatFloat");
        StatsSetUserAchievement = Get<SelfSetUserAchDelegate>(nameof(StatsSetUserAchievement), S + "SetUserAchievement");
        StatsClearUserAchievement = Get<SelfSetUserAchDelegate>(nameof(StatsClearUserAchievement), S + "ClearUserAchievement");

        SetWarningMessageHook = Get<SetWarningHookDelegate>(nameof(SetWarningMessageHook), "SteamAPI_ISteamUtils_SetWarningMessageHook");


        return this;
    }

    private T Get<T>(string member, string export) where T : Delegate
    {
        if (!NativeLibrary.TryGetExport(_handle, export, out var ptr))
            throw new SteamNativeException(
                $"The loaded Steamworks native library does not export '{export}', required by SourceSharp.Host.Steam " +
                $"({member}). The native library is likely older than this binding — ship the redist " +
                $"matching the SDK headers this package was built against.");
        return Marshal.GetDelegateForFunctionPointer<T>(ptr);
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
            NativeLibrary.Free(_handle);
    }
}
