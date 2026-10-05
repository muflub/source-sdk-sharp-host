using System.Runtime.CompilerServices;
using System.Text;

namespace SourceSharp.Host.Steam.Native;

/// <summary>
/// Reads game-server callback structs out of native memory.
/// Callback structs use VALVE_CALLBACK_PACK_SMALL (pack 4) on Linux/macOS and
/// VALVE_CALLBACK_PACK_LARGE (pack 8) on Windows; field offsets are computed per-OS so
/// 8-byte members land where the native compiler put them.
/// All pointers are only valid until the next FreeLastCallback/GetNextCallback — every
/// decoder copies what it needs into managed types.
/// </summary>
internal static unsafe class CallbackDecoder
{
    /// <summary>pack(4) caps natural alignment at 4, pack(8) at 8 (all members here are 8 bytes or smaller).</summary>
    private static int AlignUp(int offset, int alignment)
    {
        int pack = SteamNativeApi.CallbackPack;
        if (alignment > pack)
            alignment = pack;
        return (offset + alignment - 1) / alignment * alignment;
    }

    private static ulong ReadU64(IntPtr p, int off) => Unsafe.ReadUnaligned<ulong>((byte*)p + off);
    private static uint ReadU32(IntPtr p, int off) => Unsafe.ReadUnaligned<uint>((byte*)p + off);
    private static int ReadI32(IntPtr p, int off) => Unsafe.ReadUnaligned<int>((byte*)p + off);
    private static ushort ReadU16(IntPtr p, int off) => Unsafe.ReadUnaligned<ushort>((byte*)p + off);
    private static byte ReadU8(IntPtr p, int off) => Unsafe.ReadUnaligned<byte>((byte*)p + off);

    private static string ReadFixedAscii(IntPtr p, int off, int max)
    {
        int len = 0;
        while (len < max && ((byte*)p + off)[len] != 0)
            len++;
        return len == 0 ? string.Empty : Encoding.UTF8.GetString((byte*)p + off, len);
    }

    // ---- callbacks (ids: isteamgameserver.h / isteamgameserverstats.h / isteamuser.h) ----

    /// <summary>GSClientApprove_t (201): { CSteamID m_SteamID; CSteamID m_OwnerSteamID; }</summary>
    public static (SteamId Player, SteamId Owner) ReadClientApprove(IntPtr p)
        => (new SteamId(ReadU64(p, 0)), new SteamId(ReadU64(p, 8)));

    /// <summary>GSClientDeny_t (202): { CSteamID(8); EDenyReason(4); char m_rgchOptionalText[128] @12 }</summary>
    public static (SteamId Player, DenyReason Reason, string OptionalText) ReadClientDeny(IntPtr p)
        => (new SteamId(ReadU64(p, 0)), (DenyReason)ReadI32(p, 8), ReadFixedAscii(p, 12, 128));

    /// <summary>GSClientKick_t (203): { CSteamID(8); EDenyReason(4) }</summary>
    public static (SteamId Player, DenyReason Reason) ReadClientKick(IntPtr p)
        => (new SteamId(ReadU64(p, 0)), (DenyReason)ReadI32(p, 8));

    /// <summary>GSClientAchievementStatus_t (206): { u64(8); char[128] @8; bool @136 }</summary>
    public static (SteamId Player, string Achievement, bool Unlocked) ReadAchievementStatus(IntPtr p)
        => (new SteamId(ReadU64(p, 0)), ReadFixedAscii(p, 8, 128), ReadU8(p, 8 + 128) != 0);

    /// <summary>GSPolicyResponse_t (115): { uint8 m_bSecure }</summary>
    public static bool ReadPolicyResponse(IntPtr p) => ReadU8(p, 0) != 0;

    /// <summary>GSGameplayStats_t (207): { EResult(4), int rank(4), u32 connects(4), u32 minutes(4) }</summary>
    public static GameplayStats ReadGameplayStats(IntPtr p)
        => new((SteamResult)ReadI32(p, 0), ReadI32(p, 4), ReadU32(p, 8), ReadU32(p, 12));

