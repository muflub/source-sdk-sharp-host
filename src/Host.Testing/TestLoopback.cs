using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace SourceSharp.Host.Testing;

/// <summary>
/// Where facts bind the listeners they pick ports for. Picking a port then binding it races
/// anything else that binds between the two: another test process (other worktrees run theirs
/// at the same time) and every outgoing connection's ephemeral source port, which the kernel
/// takes on 127.0.0.1. So each test process listens on its own loopback address, seeded by its
/// PID, in 127.200.0.0–127.249.255.255 (clear of the relay's 127.1/16, the fake pods'
/// 127.20–199 and the fake game's 127.254 peers); and a port is never handed out twice in a
/// process, so facts running in parallel cannot pick the same one.
/// </summary>
public static class TestLoopback
{
    public static IPAddress Address { get; } = ForPid(Environment.ProcessId);

    /// <summary><see cref="Address"/> as text, for "host:port" settings.</summary>
    public static string Host => Address.ToString();

    static readonly ConcurrentDictionary<int, bool> Given = new();

    public static IPAddress ForPid(int pid) => new([127, (byte)(200 + pid % 50), (byte)(pid / 50 % 256), 1]);

    /// <summary>A TCP port free on <see cref="Address"/> that this process has not handed out before.</summary>
    public static int FreePort()
    {
        while (true)
        {
            using var l = new TcpListener(Address, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            if (Given.TryAdd(port, true)) return port;
        }
    }

    /// <summary>"address:port" with a fresh <see cref="FreePort"/>.</summary>
    public static string Endpoint() => $"{Host}:{FreePort()}";
}
