using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;
using SourceSharp.Host.Relay;

namespace SourceSharp.Host.Launcher.Tests;

/// <summary>A fake libc: an fd table of bound ports, and a record of every call that fell through.</summary>
sealed class FakeLibc : ISocketCalls
{
    public readonly Dictionary<int, int> Bound = [];
    public readonly List<string> Calls = [];
    public int GetSockNameCalls;

    public nint RecvFrom(int fd, nint buf, nuint len, int flags, nint addr, nint addrLen) { Calls.Add($"recvfrom {fd}"); return -1; }
    public nint SendTo(int fd, nint buf, nuint len, int flags, nint addr, uint addrLen) { Calls.Add($"sendto {fd}"); return (nint)len; }

    public int GetSockName(int fd, nint addr, nint addrLen)
    {
        GetSockNameCalls++;
        if (!Bound.TryGetValue(fd, out var port)) return -1;
        Marshal.WriteInt16(addr, 0, 2);
        Marshal.WriteInt16(addr, 2, (short)BinaryPrimitives.ReverseEndianness((ushort)port));
        Marshal.WriteInt32(addr, 4, 0);
        Marshal.WriteInt32(addrLen, 16);
        return 0;
    }
}

/// <summary>The interposed recvfrom / sendto on the engine's game socket (Interpose mode).</summary>
public class InterposeFacts : IDisposable
{
    const int Game = 7, Other = 8;
    static readonly IPEndPoint Client = IPEndPoint.Parse("203.0.113.7:27005");
    readonly FakeLibc _libc = new() { Bound = { [Game] = 27015, [Other] = 27005 } };
    readonly InterposeSwitchboard _board = new();
    readonly SocketInterposer _i;
    readonly nint _buf = Marshal.AllocHGlobal(2048), _sa = Marshal.AllocHGlobal(16), _salen = Marshal.AllocHGlobal(4);
    readonly List<byte[]> _down = [];
    readonly InterposeSession _session;

    public InterposeFacts()
    {
        _i = new SocketInterposer(_libc, _board, 27015);
        _session = _board.Register(Client, "s1", null, DateTimeOffset.UnixEpoch, b => { _down.Add(b); return true; }).Session;
    }

    public void Dispose() { Marshal.FreeHGlobal(_buf); Marshal.FreeHGlobal(_sa); Marshal.FreeHGlobal(_salen); }

    nint Recv(int fd, int len = 2048)
    {
        Marshal.WriteInt32(_salen, 16);
        return _i.RecvFrom(fd, _buf, (nuint)len, 0, _sa, _salen);
    }

    nint SendTo(int fd, IPEndPoint to, byte[] data)
    {
        Marshal.Copy(data, 0, _buf, data.Length);
        Marshal.WriteInt16(_sa, 0, 2);
        Marshal.WriteInt16(_sa, 2, (short)BinaryPrimitives.ReverseEndianness((ushort)to.Port));
        Marshal.WriteInt32(_sa, 4, BitConverter.ToInt32(to.Address.GetAddressBytes()));
        return _i.SendTo(fd, _buf, (nuint)data.Length, 0, _sa, 16);
    }

    [Fact]
    public void A_queued_datagram_is_read_from_the_game_socket_with_the_clients_real_address()
    {
        _board.FromClient(_session, [1, 2, 3]);
        Assert.Equal(3, Recv(Game));
        var bytes = new byte[3];
        Marshal.Copy(_buf, bytes, 0, 3);
        Assert.Equal([1, 2, 3], bytes);
        var port = BinaryPrimitives.ReverseEndianness((ushort)Marshal.ReadInt16(_sa, 2));
        var addr = new IPAddress(BitConverter.GetBytes(Marshal.ReadInt32(_sa, 4)));
        Assert.Equal((2, Client, 16), (Marshal.ReadInt16(_sa), new IPEndPoint(addr, port), Marshal.ReadInt32(_salen)));
        Assert.Empty(_libc.Calls);
    }

    [Fact]
    public void With_nothing_queued_the_game_socket_falls_through_to_libc()
    {
        Assert.Equal(-1, Recv(Game));
        Assert.Equal(["recvfrom 7"], _libc.Calls);
    }

