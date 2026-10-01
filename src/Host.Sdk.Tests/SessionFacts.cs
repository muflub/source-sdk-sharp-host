using System.Security.Cryptography;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Contracts;
using SourceSharp.Host.Testing;
using P = SourceSharp.Host.Proto;

namespace SourceSharp.Host.Sdk.Tests;

public class SessionFacts
{
    static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    static (RulesModuleFile Module, byte[] Main, byte[] Dep) ModuleOnDisk(int size = 200_000)
    {
        var dir = Directory.CreateTempSubdirectory("sdk-module-").FullName;
        var main = RandomNumberGenerator.GetBytes(size);
        var dep = RandomNumberGenerator.GetBytes(5_000);
        File.WriteAllBytes(Path.Combine(dir, "Fake.Rules.dll"), main);
        File.WriteAllBytes(Path.Combine(dir, "Fake.Rules.Dep.dll"), dep);
        return (new RulesModuleFile("Fake.Rules", "1.2.3", Path.Combine(dir, "Fake.Rules.dll"), HostContract.Version,
            [new RulesModuleDependency("Fake.Rules.Dep", Path.Combine(dir, "Fake.Rules.Dep.dll"))]), main, dep);
    }

    [Fact]
    public async Task The_stream_connects_and_its_heartbeats_are_acknowledged()
    {
        await using var h = await SdkHarness.Start();
        await h.Instance("hub-1", InstanceKind.Hub);
        var sdk = h.Sdk("hub-1");
        await sdk.PumpUntil(() => sdk.Metrics.HeartbeatsAcked >= 2, what: "two acks");
        Assert.True(sdk.Session.Connected);
        Assert.True(h.Streams.IsOpen("hub-1"));
    }

    [Fact]
    public async Task A_main_thread_that_stops_pumping_stops_the_heartbeats_and_resumes_them_when_it_pumps()
    {
        await using var h = await SdkHarness.Start();
        await h.Instance("hub-1", InstanceKind.Hub);
        var o = SdkHarness.FastOptions();
        o.MainThreadStallLimit = TimeSpan.FromMilliseconds(300);
        var sdk = h.Sdk("hub-1", o);
        await sdk.PumpUntil(() => sdk.Metrics.HeartbeatsAcked >= 2, what: "acks while pumping");
        // The game hangs: no Pump.
        await Pumping.Until(() => sdk.Metrics.HeartbeatsWithheld >= 3, what: "heartbeats withheld");
        var sent = sdk.Metrics.HeartbeatsSent;
        await Task.Delay(300);
        Assert.Equal(sent, sdk.Metrics.HeartbeatsSent);
        Assert.True(sdk.Session.MainThreadStalled);
        Assert.True(h.Streams.IsOpen("hub-1")); // a hang is not a closed stream
        await sdk.PumpUntil(() => sdk.Metrics.HeartbeatsSent > sent + 2, what: "heartbeats again");
    }

    [Fact]
    public async Task Heartbeats_carry_the_player_count()
    {
        await using var h = await SdkHarness.Start();
        await h.Instance("hub-1", InstanceKind.Hub);
        var sdk = h.Sdk("hub-1");
        sdk.Session.Players = 3;
        await Pumping.Until(() => h.Data.ReadAsync((tx, _) => tx.Instances.Get("hub-1")).Result?.Players == 3, what: "players = 3 on the row");
    }

    [Fact]
    public async Task A_completion_is_not_delivered_until_Pump()
    {
        await using var h = await SdkHarness.Start();
        await h.Instance("hub-1", InstanceKind.Hub);
        var sdk = h.Sdk("hub-1");
        var call = sdk.Characters.Create("76561198000000001", "scout", "Ann");
        // The service has done the work (the row exists) and still the game has seen nothing.
        await Pumping.Until(() => h.Data.ReadAsync((tx, _) => tx.Characters.List("76561198000000001")).Result.Count == 1, what: "the row");
        await Task.Delay(100);
        Assert.False(call.IsCompleted);
        var r = await sdk.Pumped(call);
        Assert.True(r.Ok);
    }

