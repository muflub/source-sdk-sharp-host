using System.Runtime.InteropServices;

namespace SourceSharp.Host.Launcher;

/// <summary>
/// Blocks a signal on the calling thread (libc <c>pthread_sigmask</c>, reached through
/// <see cref="NativeLibrary"/> — no DllImport). The launcher blocks SIGTERM on the main thread before
/// the engine starts: a process-directed SIGTERM is then delivered to one of the runtime's threads,
/// where the launcher's handler cancels it, instead of interrupting an engine syscall with EINTR.
/// Threads the engine creates afterwards inherit the mask.
/// </summary>
public static class SignalMask
{
    public const int SIGTERM = 15;
    const int SIG_BLOCK = 0;
    const int SigsetBytes = 128; // glibc sigset_t

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int SigemptysetFn(nint set);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int SigaddsetFn(nint set, int signo);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int SigismemberFn(nint set, int signo);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int PthreadSigmaskFn(int how, nint set, nint old);

    static readonly Lazy<nint> Libc = new(() => NativeLibrary.Load("libc.so.6"));

    static T Fn<T>(string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(Libc.Value, name));

    /// <summary>Blocks <paramref name="signo"/> on this thread; true on success.</summary>
    public static bool Block(int signo)
    {
        var set = Marshal.AllocHGlobal(SigsetBytes);
        try
        {
            Fn<SigemptysetFn>("sigemptyset")(set);
            Fn<SigaddsetFn>("sigaddset")(set, signo);
            return Fn<PthreadSigmaskFn>("pthread_sigmask")(SIG_BLOCK, set, 0) == 0;
        }
        finally { Marshal.FreeHGlobal(set); }
    }

    /// <summary>Whether <paramref name="signo"/> is blocked on this thread.</summary>
    public static bool IsBlocked(int signo)
    {
        var old = Marshal.AllocHGlobal(SigsetBytes);
        try
        {
            if (Fn<PthreadSigmaskFn>("pthread_sigmask")(SIG_BLOCK, 0, old) != 0) return false;
            return Fn<SigismemberFn>("sigismember")(old, signo) == 1;
        }
        finally { Marshal.FreeHGlobal(old); }
    }
}
