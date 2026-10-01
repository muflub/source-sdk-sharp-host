using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using SourceSharp.Host.Relay;

namespace SourceSharp.Host.Launcher;

/// <summary>The launcher's own configuration (LAUNCHER_* environment); the relay's is RELAY_*.</summary>
public sealed class LauncherOptions
{
    /// <summary>Where 244310 and the overlay are installed; the engine runs with this as its working directory.</summary>
    public string EngineDir { get; set; } = "/opt/srcds";
    /// <summary>chdir to <see cref="EngineDir"/> before the engine starts (facts turn it off).</summary>
    public bool ChangeDirectory { get; set; } = true;
    /// <summary>With <c>-wait_for_debugger</c> among the engine args: how long to wait for a tracer (srcds waits 30 s).</summary>
    public TimeSpan DebuggerWait { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>The libraries loaded in order, relative to <see cref="EngineDir"/> (or bare sonames); the last
    /// exports <see cref="EntryPoint"/>. Empty = srcds's three (the binder appends to a non-empty default).</summary>
    public List<string> Libraries { get; set; } = [];
    public string EntryPoint { get; set; } = "DedicatedMain";
    /// <summary>Block SIGTERM on the thread that runs the engine (<see cref="SignalMask"/>); facts on a
    /// thread-pool thread turn it off.</summary>
    public bool BlockSigtermOnEngineThread { get; set; } = true;
    /// <summary>
    /// argv[0] for the engine. The dedicated server takes its base directory — where it looks for
    /// <c>-game</c>'s gameinfo.txt — from the directory of argv[0], so it must name a file in <see cref="EngineDir"/>, as
    /// srcds_linux64's own path would; the file need not exist. Empty = <c>EngineDir/srcds_linux64</c>.
    /// </summary>
    public string Argv0 { get; set; } = "";

    public string EngineArgv0 => string.IsNullOrEmpty(Argv0) ? Path.Combine(EngineDir, "srcds_linux64") : Argv0;
}

/// <summary>
/// Host.Launcher (plan D-H11): one process per game pod. The relay (PeerRelay, PeerInfo, health) runs
/// on background threads; the engine runs on the calling (main) thread until it returns; then the
/// relay stops and the engine's exit code is the process's.
///
/// <para><b>SIGTERM</b> is caught and cancelled: .NET's default would start a shutdown that tears the
/// process down under a running engine. The clean stop is the service's <c>Shutdown</c> command down
/// the SDK stream (the engine exits, <c>DedicatedMain</c> returns); the kubelet's SIGKILL at the end of
/// the grace period is the backstop. The host's own lifetime is a no-op for the same reason.</para>
/// </summary>
public static class LauncherApp
{
    /// <summary>Runs the relay, then the engine; returns the engine's exit code.</summary>
    public static int Run(LauncherOptions lo, RelayOptions ro, string argv0, IReadOnlyList<string> engineArgs,
        IEngine engine, LauncherEngineState state, ILoggerFactory? logs = null, Action<WebApplication>? started = null)
    {
        var log = (logs ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance).CreateLogger("Launcher");
        var app = BuildRelay(ro, state, logs);
        app.StartAsync().GetAwaiter().GetResult();
        log.LogInformation("relay up: PeerRelay {Listen}, PeerInfo {Info}, health {Health}", ro.Listen, ro.InfoListen, ro.Health);
        started?.Invoke(app);
        if (ro.Interpose)
        {
            // The engine's game socket reads and writes through the relay with real client addresses.
            InterposeExports.Current = new SocketInterposer(LibcSocketCalls.Instance,
                app.Services.GetRequiredService<InterposeSwitchboard>(), System.Net.IPEndPoint.Parse(ro.EngineEndpoint).Port);
            log.LogInformation("relay mode Interpose: recvfrom/sendto on the game port {Port} go through the relay", System.Net.IPEndPoint.Parse(ro.EngineEndpoint).Port);
        }
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => OnSigterm(ctx, state, log));
        int code;
        try
        {
            if (lo.ChangeDirectory) Directory.SetCurrentDirectory(lo.EngineDir);
            engineArgs = EngineArgs(ro, engineArgs);
            if (engineArgs.Contains("-wait_for_debugger")) WaitForDebugger(lo.DebuggerWait, log);
            using var argv = new Argv([argv0, .. engineArgs]);
            if (lo.BlockSigtermOnEngineThread && !SignalMask.Block(SignalMask.SIGTERM))
                log.LogWarning("could not block SIGTERM on the engine thread");
            state.Running();
            log.LogInformation("engine starting in {Dir}: {Args}", lo.EngineDir, string.Join(' ', engineArgs));
            code = engine.Run(argv.Count, argv.Pointer);
            state.Exited(code);
            log.LogInformation("engine exited with {Code}", code);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or IOException or UnauthorizedAccessException)
        {
            state.Failed(e.Message);
            log.LogCritical("engine could not start: {Error}", e.Message);
            code = 70; // EX_SOFTWARE
        }
        finally
        {
            InterposeExports.Current = null;
            app.StopAsync().GetAwaiter().GetResult();
            app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        return code;
    }

