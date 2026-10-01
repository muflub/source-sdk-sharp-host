using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using SourceSharp.Host.Relay;

namespace SourceSharp.Host.Launcher;

/// <summary>The libc socket calls the interposer falls through to (faked in facts).</summary>
public interface ISocketCalls
{
    nint RecvFrom(int fd, nint buf, nuint len, int flags, nint addr, nint addrLen);
    nint SendTo(int fd, nint buf, nuint len, int flags, nint addr, uint addrLen);
    int GetSockName(int fd, nint addr, nint addrLen);
}

/// <summary>
/// libc's own recvfrom / sendto / getsockname, looked up on an explicit <c>libc.so.6</c> handle: dlsym on
/// that handle returns libc's definitions, never the launcher's exported hooks, so falling through
/// cannot recurse. libc sets errno itself on these paths; the interposer never has to.
/// </summary>
public sealed class LibcSocketCalls : ISocketCalls
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate nint RecvFromFn(int fd, nint buf, nuint len, int flags, nint addr, nint addrLen);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate nint SendToFn(int fd, nint buf, nuint len, int flags, nint addr, uint addrLen);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GetSockNameFn(int fd, nint addr, nint addrLen);

    public static readonly LibcSocketCalls Instance = new();
    readonly RecvFromFn _recvfrom;
    readonly SendToFn _sendto;
    readonly GetSockNameFn _getsockname;

    LibcSocketCalls()
    {
        var libc = NativeLibrary.Load("libc.so.6");
        _recvfrom = Marshal.GetDelegateForFunctionPointer<RecvFromFn>(NativeLibrary.GetExport(libc, "recvfrom"));
        _sendto = Marshal.GetDelegateForFunctionPointer<SendToFn>(NativeLibrary.GetExport(libc, "sendto"));
        _getsockname = Marshal.GetDelegateForFunctionPointer<GetSockNameFn>(NativeLibrary.GetExport(libc, "getsockname"));
    }

    public nint RecvFrom(int fd, nint buf, nuint len, int flags, nint addr, nint addrLen) => _recvfrom(fd, buf, len, flags, addr, addrLen);
    public nint SendTo(int fd, nint buf, nuint len, int flags, nint addr, uint addrLen) => _sendto(fd, buf, len, flags, addr, addrLen);
    public int GetSockName(int fd, nint addr, nint addrLen) => _getsockname(fd, addr, addrLen);
}

/// <summary>
/// Interpose mode (plan D-H11 addition): the engine's game socket reads relayed datagrams with each
/// client's real address, and its sends to a relayed client go up that client's stream. Only the fd
/// bound to the game port is diverted (found with getsockname and cached); every other fd, every
/// address that is not a live session, and every call with flags falls through to libc untouched.
/// <para>What the engine does on that socket (docs/net-protocol.md §12, from the shipped libraries'
/// imports and observed behaviour): non-blocking, read with recvfrom, written with sendto; no
/// select/poll on it — the dedicated frame sleeps and then drains recvfrom until EAGAIN — so no
/// doorbell is needed.</para>
/// </summary>
public sealed class SocketInterposer(ISocketCalls libc, InterposeSwitchboard board, int gamePort)
{
    const short AF_INET = 2;
    const int SockaddrInSize = 16;
    readonly ConcurrentDictionary<int, bool> _isGame = new();
    long _served, _sent, _fellThrough;

    public long Served => Interlocked.Read(ref _served);
    public long Sent => Interlocked.Read(ref _sent);
    public long FellThrough => Interlocked.Read(ref _fellThrough);
    /// <summary>Whether a call on the engine's game socket has been seen (the hooks are bound).</summary>
    public bool GameSocketSeen => _isGame.Values.Any(v => v);

    public nint RecvFrom(int fd, nint buf, nuint len, int flags, nint addr, nint addrLen)
    {
        if (flags == 0 && IsGame(fd) && board.TryTakeForEngine(out var from, out var payload))
        {
            var n = (int)Math.Min((nuint)payload.Length, len);
            Marshal.Copy(payload, 0, buf, n); // UDP: a short buffer truncates, as recvfrom does
            if (addr != 0 && addrLen != 0)
            {
                if (Marshal.ReadInt32(addrLen) >= SockaddrInSize) WriteSockaddrIn(addr, from);
                Marshal.WriteInt32(addrLen, SockaddrInSize);
            }
            Interlocked.Increment(ref _served);
            return n;
        }
        Interlocked.Increment(ref _fellThrough);
        return libc.RecvFrom(fd, buf, len, flags, addr, addrLen);
    }

