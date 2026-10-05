using System.Runtime.InteropServices;
using System.Text;

namespace SourceSharp.Host.Steam.Native;

/// <summary>Callback over a NUL-terminated UTF-8 buffer that is only valid for the duration of the call.</summary>
internal delegate void Utf8Action<TState>(TState state, IntPtr utf8);

/// <summary><see cref="Utf8Action{TState}"/> that returns a value.</summary>
internal delegate TResult Utf8Func<TState, TResult>(TState state, IntPtr utf8);

/// <summary>Two-buffer form (key/value style calls).</summary>
internal delegate void Utf8Action2<TState>(TState state, IntPtr utf8a, IntPtr utf8b);

/// <summary><see cref="Utf8Action2{TState}"/> that returns a value.</summary>
internal delegate TResult Utf8Func2<TState, TResult>(TState state, IntPtr utf8a, IntPtr utf8b);

/// <summary>
/// Hands Valve's <c>const char*</c> parameters NUL-terminated UTF-8 buffers without per-call
/// native allocations: exact-capacity stackalloc for short strings (UTF-8 is at most 3 bytes per
/// UTF-16 unit, so length*4 is a safe bound), native heap only past <see cref="StackLimitBytes"/>.
/// Replaces Marshal.StringToCoTaskMemUTF8/FreeCoTaskMem round-trips.
/// The state parameter carries the call context so callbacks can be static lambdas (no closure allocs).
/// </summary>
internal static unsafe class Utf8
{
    public const int StackLimitBytes = 1024;

    public static void Use<TState>(string? value, TState state, Utf8Action<TState> action)
    {
        int capacity = (value?.Length ?? 0) * 4; // UTF-8 worst case: 3 bytes per UTF-16 unit
        if (capacity <= StackLimitBytes)
        {
            byte* buffer = stackalloc byte[capacity + 1];
            Write(value, buffer, capacity);
            action(state, (IntPtr)buffer);
            return;
        }

        byte* heap = Alloc(value, capacity);
        try { action(state, (IntPtr)heap); }
        finally { NativeMemory.Free(heap); }
    }

    public static TResult Eval<TState, TResult>(string? value, TState state, Utf8Func<TState, TResult> func)
    {
        int capacity = (value?.Length ?? 0) * 4;
        if (capacity <= StackLimitBytes)
        {
            byte* buffer = stackalloc byte[capacity + 1];
            Write(value, buffer, capacity);
            return func(state, (IntPtr)buffer);
        }

        byte* heap = Alloc(value, capacity);
        try { return func(state, (IntPtr)heap); }
        finally { NativeMemory.Free(heap); }
    }

    public static void Use2<TState>(string? a, string? b, TState state, Utf8Action2<TState> action)
    {
        int ca = (a?.Length ?? 0) * 4, cb = (b?.Length ?? 0) * 4;
        if (ca <= StackLimitBytes && cb <= StackLimitBytes)
        {
            byte* ba = stackalloc byte[ca + 1];
            byte* bb = stackalloc byte[cb + 1];
            Write(a, ba, ca);
            Write(b, bb, cb);
            action(state, (IntPtr)ba, (IntPtr)bb);
            return;
        }

        byte* ha = Alloc(a, ca), hb = Alloc(b, cb);
        try { action(state, (IntPtr)ha, (IntPtr)hb); }
        finally { NativeMemory.Free(ha); NativeMemory.Free(hb); }
    }

    public static TResult Eval2<TState, TResult>(string? a, string? b, TState state, Utf8Func2<TState, TResult> func)
    {
        int ca = (a?.Length ?? 0) * 4, cb = (b?.Length ?? 0) * 4;
        if (ca <= StackLimitBytes && cb <= StackLimitBytes)
        {
            byte* ba = stackalloc byte[ca + 1];
            byte* bb = stackalloc byte[cb + 1];
            Write(a, ba, ca);
            Write(b, bb, cb);
            return func(state, (IntPtr)ba, (IntPtr)bb);
        }

        byte* ha = Alloc(a, ca), hb = Alloc(b, cb);
        try { return func(state, (IntPtr)ha, (IntPtr)hb); }
        finally { NativeMemory.Free(ha); NativeMemory.Free(hb); }
    }

    /// <summary>Encodes <paramref name="value"/> as UTF-8 into <paramref name="buffer"/>, NUL-terminating it.</summary>
    public static int Write(string? value, byte* buffer, int capacity)
    {
        if (string.IsNullOrEmpty(value))
        {
            buffer[0] = 0;
            return 0;
        }
        int written = Encoding.UTF8.GetBytes(value, new Span<byte>(buffer, capacity));
        buffer[written] = 0;
        return written;
    }

    private static byte* Alloc(string? value, int capacity)
    {
        byte* p = (byte*)NativeMemory.Alloc((nuint)capacity + 1);
        Write(value, p, capacity);
        return p;
    }
}
