using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace SourceSharp.Host.FakeClient;

/// <summary>
/// The client side of the toy protocol (plan §7.6): connects to an address (a <see cref="ToyBackend"/>
/// or a gateway in front of some), follows <c>retry</c> by restarting the handshake at the same
/// address, re-sends <c>connect</c> when it receives a second challenge (as the identity-first
/// handshake needs, §8.1b), and reports which instance it is on and every step it took.
/// </summary>
public sealed class FakeClient : IDisposable
{
    readonly Socket _socket;
    readonly IPEndPoint _server;
    readonly CancellationTokenSource _stop = new();
    readonly Task _loop;
    readonly Lock _sync = new();
    readonly ConcurrentQueue<string> _steps = new();
    readonly ConcurrentQueue<byte[]> _netchannel = new();
    readonly ConcurrentQueue<string> _echoes = new();
    Timer? _resend;
    int _clientChallenge;
    int _challenge;
    bool _sentConnect;
    uint _seq;
    uint _echoFor; // the keepalive KeepaliveAsync waits on; 0 = none (seqs start at 1)
    TaskCompletionSource<string> _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    TaskCompletionSource<string> _echo = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ulong SteamId { get; }
    public string Name { get; }
    /// <summary>Send an unreadable ticket in connect (the identity-first fallback, §8.1b).</summary>
    public bool UnreadableTicket { get; init; }
    /// <summary>How long to wait for an answer before re-sending the current handshake packet; infinite = never.</summary>
    public TimeSpan ResendInterval { get; init; } = Timeout.InfiniteTimeSpan;

    public IPEndPoint LocalEndPoint { get; }
    /// <summary>The instance id of the last accept, null before the first.</summary>
    public string? InstanceId { get; private set; }
    public bool Connected { get; private set; }
    public string? RejectReason { get; private set; }
    /// <summary>How many times the server sent <c>retry</c>.</summary>
    public int Retries { get; private set; }
    /// <summary>Every handshake step, in order: "q", "A", "k", "B inst", "9 reason", "retry".</summary>
    public IReadOnlyList<string> Steps => [.. _steps];
    /// <summary>The instance id of every keepalive echo, in order.</summary>
    public IReadOnlyList<string> Echoes => [.. _echoes];
    /// <summary>Netchannel packets received other than echoes and retries.</summary>
    public IReadOnlyList<byte[]> Netchannel => [.. _netchannel];

    public FakeClient(IPEndPoint server, ulong steamId, string name = "player", IPAddress? bind = null)
    {
        _server = server;
        SteamId = steamId;
        Name = name;
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.Bind(new IPEndPoint(bind ?? IPAddress.Loopback, 0));
        LocalEndPoint = (IPEndPoint)_socket.LocalEndPoint!;
        _loop = Task.Run(ReceiveLoop);
    }

    /// <summary>Starts the handshake and waits for an accept; returns the instance id.</summary>
    public async Task<string> ConnectAsync(TimeSpan? timeout = null)
    {
        StartHandshake();
        return await WaitConnectedAsync(timeout);
    }

