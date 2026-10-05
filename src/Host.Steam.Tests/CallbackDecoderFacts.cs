using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SourceSharp.Host.Steam;
using SourceSharp.Host.Steam.Native;

namespace SourceSharp.Host.Steam.Tests;

/// <summary>
/// The callback structs the gateway acts on, decoded from hand-built native buffers laid out as
/// the Steamworks headers declare them on Linux: <c>VALVE_CALLBACK_PACK_SMALL</c>, i.e. pack(4)
/// (steamclientpublic.h), so an 8-byte member after a 4-byte one sits at offset 12, not 16.
/// </summary>
public class CallbackDecoderFacts
{
    const ulong Player = 0x0110000100ABCDEFUL;
    const ulong Owner = 0x0110000100123456UL;

    /// <summary>Copies <paramref name="bytes"/> into native memory for the decoder, which reads by pointer.</summary>
    static T Decode<T>(byte[] bytes, Func<IntPtr, T> read)
    {
        var p = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, p, bytes.Length);
            return read(p);
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    [Fact]
    public void Callback_structs_use_pack_4_on_linux() =>
        Assert.Equal(4, SteamNativeApi.CallbackPack);

    [Fact]
    public void ValidateAuthTicketResponse_reads_player_response_and_owner_at_0_8_12()
    {
        // ValidateAuthTicketResponse_t (isteamuser.h): CSteamID m_SteamID; EAuthSessionResponse; CSteamID m_OwnerSteamID.
        var b = new byte[20];
        BinaryPrimitives.WriteUInt64LittleEndian(b, Player);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(8), (int)AuthSessionResponse.AuthTicketCanceled);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(12), Owner);

        var v = Decode(b, CallbackDecoder.ReadValidateAuthTicket);

        Assert.Equal(new SteamId(Player), v.Player);
        Assert.Equal(AuthSessionResponse.AuthTicketCanceled, v.Response);
        Assert.Equal(new SteamId(Owner), v.OwnerSteamId);
    }

    [Fact]
    public void ValidateAuthTicketResponse_owner_is_not_read_at_the_pack_8_offset()
    {
        // The negative arm: an owner placed at 16 (pack 8) must not be what the decoder returns.
        var b = new byte[24];
        BinaryPrimitives.WriteUInt64LittleEndian(b, Player);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(16), Owner);

        Assert.NotEqual(new SteamId(Owner), Decode(b, CallbackDecoder.ReadValidateAuthTicket).OwnerSteamId);
    }

    [Fact]
    public void ClientApprove_reads_player_then_owner()
    {
        var b = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(b, Player);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(8), Owner);

        var (player, owner) = Decode(b, CallbackDecoder.ReadClientApprove);

        Assert.Equal((new SteamId(Player), new SteamId(Owner)), (player, owner));
    }

    [Fact]
    public void ClientDeny_reads_reason_at_8_and_text_at_12()
    {
        var b = new byte[12 + 128];
        BinaryPrimitives.WriteUInt64LittleEndian(b, Player);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(8), (int)DenyReason.SteamValidationStalled);
        "stalled"u8.CopyTo(b.AsSpan(12));

        var (player, reason, text) = Decode(b, CallbackDecoder.ReadClientDeny);

        Assert.Equal(new SteamId(Player), player);
        Assert.Equal(DenyReason.SteamValidationStalled, reason);
        Assert.Equal("stalled", text);
    }

    [Fact]
    public void ClientDeny_text_without_a_terminator_stops_at_128_bytes()
    {
        var b = new byte[12 + 128 + 8];
        b.AsSpan(12, 128 + 8).Fill((byte)'x');

        Assert.Equal(128, Decode(b, CallbackDecoder.ReadClientDeny).OptionalText.Length);
    }

    [Fact]
    public void ClientKick_reads_player_and_reason()
    {
        var b = new byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(b, Player);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(8), (int)DenyReason.Cheater);

        Assert.Equal((new SteamId(Player), DenyReason.Cheater), Decode(b, CallbackDecoder.ReadClientKick));
    }

    [Fact]
    public void ApicallCompleted_reads_handle_callback_and_size()
    {
        var b = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(b, 0xDEADBEEF00000001UL);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(8), 209);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(12), 40);

        Assert.Equal((0xDEADBEEF00000001UL, 209, 40), Decode(b, CallbackDecoder.ReadApicallCompleted));
    }
}