    [Fact]
    public void Another_socket_falls_through_and_leaves_the_queue_alone()
    {
        _board.FromClient(_session, [1]);
        Recv(Other);
        Assert.Equal(["recvfrom 8"], _libc.Calls);
        Assert.Equal(1, _board.Queued);
    }

    [Fact]
    public void A_short_buffer_truncates_like_udp()
    {
        _board.FromClient(_session, [1, 2, 3, 4, 5]);
        Assert.Equal(2, Recv(Game, len: 2));
        Assert.Equal(0, _board.Queued);
    }

    [Fact]
    public void Sending_from_the_game_socket_to_a_relayed_client_goes_down_its_stream()
    {
        Assert.Equal(2, SendTo(Game, Client, [4, 2]));
        Assert.Equal([4, 2], Assert.Single(_down));
        Assert.Empty(_libc.Calls);
    }

    [Fact]
    public void Sending_to_an_address_that_is_not_a_session_falls_through()
    {
        SendTo(Game, IPEndPoint.Parse("198.51.100.9:27005"), [4, 2]);
        Assert.Equal(["sendto 7"], _libc.Calls);
        Assert.Empty(_down);
    }

    [Fact]
    public void Sending_from_another_socket_falls_through_even_to_a_relayed_client()
    {
        SendTo(Other, Client, [4, 2]);
        Assert.Equal(["sendto 8"], _libc.Calls);
        Assert.Empty(_down);
    }

    [Fact]
    public void The_game_socket_is_identified_once_and_cached()
    {
        Recv(Game); Recv(Game); Recv(Game);
        Assert.Equal(1, _libc.GetSockNameCalls);
    }

    [Fact]
    public void An_unbound_socket_is_asked_again_later()
    {
        _libc.Bound.Remove(9);
        Assert.False(_i.IsGame(9));
        _libc.Bound[9] = 27015;
        Assert.True(_i.IsGame(9));
    }

    [Fact]
    public void Null_address_arguments_are_tolerated()
    {
        _board.FromClient(_session, [1]);
        Assert.Equal(1, _i.RecvFrom(Game, _buf, 2048, 0, 0, 0));
    }

    [Fact]
    public void A_call_with_flags_falls_through()
    {
        _board.FromClient(_session, [1]);
        _i.RecvFrom(Game, _buf, 2048, 2 /* MSG_PEEK */, _sa, _salen);
        Assert.Equal(["recvfrom 7"], _libc.Calls);
        Assert.Equal(1, _board.Queued);
    }
}

/// <summary>The engine arguments Interpose mode needs (docs/net-protocol.md §12).</summary>
public class InterposeArgsFacts
{
    [Fact]
    public void Interpose_makes_the_engine_send_to_localhost_through_its_socket()
    {
        var args = LauncherApp.EngineArgs(new RelayOptions { Mode = "Interpose" }, ["-game", "x"]);
        Assert.Equal(["-game", "x", "+net_usesocketsforloopback", "1"], args);
    }

    [Fact]
    public void Loopback_mode_leaves_the_engine_args_alone()
    {
        Assert.Equal(["-game", "x"], LauncherApp.EngineArgs(new RelayOptions(), ["-game", "x"]));
    }

    [Fact]
    public void A_given_net_usesocketsforloopback_is_not_overridden()
    {
        var given = new[] { "+net_usesocketsforloopback", "0" };
        Assert.Equal(given, LauncherApp.EngineArgs(new RelayOptions { Mode = "Interpose" }, given));
    }

    [Fact]
    public void The_engine_is_given_the_added_args_in_interpose_mode()
    {
        IReadOnlyList<string>? seen = null;
        LauncherApp.Run(new LauncherOptions { ChangeDirectory = false, BlockSigtermOnEngineThread = false },
            new RelayOptions { Mode = "Interpose", Listen = "127.0.0.1:0", InfoListen = "127.0.0.1:0", Health = "127.0.0.1:0", EngineEndpoint = "127.0.0.1:27015", PeerPort = 0 },
            "launcher", ["-game", "x"], new FakeEngine((argc, argv) => { seen = Argv.Read(argc, argv); return 0; }), new LauncherEngineState());
        Assert.Equal(["launcher", "-game", "x", "+net_usesocketsforloopback", "1"], seen);
    }
}
