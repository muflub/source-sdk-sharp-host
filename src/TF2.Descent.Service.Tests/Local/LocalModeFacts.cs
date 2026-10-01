using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Instances;
using SourceSharp.Host.Local;
using P = SourceSharp.Host.Proto;

namespace TF2.Descent.Service.Tests.Local;

/// <summary>D-H13: the SDK's local mode is the real service, out of process or in the game's.</summary>
public class LocalModeFacts
{
    static string TempDir() => Path.Combine(Path.GetTempPath(), $"sourcesharp-local-{Guid.NewGuid():N}");

    static PodSpec Pod(string kind, string id, params string[] args) => new($"tf2_descent-{kind}-{id}",
        new Dictionary<string, string> { [GamePodLabels.Kind] = kind, [GamePodLabels.Instance] = id }, [],
        args.Length == 0 ? [] : [new ContainerSpec("game", "engine", [], args, [], [], [])], [], new Dictionary<string, string>(), 60,
        new SecretSpec($"tf2_descent-{id}", new Dictionary<string, string> { [GamePodBuilder.SecretKey] = "tok-" + id }));

    static LocalConnection Read(string file) => JsonSerializer.Deserialize<LocalConnection>(File.ReadAllText(file), LocalInstanceHost.Json)!;

    /// <summary>A local pool with level <paramref name="map"/>'s files in it.</summary>
    static string Levels(string dir, string map)
    {
        var levels = Path.Combine(dir, "maps", "levels");
        Directory.CreateDirectory(levels);
        foreach (var e in LocalInstanceHost.LevelExtensions) File.WriteAllText(Path.Combine(levels, map + e), e);
        return levels;
    }