    [Fact]
    public async Task Pump_delivers_completions_and_commands_only_on_the_pumping_thread()
    {
        await using var h = await SdkHarness.Start();
        await h.Instance("hub-1", InstanceKind.Hub);
        var sdk = h.Sdk("hub-1");
        var seen = new System.Collections.Concurrent.ConcurrentBag<(string What, int Thread)>();
        var done = new ManualResetEventSlim();
        var stop = false;
        var mainId = 0;

        // The game's main thread: no synchronization context, pumps once per "frame".
        var main = new Thread(() =>
        {
            mainId = Environment.CurrentManagedThreadId;
            sdk.Commands.Say += _ => seen.Add(("say", Environment.CurrentManagedThreadId));
            sdk.Session.ConnectionChanged += _ => seen.Add(("connection", Environment.CurrentManagedThreadId));
            async Task Game()
            {
                var created = await sdk.Characters.Create("76561198000000001", "scout", "Ann");
                seen.Add(("create", Environment.CurrentManagedThreadId));
                var lease = await sdk.Characters.Lease(created.Value.Id);
                seen.Add(("lease", Environment.CurrentManagedThreadId));
                await lease.Value.Checkpoint(created.Value.Sheet);
                seen.Add(("checkpoint", Environment.CurrentManagedThreadId));
                done.Set();
            }
            _ = Game();
            while (!Volatile.Read(ref stop)) { sdk.Pump(); Thread.Sleep(2); }
        }) { IsBackground = true };
        main.Start();

        await Pumping.Until(() => h.Streams.IsOpen("hub-1"), what: "the stream");
        await h.Streams.Send("hub-1", new P.ServerCommand { Say = new P.Say { Text = "hello" } });
        Assert.True(done.Wait(Pumping.Timeout));
        await Pumping.Until(() => seen.Any(s => s.What == "say"), what: "the Say event");
        Volatile.Write(ref stop, true);
        main.Join();

        Assert.Equal(["checkpoint", "connection", "create", "lease", "say"], seen.Select(s => s.What).Distinct().Order());
        Assert.All(seen, s => Assert.Equal(mainId, s.Thread));
    }

    [Fact]
    public async Task Booting_announces_the_module_hash_and_uploads_nothing_when_known()
    {
        await using var h = await SdkHarness.Start();
        await h.Instance("lvl-1", InstanceKind.Level, 3, InstanceState.Booting);
        var (module, main, dep) = ModuleOnDisk();
        var o = SdkHarness.FastOptions();
        o.ModuleSource = new FixedModuleSource(module);
        var sdk = h.Sdk("lvl-1", o);
        var r = await sdk.Pumped(sdk.Session.Booting("descent-3-abc"));
        Assert.True(r.Ok, r.ToString());
        var a = Assert.Single(h.Modules.Announced);
        Assert.Equal(("Fake.Rules", "1.2.3", Sha(main), HostContract.Version), (a.Assembly, a.Version, a.Sha256, a.ContractVersion));
        Assert.Equal([("Fake.Rules.Dep", Sha(dep))], a.Deps.Select(d => (d.Assembly, d.Sha256)));
        Assert.Equal((ModuleAnswer.Known, false, 0), (r.Value.Module, r.Value.Uploaded, h.Modules.Uploads));
        Assert.Equal(4321UL, r.Value.InstanceSeed);
        Assert.Same(r.Value, sdk.Session.Boot);
    }

    [Fact]
    public async Task Booting_uploads_every_file_in_chunks_when_the_host_says_send()
    {
        await using var h = await SdkHarness.Start();
        await h.Instance("lvl-1", InstanceKind.Level, 3, InstanceState.Booting);
        h.Modules.Answer = P.ModuleAnswer.Send;
        var (module, main, dep) = ModuleOnDisk();
        var o = SdkHarness.FastOptions();
        o.ModuleSource = new FixedModuleSource(module);
        o.ModuleChunkBytes = 64 * 1024;
        var sdk = h.Sdk("lvl-1", o);
        var r = await sdk.Pumped(sdk.Session.Booting("descent-3-abc"));
        Assert.True(r.Ok, r.ToString());
        Assert.True(r.Value.Uploaded);
        Assert.Equal(1, h.Modules.Uploads);
        Assert.Equal(main, h.Modules.Uploaded[Sha(main)]);
        Assert.Equal(dep, h.Modules.Uploaded[Sha(dep)]);
        Assert.Equal(4, h.Modules.Chunks.Count(c => c.Sha256 == Sha(main))); // 200 000 bytes in 64 KiB chunks
        Assert.Equal(2, h.Modules.Chunks.Count(c => c.Last));
    }

