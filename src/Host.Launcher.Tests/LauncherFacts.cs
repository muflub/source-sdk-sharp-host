using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Logging.Abstractions;
using SourceSharp.Host.Relay;

namespace SourceSharp.Host.Launcher.Tests;

/// <summary>A fake engine: runs a delegate with the C argv it was given.</summary>
sealed class FakeEngine(Func<int, nint, int> run) : IEngine
{
    public int Run(int argc, nint argv) => run(argc, argv);
}

/// <summary>Host.Launcher with a fake engine (plan D-H11).</summary>
public class LauncherFacts
{
    static readonly LauncherOptions NoChdir = new() { ChangeDirectory = false, BlockSigtermOnEngineThread = false };

    /// <summary>Relay listeners on free loopback ports; /healthz answers on any of them (no port restriction for port 0).</summary>
    static RelayOptions Loopback() => new()
    {
        Listen = "127.0.0.1:0", InfoListen = "127.0.0.1:0", Health = "127.0.0.1:0",
        EngineEndpoint = "127.0.0.1:9", PeerPort = 0,
    };

    static async Task<JsonObject> Health(string address)
    {
        using var http = new HttpClient { DefaultRequestVersion = HttpVersion.Version20, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact };
        return JsonNode.Parse(await http.GetStringAsync($"{address}/healthz"))!.AsObject();
    }

    static string FirstAddress(WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

    [Fact]
    public void The_engines_exit_code_is_the_launchers()
    {
        var code = LauncherApp.Run(NoChdir, Loopback(), "launcher", [], new FakeEngine((_, _) => 7), new LauncherEngineState());
        Assert.Equal(7, code);
    }

    [Fact]
    public void The_engine_gets_argv0_then_the_args_as_a_C_argv()
    {
        IReadOnlyList<string>? seen = null;
        nint terminator = -1;
        LauncherApp.Run(NoChdir, Loopback(), "/opt/descent/launcher", ["-game", "hl2mp", "+map", "dm_lockdown", "+hostname", "Dëscent"],
            new FakeEngine((argc, argv) =>
            {
                seen = Argv.Read(argc, argv);
                terminator = Marshal.ReadIntPtr(argv, argc * nint.Size);
                return 0;
            }), new LauncherEngineState());
        Assert.Equal(["/opt/descent/launcher", "-game", "hl2mp", "+map", "dm_lockdown", "+hostname", "Dëscent"], seen);
        Assert.Equal(0, terminator);
    }

    [Fact]
    public void The_relay_is_up_and_reports_the_engine_running_before_the_engine_starts_its_work()
    {
        string? address = null;
        JsonObject? during = null;
        LauncherApp.Run(NoChdir, Loopback(), "launcher", [], new FakeEngine((_, _) =>
        {
            during = Health(address!).GetAwaiter().GetResult();
            return 0;
        }), new LauncherEngineState(), started: app => address = FirstAddress(app));
        Assert.Equal((true, "running"), (during!["relay"]!["up"]!.GetValue<bool>(), during["engine"]!["state"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Health_says_not_started_before_the_engine_runs()
    {
        var app = LauncherApp.BuildRelay(Loopback(), new LauncherEngineState(), null);
        await app.StartAsync();
        try { Assert.Equal("not started", (await Health(FirstAddress(app)))["engine"]!["state"]!.GetValue<string>()); }
        finally { await app.StopAsync(); await app.DisposeAsync(); }
    }

    [Fact]
    public void After_the_engine_returns_the_state_is_exited_with_its_code()
    {
        var state = new LauncherEngineState();
        LauncherApp.Run(NoChdir, Loopback(), "launcher", [], new FakeEngine((_, _) => 3), state);
        Assert.Equal(("exited", (int?)3), (state.State, state.ExitCode));
    }

    [Fact]
    public void The_relay_is_stopped_when_the_engine_returns()
    {
        string? address = null;
        LauncherApp.Run(NoChdir, Loopback(), "launcher", [], new FakeEngine((_, _) => 0), new LauncherEngineState(), started: app => address = FirstAddress(app));
        Assert.ThrowsAny<HttpRequestException>(() => Health(address!).GetAwaiter().GetResult());
    }

    [Fact]
    public void An_engine_that_cannot_load_fails_with_70_and_says_why()
    {
        var state = new LauncherEngineState();
        var code = LauncherApp.Run(NoChdir, Loopback(), "launcher", [],
            new NativeEngine("/nonexistent", NativeEngine.SrcdsLibraries), state);
        Assert.Equal(70, code);
        Assert.StartsWith("failed: ", state.State);
    }

    [Fact]
    public void Sigterm_is_cancelled_so_the_process_does_not_stop_under_the_engine()
    {
        var ctx = new PosixSignalContext(PosixSignal.SIGTERM);
        var state = new LauncherEngineState();
        state.Running();
        LauncherApp.OnSigterm(ctx, state, NullLogger.Instance);
        Assert.True(ctx.Cancel);
        Assert.Equal("running", state.State);
    }

    [Fact]
    public void The_native_engine_calls_the_entry_point_with_argc()
    {
        // libc's abs(int) stands in for DedicatedMain(int argc, char** argv): it returns its first argument.
        using var argv = new Argv(["a", "b", "c"]);
        Assert.Equal(3, new NativeEngine("/", ["libc.so.6"], "abs").Run(argv.Count, argv.Pointer));
    }

    [Fact]
    public void Sigterm_can_be_blocked_on_the_engine_thread()
    {
        bool? before = null, ok = null, after = null;
        var t = new Thread(() =>
        {
            before = SignalMask.IsBlocked(SignalMask.SIGTERM);
            ok = SignalMask.Block(SignalMask.SIGTERM);
            after = SignalMask.IsBlocked(SignalMask.SIGTERM);
        });
        t.Start();
        t.Join();
        Assert.Equal((false, true, true), (before, ok, after));
    }

    [Fact]
    public void Argv0_names_srcds_in_the_engine_dir_so_the_engine_finds_its_base_directory()
    {
        Assert.Equal("/opt/srcds/srcds_linux64", new LauncherOptions { EngineDir = "/opt/srcds" }.EngineArgv0);
    }
}