    [Fact]
    public async Task The_hub_file_carries_its_map_and_port_and_no_level_files()
    {
        var dir = TempDir();
        try
        {
            var host = new LocalInstanceHost(dir, () => "http://127.0.0.1:5001", Levels(dir, "tf2_descent-1-abc"), _ => true);
            await host.Create(Pod("hub", "h1", "-port", "27015", "+map", "tf2_descent_town"));
            var c = Read(Path.Combine(dir, "local-host.json"));
            Assert.Equal(("tf2_descent_town", 27015), (c.Map, c.Port));
            Assert.Null(c.LevelFiles);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task A_level_file_carries_the_levels_files_as_absolute_paths()
    {
        var dir = TempDir();
        try
        {
            var levels = Levels(dir, "tf2_descent-1-abc");
            var host = new LocalInstanceHost(dir, () => "http://127.0.0.1:5001", levels, _ => true);
            await host.Create(Pod("level", "l1", "-port", "27016", "+map", "tf2_descent-1-abc"));
            var c = Read(Path.Combine(dir, "local-level-l1.json"));
            Assert.Equal("tf2_descent-1-abc", c.Map);
            Assert.Equal(new[] { ".bsp", ".nav3d", ".map2d" }.Select(e => Path.Combine(levels, "tf2_descent-1-abc" + e)), c.LevelFiles!);
            Assert.All(c.LevelFiles!, f => Assert.True(Path.IsPathRooted(f) && File.Exists(f), f));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task A_level_whose_bsp_is_not_in_the_local_pool_is_refused()
    {
        var dir = TempDir();
        try
        {
            var host = new LocalInstanceHost(dir, () => "http://127.0.0.1:5001", Levels(dir, "tf2_descent-1-abc"), _ => true);
            await host.Create(Pod("level", "ok", "+map", "tf2_descent-1-abc")); // the positive arm: a pooled level is accepted
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.Create(Pod("level", "l2", "+map", "tf2_descent-2-zzz")));
            Assert.False(File.Exists(Path.Combine(dir, "local-level-l2.json")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_hub_keeps_the_configured_port()
    {
        var host = new LocalInstanceHost(TempDir(), () => "", portFree: _ => true);
        Assert.Equal(27015, host.GamePort(InstanceKind.Hub, 27015));
    }

    [Fact]
    public void Each_level_gets_its_own_port_above_the_hubs()
    {
        var host = new LocalInstanceHost(TempDir(), () => "", portFree: _ => true);
        var ports = new[] { host.GamePort(InstanceKind.Level, 27015), host.GamePort(InstanceKind.Level, 27015) };
        Assert.Equal([27016, 27017], ports);
    }

    [Fact]
    public void Same_process_levels_share_the_hubs_port()
    {
        var host = new LocalInstanceHost(TempDir(), () => "", portFree: _ => true, sameProcess: true);
        Assert.Equal((27015, 27015), (host.GamePort(InstanceKind.Hub, 27015), host.GamePort(InstanceKind.Level, 27015)));
    }

    [Fact]
    public async Task The_connection_record_is_what_the_file_holds_until_the_pod_is_deleted()
    {
        var dir = TempDir();
        try
        {
            var host = new LocalInstanceHost(dir, () => "http://127.0.0.1:5001", Levels(dir, "tf2_descent-1-abc"), _ => true);
            Assert.Null(host.For("l1"));
            var pod = await host.Create(Pod("level", "l1", "-port", "27016", "+map", "tf2_descent-1-abc"));
            Assert.Equal(File.ReadAllText(Path.Combine(dir, "local-level-l1.json")), host.For("l1"));
            await host.Delete(pod.Name, pod.Uid, TimeSpan.Zero);
            Assert.Null(host.For("l1"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_port_something_else_bound_is_skipped()
    {
        var host = new LocalInstanceHost(TempDir(), () => "", portFree: p => p != 27016);
        Assert.Equal(27017, host.GamePort(InstanceKind.Level, 27015));
    }

    [Fact]
    public async Task Deleting_a_level_releases_its_port()
    {
        var dir = TempDir();
        try
        {
            var host = new LocalInstanceHost(dir, () => "", Levels(dir, "tf2_descent-1-abc"), _ => true);
            var port = host.GamePort(InstanceKind.Level, 27015);
            var pod = await host.Create(Pod("level", "l1", "-port", port.ToString(), "+map", "tf2_descent-1-abc"));
            Assert.Equal(27017, host.GamePort(InstanceKind.Level, 27015)); // held while the pod lives
            Assert.True(await host.Delete(pod.Name, pod.Uid, TimeSpan.Zero));
            Assert.Equal(port, host.GamePort(InstanceKind.Level, 27015));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task The_sdk_reads_every_field_the_local_host_writes()
    {
        var dir = TempDir();
        try
        {
            var levels = Levels(dir, "tf2_descent-1-abc");
            var host = new LocalInstanceHost(dir, () => "http://127.0.0.1:5001", levels, _ => true);
            await host.Create(Pod("level", "l1", "-port", "27016", "+map", "tf2_descent-1-abc"));
            var written = Read(Path.Combine(dir, "local-level-l1.json"));
            var read = SourceSharp.Host.Sdk.HostLocalFile.Parse(File.ReadAllText(Path.Combine(dir, "local-level-l1.json")), "local-level-l1.json");
            Assert.Equal((written.Service, written.InstanceId, written.Token, written.Map, written.Port), (read.Service, read.InstanceId, read.Token, read.Map, read.Port));
            Assert.Equal(written.LevelFiles!, read.LevelFiles!);
            Assert.Equal(3, read.LevelFiles!.Count);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Creating_the_hub_pod_writes_the_connection_file()
    {
        var dir = TempDir();
        try
        {
            var host = new LocalInstanceHost(dir, () => "http://127.0.0.1:5001");
            await host.Create(Pod("hub", "h1"));
            var c = JsonSerializer.Deserialize<LocalConnection>(await File.ReadAllTextAsync(Path.Combine(dir, "local-host.json")), LocalInstanceHost.Json)!;
            Assert.Equal(("http://127.0.0.1:5001", "h1", "tok-h1"), (c.Service, c.InstanceId, c.Token));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task A_level_pod_gets_its_own_file_and_deleting_it_removes_the_file()
    {
        var dir = TempDir();
        try
        {
            var host = new LocalInstanceHost(dir, () => "http://127.0.0.1:5001");
            var pod = await host.Create(Pod("level", "l7"));
            var file = Path.Combine(dir, "local-level-l7.json");
            Assert.True(File.Exists(file));
            Assert.True(await host.Delete(pod.Name, pod.Uid, TimeSpan.Zero));
            Assert.False(File.Exists(file));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task A_bake_job_is_refused_in_local_mode()
    {
        var host = new LocalInstanceHost(TempDir(), () => "");
        var e = await Assert.ThrowsAsync<HostRefusal>(() => host.RunJob(new JobSpec("bake", new Dictionary<string, string>(), new ContainerSpec("c", "i", [], [], [], [], []), [])));
        Assert.Equal("local_mode", e.Reason);
    }

    [Fact]
    public void The_local_flag_expands_to_the_profile()
    {
        var args = LocalProfile.Expand(["--local", "/tmp/x"]);
        Assert.Contains("--Instances:Host=Local", args);
        Assert.Contains("--Data:Path=/tmp/x/host.db", args);
        Assert.Contains("--Listen:GatewayControl=", args);
        Assert.DoesNotContain("/tmp/x", args.Where(a => !a.StartsWith("--")));
    }

    [Fact]
    public async Task The_in_process_host_starts_and_its_connection_record_authenticates_and_boots_the_hub()
    {
        var dir = TempDir();
        try
        {
            var json = await LocalHost.StartAsync(dir, CancellationToken.None);
            var c = JsonSerializer.Deserialize<LocalConnection>(json, LocalInstanceHost.Json)!;
            using var channel = GrpcChannel.ForAddress(c.Service);
            var auth = new Metadata { { "x-instance-id", c.InstanceId }, { "x-instance-token", c.Token } };
            var instances = new P.InstanceService.InstanceServiceClient(channel);
            var boot = await instances.BootingAsync(new P.BootingRequest
            {
                RequestId = "b1", SdkVersion = "2.0.0",
                Module = new P.RulesModule { Assembly = "x", Version = "1", Sha256 = new string('a', 64), ContractVersion = SourceSharp.Host.Contracts.HostContract.Version },
            }, auth);
            Assert.Equal("hub", boot.InstanceKind);
            var ready = await instances.MapReadyAsync(new P.MapReadyRequest { RequestId = "m1", Port = 27015, Map = "tf2_descent_town" }, auth);
            Assert.Equal(27015, ready.ExpectedPort); // the hub keeps the configured port: a hand-started game binds it
        }
        finally
        {
            await LocalHost.StopAsync();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
