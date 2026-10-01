using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace SourceSharp.Host.FakeClient;

/// <summary>One datagram a <see cref="ToyBackend"/> received: who sent it, when, and its bytes.</summary>
public sealed record Received(IPEndPoint From, DateTimeOffset At, long Order, byte[] Bytes)
{
    public byte Type => ToyWire.TypeOf(Bytes);
    public byte Kind => ToyWire.KindOf(Bytes);
    public bool Connectionless => ToyWire.IsConnectionless(Bytes);
}

/// <summary>
/// A UDP server hosting one toy game instance (plan §7.6). It answers the toy handshake, echoes
/// keepalives with its instance id, and records every datagram it receives with its source and time
/// — the receive log facts assert on. Like Source it binds a challenge to the peer that asked for it,
/// and like Source's <c>MAX_REUSE_PER_IP</c> it refuses a new handshake from an IP that already has
/// <see cref="MaxHandshakingPerIp"/> handshakes in progress (docs/net-protocol.md).
/// </summary>
public sealed class ToyBackend : IDisposable
{
    readonly Socket _socket;
    readonly CancellationTokenSource _stop = new();
    readonly Lock _sync = new();
    readonly List<Received> _log = [];
    readonly Dictionary<IPEndPoint, Peer> _peers = [];
    readonly Task _loop;
    long _order;

    sealed class Peer
    {
        public int Challenge;
        public bool Accepted;
        public bool Active;
        public ulong SteamId;
        public string Name = "";
    }

    /// <summary>The instance id keepalive echoes and accepts carry.</summary>
    public string InstanceId { get; }
    public IPEndPoint EndPoint { get; }

    /// <summary>An unready backend logs what it receives and answers nothing (a pod still booting).</summary>
    public bool Ready { get; set; } = true;

    /// <summary>Delay before each answer (a slow pod).</summary>
    public TimeSpan ResponseDelay { get; set; } = TimeSpan.Zero;

    /// <summary>Handshakes in progress allowed per source IP (Source's MAX_REUSE_PER_IP); 0 = no limit.</summary>
    public int MaxHandshakingPerIp { get; set; } = 5;

    public ToyBackend(string instanceId, IPAddress? bind = null, int port = 0)
    {
        InstanceId = instanceId;
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.Bind(new IPEndPoint(bind ?? IPAddress.Loopback, port));
        EndPoint = (IPEndPoint)_socket.LocalEndPoint!;
        _loop = Task.Run(ReceiveLoop);
    }

    /// <summary>Everything received so far, in arrival order.</summary>
    public IReadOnlyList<Received> Log { get { lock (_sync) return [.. _log]; } }

    /// <summary>The distinct peers that have sent anything.</summary>
    public IReadOnlyList<IPEndPoint> Senders { get { lock (_sync) return _log.Select(r => r.From).Distinct().ToList(); } }

    /// <summary>The SteamIDs of the peers this backend has accepted, by peer.</summary>
    public IReadOnlyDictionary<IPEndPoint, ulong> Accepted
    {
        get { lock (_sync) return _peers.Where(p => p.Value.Accepted).ToDictionary(p => p.Key, p => p.Value.SteamId); }
    }

    /// <summary>Handshakes in progress from one IP (challenged, not yet active).</summary>
    public int HandshakingFrom(IPAddress ip) { lock (_sync) return _peers.Count(p => p.Key.Address.Equals(ip) && !p.Value.Active); }

    /// <summary>Sends <c>retry</c> to one peer.</summary>
    public void SendRetry(IPEndPoint peer) => _socket.SendTo(ToyWire.RetryPacket(), peer);

    /// <summary>Sends <c>retry</c> to every accepted peer; returns how many.</summary>
    public int SendRetryAll()
    {
        List<IPEndPoint> peers;
        lock (_sync) { peers = _peers.Where(p => p.Value.Accepted).Select(p => p.Key).ToList(); }
        foreach (var p in peers) SendRetry(p);
        return peers.Count;
    }

    /// <summary>Sends an arbitrary datagram to a peer (a server-initiated netchannel packet).</summary>
    public void SendTo(IPEndPoint peer, byte[] bytes) => _socket.SendTo(bytes, peer);

    /// <summary>Waits until the receive log satisfies <paramref name="condition"/>; false at the deadline.</summary>
    public async Task<bool> WaitForAsync(Func<IReadOnlyList<Received>, bool> condition, TimeSpan? timeout = null)
    {
        var sw = Stopwatch.StartNew();
        var limit = timeout ?? TimeSpan.FromSeconds(5);
        while (sw.Elapsed < limit)
        {
            if (condition(Log)) return true;
            await Task.Delay(5);
        }
        return condition(Log);
    }

    async Task ReceiveLoop()
    {
        var buf = new byte[65536];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!_stop.IsCancellationRequested)
        {
            SocketReceiveFromResult r;
            try { r = await _socket.ReceiveFromAsync(buf, SocketFlags.None, any, _stop.Token); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }
            var from = (IPEndPoint)r.RemoteEndPoint;
            var bytes = buf.AsSpan(0, r.ReceivedBytes).ToArray();
            lock (_sync) _log.Add(new Received(from, DateTimeOffset.UtcNow, _order++, bytes));
            if (!Ready) continue;
            if (ResponseDelay > TimeSpan.Zero)
            {
                var delay = ResponseDelay;
                _ = Task.Run(async () => { await Task.Delay(delay); Answer(from, bytes); });
            }
            else Answer(from, bytes);
        }
    }

    void Answer(IPEndPoint from, byte[] p)
    {
        byte[]? reply = null;
        lock (_sync)
        {
            switch (ToyWire.TypeOf(p))
            {
                case ToyWire.GetChallenge when ToyWire.TryReadGetChallenge(p, out var cc):
                {
                    if (!_peers.TryGetValue(from, out var peer))
                    {
                        if (MaxHandshakingPerIp > 0 && _peers.Count(x => x.Key.Address.Equals(from.Address) && !x.Value.Active) >= MaxHandshakingPerIp)
                        {
                            reply = ToyWire.RejectPacket("too many connections from your address");
                            break;
                        }
                        peer = _peers[from] = new Peer();
                    }
                    // A fresh handshake from a known peer starts over, as Source's does.
                    peer.Challenge = Random.Shared.Next(1, int.MaxValue);
                    peer.Accepted = peer.Active = false;
                    reply = ToyWire.ChallengePacket(peer.Challenge, cc);
                    break;
                }
                case ToyWire.Connect when ToyWire.TryReadConnect(p, out var challenge, out var steamId, out var name):
                {
                    if (!_peers.TryGetValue(from, out var peer) || peer.Challenge != challenge)
                    {
                        reply = ToyWire.RejectPacket("bad challenge");
                        break;
                    }
                    peer.Accepted = true;
                    peer.SteamId = steamId ?? 0;
                    peer.Name = name;
                    reply = ToyWire.AcceptPacket(InstanceId);
                    break;
                }
                case 0:
                {
                    if (!_peers.TryGetValue(from, out var peer) || !peer.Accepted) break;
                    peer.Active = true;
                    if (ToyWire.KindOf(p) == ToyWire.Keepalive)
                        reply = ToyWire.EchoPacket(ToyWire.SeqOf(p), InstanceId);
                    break;
                }
            }
        }
        if (reply is null) return;
        try { _socket.SendTo(reply, from); }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _socket.Dispose();
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _stop.Dispose();
    }
}