    /// <summary>GSClientGroupStatus_t (208): { user(8), group(8), bool member @16, bool officer @17 }</summary>
    public static (SteamId User, SteamId Group, bool IsMember, bool IsOfficer) ReadClientGroupStatus(IntPtr p)
        => (new SteamId(ReadU64(p, 0)), new SteamId(ReadU64(p, 8)), ReadU8(p, 16) != 0, ReadU8(p, 17) != 0);

    /// <summary>
    /// GSReputation_t (call result 209):
    /// { EResult(4), u32 score(4), bool banned(1), [pad]→u32 bannedIP, u16 bannedPort, [pad]→u64 bannedGameID, u32 banExpires }
    /// The u64 forces a pack-dependent offset.
    /// </summary>
    public static ServerReputation ReadReputation(IntPtr p)
    {
        int offBanned = 8;
        int offBannedIp = AlignUp(offBanned + 1, 4);
        int offBannedPort = offBannedIp + 4;
        int offBannedGameId = AlignUp(offBannedPort + 2, 8);
        int offBanExpires = offBannedGameId + 8;
        return new ServerReputation(
            (SteamResult)ReadI32(p, 0),
            ReadU32(p, 4),
            ReadU8(p, offBanned) != 0,
            new SteamIpAddress(SteamIpType.IPv4, ReadU32(p, offBannedIp), null),
            ReadU16(p, offBannedPort),
            ReadU64(p, offBannedGameId),
            ReadU32(p, offBanExpires));
    }

    /// <summary>AssociateWithClanResult_t (210): { EResult m_eResult; }</summary>
    public static SteamResult ReadAssociateWithClan(IntPtr p) => (SteamResult)ReadI32(p, 0);

    /// <summary>
    /// ComputeNewPlayerCompatibilityResult_t (211):
    /// { EResult(4), int dontLike(4), int doesntLike(4), int clanDontLike(4), [pad]→u64 candidate }
    /// </summary>
    public static PlayerCompatibility ReadPlayerCompatibility(IntPtr p)
    {
        int offSteamId = AlignUp(16, 8);
        return new PlayerCompatibility(
            (SteamResult)ReadI32(p, 0), ReadI32(p, 4), ReadI32(p, 8), ReadI32(p, 12),
            new SteamId(ReadU64(p, offSteamId)));
    }

    /// <summary>ValidateAuthTicketResponse_t (143): { SteamID(8), EAuthSessionResponse(4), [pad]→OwnerSteamID }</summary>
    public static AuthValidation ReadValidateAuthTicket(IntPtr p)
    {
        int offOwner = AlignUp(8 + 4, 8);
        return new AuthValidation(
            new SteamId(ReadU64(p, 0)),
            (AuthSessionResponse)ReadI32(p, 8),
            new SteamId(ReadU64(p, offOwner)));
    }

    /// <summary>GSStatsReceived_t (1800): { EResult(4), [pad]→SteamID(8) }</summary>
    public static StatsReceived ReadStatsReceived(IntPtr p)
        => new((SteamResult)ReadI32(p, 0), new SteamId(ReadU64(p, AlignUp(4, 8))));

    /// <summary>GSStatsStored_t (1801): { EResult(4), [pad]→SteamID(8) }</summary>
    public static (SteamResult Result, SteamId Player) ReadStatsStored(IntPtr p)
        => ((SteamResult)ReadI32(p, 0), new SteamId(ReadU64(p, AlignUp(4, 8))));

    /// <summary>GSStatsUnloaded_t (1108): { CSteamID m_steamIDUser }</summary>
    public static SteamId ReadStatsUnloaded(IntPtr p) => new(ReadU64(p, 0));

    /// <summary>SteamAPICallCompleted_t (703): { SteamAPICall_t(8), ECallbackType(4), uint32 cub(4) } — pack-independent.</summary>
    public static (ulong Call, int CallbackId, int Size) ReadApicallCompleted(IntPtr p)
        => (ReadU64(p, 0), ReadI32(p, 8), ReadI32(p, 12));
}
