using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SourceSharp.Host.Proto.Relay;

namespace SourceSharp.Host.Relay;

/// <summary>
/// PeerRelay (relay.proto): one stream per client session. The first message is Open; the sidecar
/// takes the session's loopback address from the <see cref="PeerTable"/>, answers Opened with the
/// peer, and then relays payload-agnostically: each Datagram up is sent to the engine from that peer,
/// each datagram the engine sends to the peer goes down. The stream's end releases the peer.
/// </summary>
public sealed class PeerRelayService(PeerTable table, InterposeSwitchboard board, RelayOptions options, TimeProvider time, RelayMetrics metrics,
    ILogger<PeerRelayService>? log = null)
    : PeerRelay.PeerRelayBase
{
    readonly ILogger _log = (ILogger?)log ?? NullLogger.Instance;
    readonly IPEndPoint _engine = IPEndPoint.Parse(options.EngineEndpoint);

    public override async Task Relay(IAsyncStreamReader<RelayUp> up, IServerStreamWriter<RelayDown> down, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        if (!await up.MoveNext(ct) || up.Current.KindCase != RelayUp.KindOneofCase.Open)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "the first message must be Open"));
        var open = up.Current.Open;
        ulong? steamId = ulong.TryParse(open.Steamid, out var sid) && sid != 0 ? sid : null;
        IPEndPoint.TryParse(open.PeerHint, out var hint);
        if (options.Interpose)
        {
            await RelayInterposed(open, steamId, up, down, ct);
            return;
        }

        var (lease, superseded) = table.Acquire(open.SessionId, open.ClientAddr, steamId, hint);
        if (superseded is not null)
            _log.LogInformation("peer {Peer}: session {Old} superseded by {New} for {SteamId}", superseded.Peer, superseded.SessionId, open.SessionId, steamId);
        metrics.Streams.Inc();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.Cut.Token);
        var writeLock = new SemaphoreSlim(1, 1);
        var lastActivity = time.GetTimestamp();
        string? reason = null;
        using var idle = time.CreateTimer(_ =>
        {
            if (time.GetElapsedTime(Interlocked.Read(ref lastActivity)) < options.IdleTimeout) return;
            lease.CutReason ??= "idle";
            try { lease.Cut.Cancel(); } catch (ObjectDisposedException) { }
        }, null, options.IdleTimeout / 4, options.IdleTimeout / 4);

        try
        {
            await Write(down, writeLock, new RelayDown { Opened = new Opened { Peer = lease.Peer.ToString() } }, stop.Token);
            _log.LogInformation("session {Session} ({Client}, {SteamId}) on peer {Peer}", open.SessionId, open.ClientAddr, steamId, lease.Peer);
            var engineLoop = Task.Run(() => EngineToStream(lease, down, writeLock, () => Interlocked.Exchange(ref lastActivity, time.GetTimestamp()), stop.Token));
            try
            {
                while (await up.MoveNext(stop.Token))
                {
                    if (up.Current.KindCase != RelayUp.KindOneofCase.Datagram) continue;
                    Interlocked.Exchange(ref lastActivity, time.GetTimestamp());
                    var payload = up.Current.Datagram.Payload.Span;
                    metrics.DatagramsUp.Inc();
                    metrics.BytesUp.Inc(payload.Length);
                    try { lease.Socket.SendTo(payload, SocketFlags.None, _engine); }
                    catch (SocketException e) { _log.LogDebug("send to engine failed: {Error}", e.SocketErrorCode); }
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            reason = lease.CutReason;
            stop.Cancel();
            try { await engineLoop; } catch (OperationCanceledException) { }
        }
        finally
        {
            table.Release(lease);
            metrics.Streams.Dec();
            _log.LogInformation("session {Session} left peer {Peer} ({Reason})", open.SessionId, lease.Peer, reason ?? "stream ended");
        }
        if (reason is not null && !ct.IsCancellationRequested)
        {
            try { await Write(down, writeLock, new RelayDown { Closed = new Closed { Reason = reason } }, ct); }
            catch (Exception e) when (e is InvalidOperationException or IOException or OperationCanceledException) { }
        }
    }

    /// <summary>
    /// Interpose mode: no loopback socket. The session registers its real client address with the
    /// <see cref="InterposeSwitchboard"/>; datagrams up are queued for the engine's interposed recvfrom,
    /// datagrams the engine sends to the client's address come back through an outbox and go down.
    /// Opened reports the real address as the peer.
    /// </summary>
    async Task RelayInterposed(Open open, ulong? steamId, IAsyncStreamReader<RelayUp> up, IServerStreamWriter<RelayDown> down, CancellationToken ct)
    {
        if (!IPEndPoint.TryParse(open.ClientAddr, out var client) || client.AddressFamily != AddressFamily.InterNetwork)
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"client_addr '{open.ClientAddr}' is not an IPv4 ip:port"));
        var outbox = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        var (s, superseded) = board.Register(client, open.SessionId, steamId, time.GetUtcNow(), b => outbox.Writer.TryWrite(b));
        if (superseded is not null)
            _log.LogInformation("client {Client}: session {Old} superseded by {New}", client, superseded.SessionId, open.SessionId);
        metrics.Streams.Inc();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, s.Cut.Token);
        var lastActivity = time.GetTimestamp();
        using var idle = time.CreateTimer(_ =>
        {
            if (time.GetElapsedTime(Interlocked.Read(ref lastActivity)) < options.IdleTimeout) return;
            s.CutReason ??= "idle";
            try { s.Cut.Cancel(); } catch (ObjectDisposedException) { }
        }, null, options.IdleTimeout / 4, options.IdleTimeout / 4);
        string? reason = null;
        try
        {
            await down.WriteAsync(new RelayDown { Opened = new Opened { Peer = client.ToString() } }, stop.Token);
            var writer = Task.Run(async () =>
            {
                try
                {
                    await foreach (var b in outbox.Reader.ReadAllAsync(stop.Token))
                    {
                        Interlocked.Exchange(ref lastActivity, time.GetTimestamp());
                        metrics.DatagramsDown.Inc();
                        metrics.BytesDown.Inc(b.Length);
                        await down.WriteAsync(new RelayDown { Datagram = new Datagram { Payload = UnsafeByteOperations.UnsafeWrap(b) } }, stop.Token);
                    }
                }
                catch (Exception e) when (e is OperationCanceledException or InvalidOperationException or IOException) { }
            });
            try
            {
                while (await up.MoveNext(stop.Token))
                {
                    if (up.Current.KindCase != RelayUp.KindOneofCase.Datagram) continue;
                    Interlocked.Exchange(ref lastActivity, time.GetTimestamp());
                    var payload = up.Current.Datagram.Payload.Span;
                    metrics.DatagramsUp.Inc();
                    metrics.BytesUp.Inc(payload.Length);
                    board.FromClient(s, payload);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            reason = s.CutReason;
            stop.Cancel();
            await writer;
        }
        finally
        {
            board.Unregister(s);
            outbox.Writer.TryComplete();
            metrics.Streams.Dec();
        }
        if (reason is not null && !ct.IsCancellationRequested)
        {
            try { await down.WriteAsync(new RelayDown { Closed = new Closed { Reason = reason } }, ct); }
            catch (Exception e) when (e is InvalidOperationException or IOException or OperationCanceledException) { }
        }
    }

    static async Task Write(IServerStreamWriter<RelayDown> down, SemaphoreSlim gate, RelayDown m, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { await down.WriteAsync(m, ct); }
        finally { gate.Release(); }
    }

    async Task EngineToStream(PeerLease lease, IServerStreamWriter<RelayDown> down, SemaphoreSlim gate, Action touch, CancellationToken ct)
    {
        var buf = ArrayPool<byte>.Shared.Rent(65536);
        var from = new SocketAddress(AddressFamily.InterNetwork);
        var engine = _engine.Serialize();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n;
                try { n = await lease.Socket.ReceiveFromAsync(buf.AsMemory(), SocketFlags.None, from, ct); }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { continue; }
                if (!from.Equals(engine)) continue; // only the engine may answer a peer
                touch();
                metrics.DatagramsDown.Inc();
                metrics.BytesDown.Inc(n);
                try
                {
                    await Write(down, gate, new RelayDown { Datagram = new Datagram { Payload = ByteString.CopyFrom(buf, 0, n) } }, ct);
                }
                catch (Exception e) when (e is InvalidOperationException or IOException) { return; }
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }
}

