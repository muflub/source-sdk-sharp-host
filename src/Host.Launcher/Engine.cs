using System.Runtime.InteropServices;

namespace SourceSharp.Host.Launcher;

/// <summary>
/// The engine the launcher runs on its main thread: given a C argv, run until the engine returns and
/// give back its exit code. <see cref="NativeEngine"/> is srcds; facts pass a fake.
/// </summary>
public interface IEngine
{
    /// <summary>Runs the engine on the calling thread. <paramref name="argv"/> is a NULL-terminated char*[] of <paramref name="argc"/> entries.</summary>
    int Run(int argc, nint argv);
}

/// <summary>
/// srcds_linux64's contract (docs/ops.md "The engine image"), in managed code: load
/// <c>bin/linux64/libtier0_srv.so</c>, <c>libvstdlib_srv.so</c>, <c>dedicated_srv.so</c> in that order,
/// look up <c>DedicatedMain</c>, call it with (argc, argv), then free the three in reverse order.
/// No DllImport, extern or unsafe: <see cref="NativeLibrary"/> and a marshalled delegate.
/// <para>The engine's own dlopen calls search <c>LD_LIBRARY_PATH</c>, which the dynamic loader reads
/// once at process start: the entrypoint must set it before exec'ing the launcher; setting it here
/// would change nothing.</para>
/// </summary>
public sealed class NativeEngine(string engineDir, IReadOnlyList<string> libraries, string entryPoint = "DedicatedMain") : IEngine
{
    public static readonly IReadOnlyList<string> SrcdsLibraries =
        ["bin/linux64/libtier0_srv.so", "bin/linux64/libvstdlib_srv.so", "bin/linux64/dedicated_srv.so"];

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int DedicatedMainFn(int argc, nint argv);

    public int Run(int argc, nint argv)
    {
        var handles = new List<nint>();
        try
        {
            foreach (var lib in libraries)
                handles.Add(NativeLibrary.Load(lib.Contains('/') ? Path.Combine(engineDir, lib) : lib));
            var main = NativeLibrary.GetExport(handles[^1], entryPoint);
            var fn = Marshal.GetDelegateForFunctionPointer<DedicatedMainFn>(main);
            return fn(argc, argv);
        }
        finally
        {
            for (var i = handles.Count - 1; i >= 0; i--) NativeLibrary.Free(handles[i]);
        }
    }
}

/// <summary>A C argv in unmanaged memory: argc pointers to NUL-terminated strings, then NULL.</summary>
public sealed class Argv : IDisposable
{
    readonly List<nint> _strings = [];
    public int Count { get; }
    public nint Pointer { get; }

    public Argv(IReadOnlyList<string> args)
    {
        Count = args.Count;
        Pointer = Marshal.AllocHGlobal(nint.Size * (args.Count + 1));
        for (var i = 0; i < args.Count; i++)
        {
            var s = Marshal.StringToHGlobalAnsi(args[i]); // UTF-8 on Unix
            _strings.Add(s);
            Marshal.WriteIntPtr(Pointer, i * nint.Size, s);
        }
        Marshal.WriteIntPtr(Pointer, args.Count * nint.Size, 0);
    }

    /// <summary>Reads a C argv back (what an engine would see).</summary>
    public static IReadOnlyList<string> Read(int argc, nint argv)
    {
        var list = new List<string>(argc);
        for (var i = 0; i < argc; i++)
            list.Add(Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(argv, i * nint.Size)) ?? "");
        return list;
    }

    public void Dispose()
    {
        foreach (var s in _strings) Marshal.FreeHGlobal(s);
        Marshal.FreeHGlobal(Pointer);
    }
}

/// <summary>The engine as /healthz reports it.</summary>
public sealed class LauncherEngineState
{
    readonly Lock _sync = new();
    public string State { get { lock (_sync) return _state; } }
    public int? ExitCode { get { lock (_sync) return _code; } }
    string _state = "not started";
    int? _code;

    public void Running() { lock (_sync) _state = "running"; }
    public void Exited(int code) { lock (_sync) { _state = "exited"; _code = code; } }
    public void Failed(string why) { lock (_sync) { _state = "failed: " + why; } }
}
