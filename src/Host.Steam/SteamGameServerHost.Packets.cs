using System.Runtime.CompilerServices;

namespace SourceSharp.Host.Steam;

/// <summary>
/// Shared-query-socket area (QueryPort = SharedQueryPort mode): the game server relays the
/// Steam masterserver/anti-cheat traffic over the game channel. Feed inbound 0xFFFFFFFF-prefixed
/// UDP packets in; pull the masterserver's outbound packets out.
/// </summary>
public sealed partial class SteamGameServerHost
{
    /// <summary>
    /// Feed a 0xFFFFFFFF-prefixed UDP packet that arrived on your query port (QueryPort = SharedQueryPort mode).
    /// <paramref name="srcIp"/>/<paramref name="srcPort"/> are host byte order.
    /// Any reply Steam generates afterwards surfaces through <see cref="EnumerateOutgoingPacketsAsync"/>.
    /// </summary>
    public Task<bool> HandleIncomingPacketAsync(ReadOnlyMemory<byte> packet, uint srcIp, ushort srcPort) =>
        Enqueue("HandleIncomingPacket", () =>
        {
            var api = RequireApi();
            unsafe
            {
                fixed (byte* pData = packet.Span)
                    return api.HandleIncomingPacket(_gs, (IntPtr)pData, packet.Length, srcIp, srcPort);
            }
        });

    /// <summary>
    /// Pulls the next packet the masterserver updater wants sent (UDP to the returned ip/port, host byte order).
    /// null → queue drained. Single-shot primitive; prefer <see cref="EnumerateOutgoingPacketsAsync"/>.
    /// </summary>
    public Task<(byte[] Packet, uint Ip, ushort Port)?> TryGetNextOutgoingPacketAsync(int maxLen = 1400) =>
        Enqueue<(byte[] Packet, uint Ip, ushort Port)?>("GetNextOutgoingPacket", () =>
        {
            var api = RequireApi();
            var outBuf = new byte[maxLen];
            uint ip = 0;
            ushort port = 0;
            int len;
            unsafe
            {
                fixed (byte* pBuf = outBuf)
                {
                    uint* pIp = stackalloc uint[1] { 0 };
                    ushort* pPort = stackalloc ushort[1] { 0 };
                    len = api.GetNextOutgoingPacket(_gs, (IntPtr)pBuf, maxLen, (IntPtr)pIp, (IntPtr)pPort);
                    ip = *pIp;
                    port = *pPort;
                }
            }
            return len <= 0 ? null : (outBuf[..len], ip, port);
        });

    /// <summary>
    /// Streams every queued masterserver outgoing packet:
    /// <c>await foreach (var p in host.EnumerateOutgoingPacketsAsync(cancellationToken)) { SendUdp(p); }</c>.
    /// Each MoveNext pulls one packet on the pump thread; the sequence completes as soon as
    /// Steam's queue is drained. Cancel <paramref name="cancellationToken"/> to stop early.
    /// Drain fully after every inbound packet — Steam batches replies behind HandleIncomingPacket.
    /// </summary>
    public async IAsyncEnumerable<SteamOutgoingPacket> EnumerateOutgoingPacketsAsync(
        int maxLen = 1400,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLen, 1);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var next = await TryGetNextOutgoingPacketAsync(maxLen).ConfigureAwait(false);
            if (next is null)
                yield break;
            yield return new SteamOutgoingPacket(next.Value.Packet, next.Value.Ip, next.Value.Port);
        }
    }
}