    public nint SendTo(int fd, nint buf, nuint len, int flags, nint addr, uint addrLen)
    {
        if (addr != 0 && addrLen >= SockaddrInSize && Marshal.ReadInt16(addr) == AF_INET && IsGame(fd))
        {
            var bytes = new byte[(int)len];
            Marshal.Copy(buf, bytes, 0, bytes.Length);
            if (board.ToClient(ReadKey(addr), bytes))
            {
                Interlocked.Increment(ref _sent);
                return (nint)len;
            }
        }
        Interlocked.Increment(ref _fellThrough);
        return libc.SendTo(fd, buf, len, flags, addr, addrLen);
    }

    /// <summary>Whether fd is the engine's game socket: an AF_INET socket bound to the game port.</summary>
    public bool IsGame(int fd)
    {
        if (_isGame.TryGetValue(fd, out var known)) return known;
        var sa = Marshal.AllocHGlobal(128);
        var len = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(len, 128);
            if (libc.GetSockName(fd, sa, len) != 0) return false; // not a socket (yet): ask again next time
            if (Marshal.ReadInt16(sa) != AF_INET) return _isGame[fd] = false;
            var port = BinaryPrimitives.ReverseEndianness((ushort)Marshal.ReadInt16(sa, 2));
            if (port == 0) return false; // not bound yet
            return _isGame[fd] = port == gamePort;
        }
        finally { Marshal.FreeHGlobal(sa); Marshal.FreeHGlobal(len); }
    }

    /// <summary>Forget a cached fd (closed and reused by another socket).</summary>
    public void Forget(int fd) => _isGame.TryRemove(fd, out _);

    static ulong ReadKey(nint sockaddrIn)
    {
        var port = BinaryPrimitives.ReverseEndianness((ushort)Marshal.ReadInt16(sockaddrIn, 2));
        var addr = BinaryPrimitives.ReverseEndianness((uint)Marshal.ReadInt32(sockaddrIn, 4));
        return InterposeSwitchboard.KeyOf(addr, port);
    }

    static void WriteSockaddrIn(nint sa, ulong key)
    {
        Marshal.WriteInt16(sa, 0, AF_INET);
        Marshal.WriteInt16(sa, 2, (short)BinaryPrimitives.ReverseEndianness((ushort)(key & 0xFFFF)));
        Marshal.WriteInt32(sa, 4, (int)BinaryPrimitives.ReverseEndianness((uint)(key >> 16)));
        Marshal.WriteInt64(sa, 8, 0);
    }
}

/// <summary>
/// The C entry points the NativeAOT executable exports as <c>recvfrom</c> and <c>sendto</c>. The main
/// executable is first in the global lookup scope, so the engine libraries loaded afterwards bind to
/// these. With no <see cref="Current"/> interposer (Loopback mode) they are a straight pass-through to
/// libc; an exception never crosses back into the engine — the call falls through instead.
/// </summary>
public static class InterposeExports
{
    static volatile SocketInterposer? _current;
    public static SocketInterposer? Current { get => _current; set => _current = value; }

    [UnmanagedCallersOnly(EntryPoint = "recvfrom")]
    public static nint RecvFrom(int fd, nint buf, nuint len, int flags, nint addr, nint addrLen)
    {
        try
        {
            return _current is { } i ? i.RecvFrom(fd, buf, len, flags, addr, addrLen)
                                     : LibcSocketCalls.Instance.RecvFrom(fd, buf, len, flags, addr, addrLen);
        }
        catch (Exception) { return LibcSocketCalls.Instance.RecvFrom(fd, buf, len, flags, addr, addrLen); }
    }

    [UnmanagedCallersOnly(EntryPoint = "sendto")]
    public static nint SendTo(int fd, nint buf, nuint len, int flags, nint addr, uint addrLen)
    {
        try
        {
            return _current is { } i ? i.SendTo(fd, buf, len, flags, addr, addrLen)
                                     : LibcSocketCalls.Instance.SendTo(fd, buf, len, flags, addr, addrLen);
        }
        catch (Exception) { return LibcSocketCalls.Instance.SendTo(fd, buf, len, flags, addr, addrLen); }
    }
}
