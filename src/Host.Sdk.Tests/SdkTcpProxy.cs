using System.Net;
using System.Net.Sockets;

namespace SourceSharp.Host.Sdk.Tests;

/// <summary>
/// A loopback TCP forwarder in front of the service: <see cref="Block"/> cuts every connection and
/// closes new ones at once (the host is unreachable), <see cref="Unblock"/> lets them through again.
/// <see cref="Accepted"/> counts connections that reached the service, so a fact can prove its
/// stimulus reached the subject.
/// </summary>
public sealed class SdkTcpProxy : IAsyncDisposable
{
    readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    volatile int _target;
    readonly CancellationTokenSource _stop = new();
    readonly List<TcpClient> _open = [];
    readonly Lock _lock = new();
    readonly Task _accept;
    volatile bool _blocked;
    int _accepted, _refused;

    public SdkTcpProxy(int targetPort)
    {
        _target = targetPort;
        _listener.Start();
        _accept = Task.Run(AcceptLoop);
    }

    public Uri Uri => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");
    public int Accepted => _accepted;
    public int Refused => _refused;
    public bool Blocked => _blocked;

    public void Block()
    {
        _blocked = true;
        Cut();
    }

    public void Unblock() => _blocked = false;

    /// <summary>Points new connections at another port (a restarted service).</summary>
    public void Retarget(int port) => _target = port;

    /// <summary>Drops every open connection (the client sees a reset and reconnects).</summary>
    public void Cut()
    {
        lock (_lock)
        {
            foreach (var c in _open)
            {
                try { if (c.Client is { } socket) socket.LingerState = new LingerOption(true, 0); }
                catch (Exception e) when (e is ObjectDisposedException or SocketException) { }
                c.Close();
            }
            _open.Clear();
        }
    }

    async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception) { return; }
            if (_blocked)
            {
                Interlocked.Increment(ref _refused);
                try { client.Client.LingerState = new LingerOption(true, 0); } catch (SocketException) { }
                client.Close();
                continue;
            }
            _ = Task.Run(() => Serve(client));
        }
    }

    async Task Serve(TcpClient client)
    {
        var upstream = new TcpClient();
        try { await upstream.ConnectAsync(IPAddress.Loopback, _target, _stop.Token); }
        catch (Exception) { client.Close(); upstream.Dispose(); return; }
        Interlocked.Increment(ref _accepted);
        lock (_lock) { _open.Add(client); _open.Add(upstream); }
        var a = client.GetStream();
        var b = upstream.GetStream();
        var epoch = Volatile.Read(ref _stallEpoch);
        try { await Task.WhenAny(Pipe(a, b, epoch), Pipe(b, a, epoch)); }
        catch (Exception) { }
        client.Close();
        // A stalled connection is a partition the service has not noticed: its side stays open.
        if (Volatile.Read(ref _stallEpoch) > epoch && !_stop.IsCancellationRequested) return;
        upstream.Close();
        lock (_lock) { _open.Remove(client); _open.Remove(upstream); }
    }

    int _stallEpoch;

    /// <summary>
    /// Every connection open now stops forwarding in both directions without closing (a half-open
    /// partition); when the client gives up on it, the service's side stays open. New connections flow.
    /// </summary>
    public void Stall() => Interlocked.Increment(ref _stallEpoch);

    async Task Pipe(NetworkStream from, NetworkStream to, int epoch)
    {
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var n = await from.ReadAsync(buffer, _stop.Token);
            if (n == 0) return;
            if (Volatile.Read(ref _stallEpoch) > epoch) continue; // swallowed
            await to.WriteAsync(buffer.AsMemory(0, n), _stop.Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        Cut();
        try { await _accept; } catch (Exception) { }
    }
}