    /// <summary>
    /// The engine's arguments for the relay mode. Interpose adds <c>+net_usesocketsforloopback 1</c>
    /// unless given: the engine sends to 127.0.0.1 through its in-process loopback buffers instead of
    /// its socket unless <c>net_usesocketsforloopback</c> is set (observed, docs/net-protocol.md §12.1), so
    /// replies to a client whose real address is 127.0.0.1 never reach the interposed sendto
    /// (docs/net-protocol.md §12). Other addresses are unaffected by the convar.
    /// </summary>
    public static IReadOnlyList<string> EngineArgs(RelayOptions ro, IReadOnlyList<string> args) =>
        ro.Interpose && !args.Contains("+net_usesocketsforloopback") ? [.. args, "+net_usesocketsforloopback", "1"] : args;

    /// <summary>The relay, with the engine's state in /healthz, and no host lifetime reacting to signals.</summary>
    public static WebApplication BuildRelay(RelayOptions ro, LauncherEngineState state, ILoggerFactory? logs)
    {
        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        if (logs is not null) b.Services.AddSingleton(logs);
        b.Services.AddSingleton<IHostLifetime, InertLifetime>();
        b.Services.Configure<HostOptions>(h => h.ShutdownTimeout = TimeSpan.FromSeconds(5));
        b.Services.AddPeerRelay(ro);
        b.WebHost.ConfigureKestrel(k => k.ListenRelay(ro));
        var app = b.Build();
        app.MapPeerRelay(ro, j =>
        {
            var engine = new JsonObject { ["state"] = state.State };
            if (state.ExitCode is { } c) engine["exitCode"] = c;
            j["engine"] = engine;
            if (InterposeExports.Current is { } i)
                j["interpose"] = new JsonObject { ["gameSocketSeen"] = i.GameSocketSeen, ["served"] = i.Served, ["sent"] = i.Sent, ["fellThrough"] = i.FellThrough };
            if (state.State.StartsWith("failed", StringComparison.Ordinal)) j["ok"] = false;
        });
        return app;
    }

    /// <summary>SIGTERM: logged and cancelled, so the engine keeps running (see the class remarks).</summary>
    public static void OnSigterm(PosixSignalContext ctx, LauncherEngineState state, ILogger log)
    {
        ctx.Cancel = true;
        log.LogWarning("SIGTERM received with the engine {State}: ignored; the clean stop is the service's Shutdown command, SIGKILL the backstop", state.State);
    }

    static void WaitForDebugger(TimeSpan wait, ILogger log)
    {
        log.LogInformation("-wait_for_debugger: waiting up to {Wait} for a tracer (pid {Pid})", wait, Environment.ProcessId);
        var until = DateTime.UtcNow + wait;
        while (DateTime.UtcNow < until)
        {
            var tracer = File.ReadLines("/proc/self/status").FirstOrDefault(l => l.StartsWith("TracerPid:", StringComparison.Ordinal));
            if (tracer is not null && tracer.Split(':')[1].Trim() != "0") return;
            Thread.Sleep(100);
        }
    }

    /// <summary>A host lifetime that does nothing: no Ctrl+C / SIGTERM handling by the host.</summary>
    sealed class InertLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

public static class Program
{
    public static int Main(string[] args)
    {
        var config = new ConfigurationBuilder().AddEnvironmentVariables("LAUNCHER_").Build();
        var lo = new LauncherOptions();
        config.Bind(lo);
        var ro = new RelayOptions();
        new ConfigurationBuilder().AddEnvironmentVariables("RELAY_").Build().Bind(ro);
        using var logs = LoggerFactory.Create(l => l.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; }));
        var engine = new NativeEngine(lo.EngineDir, lo.Libraries.Count > 0 ? lo.Libraries : NativeEngine.SrcdsLibraries, lo.EntryPoint);
        return LauncherApp.Run(lo, ro, lo.EngineArgv0, args, engine, new LauncherEngineState(), logs);
    }
}