    [Fact]
    public void The_default_module_source_finds_the_loaded_assembly_marked_as_the_rules_module()
    {
        var m = new LoadedAssemblyModuleSource().Find();
        Assert.NotNull(m);
        // Host.FakeRules (the fake game's module) is loaded in this process through Host.Testing.
        _ = typeof(FakeGameRules);
        Assert.Equal(typeof(FakeGameRules).Assembly.Location, m.Path);
        Assert.Equal("SourceSharp.Host.FakeRules", m.Assembly);
        Assert.DoesNotContain(m.Deps, d => d.Assembly == typeof(HostContract).Assembly.GetName().Name);
    }

    [Fact]
    public async Task MapReady_answers_the_expected_port()
    {
        await using var h = await SdkHarness.Start();
        await h.Instance("lvl-1", InstanceKind.Level, 3, InstanceState.Booting);
        var sdk = h.Sdk("lvl-1");
        var r = await sdk.Pumped(sdk.Session.MapReady(27015, "descent-3-abc"));
        Assert.True(r.Ok, r.ToString());
        Assert.Equal(27015, r.Value);
    }

    [Fact]
    public async Task MapReady_is_not_reported_before_the_stream_is_up()
    {
        await using var h = await SdkHarness.Start();
        await h.Instance("lvl-1", InstanceKind.Level, 3, InstanceState.Booting);
        var o = SdkHarness.FastOptions();
        o.StreamWaitBeforeMapReady = TimeSpan.FromMilliseconds(500);
        o.ReconnectBackoff = TimeSpan.FromSeconds(30); // the first attempt fails and the next is far away
        h.Proxy.Block();
        var sdk = h.Sdk("lvl-1", o);
        await Pumping.Until(() => sdk.Metrics.ConnectFailures >= 1, what: "the first attempt failed");
        h.Proxy.Unblock(); // unary calls would get through now; the stream is still backing off
        var r = await sdk.Pumped(sdk.Session.MapReady(27015, "descent-3-abc"));
        Assert.Equal((HostError.Unreachable, "stream_not_connected"), (r.Error, r.Refusal!.Reason));
        Assert.Equal(0, await h.Data.ReadAsync((tx, _) => tx.Audit.Count(new AuditQuery(Action: "instance.map_ready", Target: "lvl-1"))));
    }

    [Fact]
    public async Task MapReady_on_another_port_is_a_typed_port_mismatch()
    {
        await using var h = await SdkHarness.Start();
        await h.Instance("lvl-1", InstanceKind.Level, 3, InstanceState.Booting);
        var sdk = h.Sdk("lvl-1");
        var r = await sdk.Pumped(sdk.Session.MapReady(27016, "descent-3-abc"));
        Assert.Equal(HostError.PortMismatch, r.Error);
    }

    [Fact]
    public async Task PlayerJoined_returns_the_real_address_and_session()
    {
        await using var h = await SdkHarness.Start();
        await h.Instance("hub-1", InstanceKind.Hub);
        var sdk = h.Sdk("hub-1");
        var r = await sdk.Pumped(sdk.Session.PlayerJoined("127.0.4.2:27005", "76561198000000001"));
        Assert.Equal(new PlayerJoin(true, "", "sess-76561198000000001", "203.0.113.7:27005"), r.Value);
        Assert.Equal([("127.0.4.2:27005", "76561198000000001")], h.Sessions.Joined);
    }

    [Fact]
    public async Task PlayerLeft_reaches_the_service()
    {
        await using var h = await SdkHarness.Start();
        await h.Instance("hub-1", InstanceKind.Hub);
        var sdk = h.Sdk("hub-1");
        var r = await sdk.Pumped(sdk.Session.PlayerLeft("76561198000000001", "127.0.4.2:27005"));
        Assert.True(r.Ok, r.ToString());
        Assert.Equal([("127.0.4.2:27005", "76561198000000001")], h.Sessions.Left);
    }

    [Fact]
    public async Task Log_lines_arrive_batched_at_the_service()
    {
        await using var h = await SdkHarness.Start();
        await h.Instance("hub-1", InstanceKind.Hub);
        var sdk = h.Sdk("hub-1");
        for (var i = 0; i < 10; i++) sdk.Session.Log("info", $"line {i}");
        await Pumping.Until(() => h.Chatter.Tail("hub-1", 100).Count == 10, what: "ten lines");
        Assert.EndsWith("info line 9", h.Chatter.Tail("hub-1", 1)[0]);
    }

