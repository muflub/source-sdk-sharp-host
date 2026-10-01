using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Instances;
using SourceSharp.Host.Local;
using P = SourceSharp.Host.Proto;

namespace Descent.Service.Tests.Local;

/// <summary>D-H13: the SDK's local mode is the real service, out of process or in the game's.</summary>
public class LocalModeFacts
{
    static string TempDir() => Path.Combine(Path.GetTempPath(), $"descent-local-{Guid.NewGuid():N}");

    static PodSpec Pod(string kind, string id) => new($"descent-{kind}-{id}",
        new Dictionary<string, string> { [GamePodLabels.Kind] = kind, [GamePodLabels.Instance] = id }, [], [], [], new Dictionary<string, string>(), 60,
        new SecretSpec($"descent-{id}", new Dictionary<string, string> { [GamePodBuilder.SecretKey] = "tok-" + id }));

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
                RequestId = "b1", SdkVersion = "1.0.0",
                Module = new P.RulesModule { Assembly = "x", Version = "1", Sha256 = new string('a', 64), ContractVersion = SourceSharp.Host.Contracts.HostContract.Version },
            }, auth);
            Assert.Equal("hub", boot.InstanceKind);
            var ready = await instances.MapReadyAsync(new P.MapReadyRequest { RequestId = "m1", Port = 27015, Map = "descent_town" }, auth);
            Assert.Equal(27015, ready.ExpectedPort);
        }
        finally
        {
            await LocalHost.StopAsync();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