/// <summary>PeerInfo (relay.proto): who is behind a peer, for the game server in this pod.</summary>
public sealed class PeerInfoService(PeerTable table, InterposeSwitchboard board, RelayOptions options) : PeerInfo.PeerInfoBase
{
    static PeerEntry Entry(PeerLease l) => new()
    {
        Found = true,
        Peer = l.Peer.ToString(),
        ClientAddr = l.ClientAddr,
        SessionId = l.SessionId,
        Steamid = l.SteamId?.ToString() ?? "",
        OpenedUnixMs = l.Opened.ToUnixTimeMilliseconds(),
    };

    /// <summary>Interpose mode: the peer the engine sees is the client's real address.</summary>
    static PeerEntry Entry(InterposeSession s) => new()
    {
        Found = true,
        Peer = s.Client.ToString(),
        ClientAddr = s.Client.ToString(),
        SessionId = s.SessionId,
        Steamid = s.SteamId?.ToString() ?? "",
        OpenedUnixMs = s.Opened.ToUnixTimeMilliseconds(),
    };

    public override Task<PeerEntry> Resolve(ResolveRequest request, ServerCallContext context)
    {
        if (!IPEndPoint.TryParse(request.Peer, out var peer))
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"'{request.Peer}' is not ip:port"));
        if (options.Interpose)
            return Task.FromResult(board.Resolve(peer) is { } s ? Entry(s) : new PeerEntry { Found = false, Peer = request.Peer });
        return Task.FromResult(table.Resolve(peer) is { } l ? Entry(l) : new PeerEntry { Found = false, Peer = request.Peer });
    }

    public override Task<PeerList> List(ListRequest request, ServerCallContext context)
    {
        var list = new PeerList();
        if (options.Interpose) list.Peers.AddRange(board.Live.OrderBy(s => s.Opened).Select(Entry));
        else list.Peers.AddRange(table.Live.OrderBy(l => l.Opened).Select(Entry));
        return Task.FromResult(list);
    }
}