    [Fact]
    public async Task Log_lines_beyond_the_buffer_are_dropped_and_the_count_is_sent()
    {
        await using var h = await SdkHarness.Start();
        await h.Instance("hub-1", InstanceKind.Hub);
        var o = SdkHarness.FastOptions();
        o.LogCapacity = 5;
        o.LogFlushInterval = TimeSpan.FromSeconds(1);
        var sdk = h.Sdk("hub-1", o);
        for (var i = 0; i < 20; i++) sdk.Session.Log("info", $"line {i}");
        Assert.Equal(15, sdk.Metrics.DroppedLogLines);
        await Pumping.Until(() => h.Chatter.Tail("hub-1", 100).Any(l => l.Contains("dropped 15 lines")), what: "the dropped count");
        Assert.Equal(6, h.Chatter.Tail("hub-1", 100).Count); // five lines and the note
    }

    [Fact]
    public async Task The_first_connect_retries_with_backoff_until_the_host_is_reachable()
    {
        await using var h = await SdkHarness.Start();
        await h.Instance("hub-1", InstanceKind.Hub);
        h.Proxy.Block(); // a new pod's first ~2 s behind NetworkPolicy
        var sdk = h.Sdk("hub-1");
        await Pumping.Until(() => sdk.Metrics.ConnectFailures >= 2, what: "two failed attempts");
        Assert.False(sdk.Session.Connected);
        h.Proxy.Unblock();
        await sdk.PumpUntil(() => sdk.Session.Connected, what: "connected after the block");
        Assert.Equal(0, sdk.Metrics.Reconnects);
    }

    [Fact]
    public async Task A_cut_stream_is_reopened()
    {
        await using var h = await SdkHarness.Start();
        await h.Instance("hub-1", InstanceKind.Hub);
        var sdk = h.Sdk("hub-1");
        var down = 0;
        sdk.Session.ConnectionChanged += up => { if (!up) down++; };
        await sdk.PumpUntil(() => sdk.Session.Connected, what: "connected");
        var accepted = h.Proxy.Accepted;
        h.Proxy.Cut();
        // The service today crashes an instance whose stream closes while it runs (§6.1), so the
        // reopened stream is refused; the SDK's part is to try again, which reaches the service.
        await sdk.PumpUntil(() => down == 1 && h.Proxy.Accepted > accepted && sdk.Metrics.ConnectFailures > 0, what: "a new stream attempt");
        Assert.False(sdk.Session.Connected);
    }

    [Fact]
    public async Task An_unreachable_host_is_a_typed_refusal_after_the_retries()
    {
        await using var h = await SdkHarness.Start();
        await h.Instance("hub-1", InstanceKind.Hub);
        h.Proxy.Block();
        var sdk = h.Sdk("hub-1");
        var r = await sdk.Pumped(sdk.Characters.Create("76561198000000001", "scout", "Ann"));
        Assert.Equal(HostError.Unreachable, r.Error);
        Assert.True(h.Proxy.Refused > 0);
        Assert.Empty(await h.Data.ReadAsync((tx, _) => tx.Characters.List("76561198000000001")));
    }

    [Fact]
    public async Task A_retried_call_carries_the_same_request_id()
    {
        await using var h = await SdkHarness.Start();
        await h.Instance("hub-1", InstanceKind.Hub);
        h.Proxy.Block();
        var o = SdkHarness.FastOptions();
        o.RpcAttempts = 40;
        var sdk = h.Sdk("hub-1", o);
        var call = sdk.Characters.Create("76561198000000001", "scout", "Ann");
        await Pumping.Until(() => sdk.Metrics.RpcRetries >= 2, what: "retries");
        h.Proxy.Unblock();
        var r = await sdk.Pumped(call);
        Assert.True(r.Ok, r.ToString());
        Assert.Single(await h.Data.ReadAsync((tx, _) => tx.Characters.List("76561198000000001")));
    }

    [Fact]
    public void A_status_with_a_reason_trailer_maps_to_its_typed_error()
    {
        var e = new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.Unimplemented, "sdk_version: this host speaks SDK 1.x, the pod sent 9.0.0"));
        Assert.Equal(HostError.SdkVersion, HostRefusal.FromRpc(e).Error);
        var t = new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.FailedPrecondition, "whatever"), new Grpc.Core.Metadata { { "x-reason", "stale_lease" } });
        Assert.Equal(HostError.StaleLease, HostRefusal.FromRpc(t).Error);
    }

    [Fact]
    public void A_business_refusal_is_never_retried_and_a_transport_failure_is()
    {
        Assert.False(HostRefusal.Transient(new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.FailedPrecondition, "stale_lease: x"))));
        Assert.True(HostRefusal.Transient(new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.Unavailable, "down"))));
    }
}