    /// <summary>Starts the handshake without waiting.</summary>
    public void StartHandshake()
    {
        lock (_sync)
        {
            Connected = false;
            _sentConnect = false;
            _clientChallenge = Random.Shared.Next(1, int.MaxValue);
            if (_connected.Task.IsCompleted) _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        Send(ToyWire.GetChallengePacket(_clientChallenge), "q");
        ArmResend();
    }

    /// <summary>Waits for the current handshake's accept (or reject, which throws).</summary>
    public async Task<string> WaitConnectedAsync(TimeSpan? timeout = null)
    {
        Task<string> t;
        lock (_sync) t = _connected.Task;
        var done = await Task.WhenAny(t, Task.Delay(timeout ?? TimeSpan.FromSeconds(5)));
        if (done != t) throw new TimeoutException($"not connected; steps: {string.Join(", ", Steps)}");
        return await t;
    }

    /// <summary>Waits until the client is connected to <paramref name="instanceId"/> (after a retry, say).</summary>
    public async Task<bool> WaitOnInstanceAsync(string instanceId, TimeSpan? timeout = null)
    {
        var sw = Stopwatch.StartNew();
        var limit = timeout ?? TimeSpan.FromSeconds(5);
        while (sw.Elapsed < limit)
        {
            if (Connected && InstanceId == instanceId) return true;
            await Task.Delay(5);
        }
        return Connected && InstanceId == instanceId;
    }

    /// <summary>Waits until <paramref name="condition"/> holds for this client; false at the deadline.</summary>
    public async Task<bool> WaitForAsync(Func<FakeClient, bool> condition, TimeSpan? timeout = null)
    {
        var sw = Stopwatch.StartNew();
        var limit = timeout ?? TimeSpan.FromSeconds(5);
        while (sw.Elapsed < limit)
        {
            if (condition(this)) return true;
            await Task.Delay(5);
        }
        return condition(this);
    }

    /// <summary>
    /// Sends a keepalive and waits for the echo of that keepalive (by seq: a late echo of an
    /// earlier one, such as the keepalive sent at accept, is not the answer); returns the instance id that answered.
    /// </summary>
    public async Task<string> KeepaliveAsync(TimeSpan? timeout = null)
    {
        Task<string> t;
        uint seq;
        lock (_sync)
        {
            _echo = new(TaskCreationOptions.RunContinuationsAsynchronously);
            t = _echo.Task;
            seq = _echoFor = ++_seq;
        }
        Send(ToyWire.KeepalivePacket(seq), null);
        var done = await Task.WhenAny(t, Task.Delay(timeout ?? TimeSpan.FromSeconds(5)));
        if (done != t) throw new TimeoutException("no keepalive echo");
        return await t;
    }

    /// <summary>Sends a keepalive and does not wait.</summary>
    public void SendKeepalive() => Send(ToyWire.KeepalivePacket(++_seq), null);

    /// <summary>Sends a netchannel data packet with the next sequence number; returns it.</summary>
    public uint SendData(ReadOnlySpan<byte> payload)
    {
        var seq = ++_seq;
        Send(ToyWire.DataPacket(seq, payload), null);
        return seq;
    }

    /// <summary>Sends raw bytes to the server address.</summary>
    public void SendRaw(byte[] bytes) => _socket.SendTo(bytes, _server);

    void Send(byte[] bytes, string? step)
    {
        if (step is not null) _steps.Enqueue(step);
        try { _socket.SendTo(bytes, _server); }
        catch (ObjectDisposedException) { }
    }

    void ArmResend()
    {
        if (ResendInterval == Timeout.InfiniteTimeSpan) return;
        _resend?.Dispose();
        _resend = new Timer(_ =>
        {
            if (Connected || RejectReason is not null) return;
            if (_sentConnect) Send(Connect(), "k");
            else Send(ToyWire.GetChallengePacket(_clientChallenge), "q");
        }, null, ResendInterval, ResendInterval);
    }

    byte[] Connect() => ToyWire.ConnectPacket(_challenge,
        UnreadableTicket ? ToyWire.UnreadableTicket() : ToyWire.Ticket(SteamId), Name);

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
            Handle(buf.AsSpan(0, r.ReceivedBytes).ToArray());
        }
    }

    void Handle(byte[] p)
    {
        switch (ToyWire.TypeOf(p))
        {
            case ToyWire.Challenge when ToyWire.TryReadChallenge(p, out var challenge, out var cc):
                if (cc != _clientChallenge || Connected) return;
                if (_sentConnect && challenge == _challenge) return;
                // A second, different challenge after connect restarts connect with it (§8.1b).
                _steps.Enqueue("A");
                _challenge = challenge;
                _sentConnect = true;
                Send(Connect(), "k");
                return;
            case ToyWire.Accept:
            {
                var id = ToyWire.TextOf(p);
                InstanceId = id;
                Connected = true;
                RejectReason = null;
                _steps.Enqueue("B " + id);
                // The first netchannel packet after accept: ends the handshake at the gateway and backend.
                SendKeepalive();
                TaskCompletionSource<string> c;
                lock (_sync) c = _connected;
                c.TrySetResult(id);
                return;
            }
            case ToyWire.Reject:
            {
                var reason = ToyWire.TextOf(p);
                RejectReason = reason;
                _steps.Enqueue("9 " + reason);
                TaskCompletionSource<string> c;
                lock (_sync) c = _connected;
                c.TrySetException(new InvalidOperationException("rejected: " + reason));
                return;
            }
            case 0:
                switch (ToyWire.KindOf(p))
                {
                    case ToyWire.Echo:
                    {
                        var id = System.Text.Encoding.UTF8.GetString(ToyWire.PayloadOf(p));
                        _echoes.Enqueue(id);
                        TaskCompletionSource<string>? e;
                        lock (_sync) e = ToyWire.SeqOf(p) == _echoFor ? _echo : null;
                        e?.TrySetResult(id);
                        return;
                    }
                    case ToyWire.Retry:
                        Retries++;
                        _steps.Enqueue("retry");
                        StartHandshake();
                        return;
                    default:
                        _netchannel.Enqueue(p);
                        return;
                }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _resend?.Dispose();
        _socket.Dispose();
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _stop.Dispose();
    }
}
