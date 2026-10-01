using System.Reflection;
using SourceSharp.Host.Abstractions;

namespace SourceSharp.Host.Sdk.Tests;

public class ConfigurationFacts
{
    static Func<string, string?> Env(params (string Key, string Value)[] vars)
    {
        var d = vars.ToDictionary(v => v.Key, v => v.Value);
        return k => d.TryGetValue(k, out var v) ? v : null;
    }

    static readonly (string, string)[] PodEnv =
    [
        (HostSdkConfig.ServiceVar, "descent-service:5001"), (HostSdkConfig.InstanceIdVar, "lvl-7"), (HostSdkConfig.TokenVar, "tok"),
    ];

    sealed class RecordingStarter(string json) : ILocalHostStarter
    {
        public List<string> Dirs { get; } = [];
        public Task<string> StartAsync(string dataDir, CancellationToken ct) { Dirs.Add(dataDir); return Task.FromResult(json); }
    }

    static string TempFile(string text)
    {
        var p = Path.Combine(Path.GetTempPath(), $"local-host-{Guid.NewGuid():N}.json");
        File.WriteAllText(p, text);
        return p;
    }

    const string Record = """{ "service": "http://127.0.0.1:5001", "instanceId": "local-hub", "token": "dev-token" }""";

    [Fact]
    public async Task Pod_mode_reads_the_pod_environment_and_defaults_the_sidecar()
    {
        var e = await HostSdkConfig.Resolve(["srcds"], Env(PodEnv), new RecordingStarter(Record));
        Assert.Equal((HostMode.Pod, "http://descent-service:5001/", "lvl-7", "tok", "http://127.0.0.1:5011/"),
            (e.Mode, e.Service.ToString(), e.InstanceId, e.Token, e.Sidecar!.ToString()));
    }

    [Fact]
    public async Task Pod_mode_names_every_missing_variable()
    {
        var e = await Assert.ThrowsAsync<HostConfigException>(() => HostSdkConfig.Resolve(["srcds"], Env((HostSdkConfig.InstanceIdVar, "x")), new RecordingStarter(Record)));
        Assert.Equal(HostConfigError.MissingEnvironment, e.Error);
        Assert.Contains(HostSdkConfig.ServiceVar, e.Message);
        Assert.Contains(HostSdkConfig.TokenVar, e.Message);
    }

    [Fact]
    public async Task The_hostlocal_argument_selects_a_local_file_and_reads_its_record()
    {
        var file = TempFile(Record);
        var e = await HostSdkConfig.Resolve(["srcds", "-game", "x", "-hostlocal", file], Env(PodEnv), new RecordingStarter("{}"));
        Assert.Equal((HostMode.LocalFile, "http://127.0.0.1:5001/", "local-hub", "dev-token", (Uri?)null),
            (e.Mode, e.Service.ToString(), e.InstanceId, e.Token, e.Sidecar));
    }

    [Fact]
    public async Task The_local_environment_variable_selects_a_local_file()
    {
        var file = TempFile(Record);
        var e = await HostSdkConfig.Resolve(["srcds"], Env([.. PodEnv, (HostSdkConfig.LocalVar, file)]), new RecordingStarter("{}"));
        Assert.Equal((HostMode.LocalFile, "local-hub"), (e.Mode, e.InstanceId));
    }

    [Fact]
    public async Task The_argument_wins_over_the_environment_variable()
    {
        var fromArg = TempFile(Record.Replace("local-hub", "from-arg"));
        var fromEnv = TempFile(Record.Replace("local-hub", "from-env"));
        var e = await HostSdkConfig.Resolve(["srcds", "-hostlocal", fromArg], Env((HostSdkConfig.LocalVar, fromEnv)), new RecordingStarter("{}"));
        Assert.Equal("from-arg", e.InstanceId);
    }

    [Fact]
    public async Task Inproc_starts_the_local_host_in_the_default_directory_and_uses_its_record()
    {
        var starter = new RecordingStarter(Record.Replace("local-hub", "inproc-hub"));
        var e = await HostSdkConfig.Resolve(["srcds", "-hostlocal", "inproc"], Env(PodEnv), starter);
        Assert.Equal((HostMode.LocalInProc, "inproc-hub"), (e.Mode, e.InstanceId));
        Assert.Equal([LocalHostContract.DefaultDataDir], starter.Dirs);
    }

    [Fact]
    public async Task Inproc_with_a_directory_passes_that_directory()
    {
        var starter = new RecordingStarter(Record);
        var e = await HostSdkConfig.Resolve(["srcds"], Env((HostSdkConfig.LocalVar, "inproc:/tmp/my-local")), starter);
        Assert.Equal(HostMode.LocalInProc, e.Mode);
        Assert.Equal(["/tmp/my-local"], starter.Dirs);
    }

    [Fact]
    public async Task A_missing_local_file_is_a_named_error()
    {
        var e = await Assert.ThrowsAsync<HostConfigException>(() =>
            HostSdkConfig.Resolve(["srcds", "-hostlocal", "/nonexistent/local-host.json"], Env(), new RecordingStarter(Record)));
        Assert.Equal(HostConfigError.LocalFileMissing, e.Error);
    }

    [Fact]
    public async Task A_local_record_without_a_token_is_refused()
    {
        var file = TempFile("""{ "service": "http://127.0.0.1:5001", "instanceId": "local-hub" }""");
        var e = await Assert.ThrowsAsync<HostConfigException>(() => HostSdkConfig.Resolve(["srcds", "-hostlocal", file], Env(), new RecordingStarter(Record)));
        Assert.Equal(HostConfigError.LocalFileInvalid, e.Error);
    }

    [Fact]
    public async Task The_hostlocal_flag_without_a_value_is_refused()
    {
        var e = await Assert.ThrowsAsync<HostConfigException>(() => HostSdkConfig.Resolve(["srcds", "-hostlocal", "+map", "x"], Env(PodEnv), new RecordingStarter(Record)));
        Assert.Equal(HostConfigError.LocalFileMissing, e.Error);
    }

    [Fact]
    public async Task The_reflection_starter_names_the_missing_local_assembly()
    {
        var starter = new ReflectionLocalHostStarter(n => throw new FileNotFoundException($"no {n}"));
        var e = await Assert.ThrowsAsync<HostConfigException>(() => HostSdkConfig.Resolve(["srcds", "-hostlocal", "inproc"], Env(), starter));
        Assert.Equal(HostConfigError.LocalHostMissing, e.Error);
        Assert.Contains(LocalHostContract.MissingMessage, e.Message);
    }

    [Fact]
    public async Task The_default_reflection_starter_reports_the_absent_dev_assembly()
    {
        // This test process does not ship SourceSharp.Host.Local: the real Assembly.Load path must say so, typed.
        var e = await Assert.ThrowsAsync<HostConfigException>(() => new ReflectionLocalHostStarter().StartAsync("./x", CancellationToken.None));
        Assert.Equal(HostConfigError.LocalHostMissing, e.Error);
    }

    [Fact]
    public async Task The_reflection_starter_refuses_an_assembly_without_the_entry_type()
    {
        var starter = new ReflectionLocalHostStarter(_ => typeof(ConfigurationFacts).Assembly);
        var e = await Assert.ThrowsAsync<HostConfigException>(() => starter.StartAsync("./x", CancellationToken.None));
        Assert.Equal(HostConfigError.LocalHostInvalid, e.Error);
    }

    [Fact]
    public void The_documented_local_record_round_trips()
    {
        var f = HostLocalFile.Parse(Record, "doc");
        Assert.Equal(new HostLocalFile("http://127.0.0.1:5001", "local-hub", "dev-token"), f);
        Assert.Equal(f, HostLocalFile.Parse(f.ToJson(), "round trip"));
        Assert.DoesNotContain("sidecar", f.ToJson());
    }

    [Fact]
    public async Task Local_mode_reads_the_file_and_connects_to_the_service_it_names()
    {
        await using var h = await SdkHarness.Start();
        await h.Instance("local-hub", InstanceKind.Hub);
        var file = TempFile(new HostLocalFile(h.Direct.ToString(), "local-hub", SdkHarness.Token("local-hub")).ToJson());
        await using var sdk = await HostSdk.StartAsync(["srcds", "-hostlocal", file], Env(), SdkHarness.FastOptions());
        Assert.Equal(HostMode.LocalFile, sdk.Endpoint.Mode);
        await sdk.PumpUntil(() => sdk.Session.Connected, what: "the stream acknowledged");
        var created = await sdk.Pumped(sdk.Characters.Create("76561198000000001", "scout", "Ann"));
        Assert.True(created.Ok, created.ToString());
        var row = await h.Data.ReadAsync((tx, _) => tx.Characters.Get(created.Value.Id));
        Assert.NotNull(row);
    }

    [Fact]
    public async Task Inproc_mode_connects_with_the_record_the_starter_returned()
    {
        await using var h = await SdkHarness.Start();
        await h.Instance("inproc-hub", InstanceKind.Hub);
        var starter = new RecordingStarter(new HostLocalFile(h.Direct.ToString(), "inproc-hub", SdkHarness.Token("inproc-hub")).ToJson());
        var o = SdkHarness.FastOptions();
        o.LocalHostStarter = starter;
        await using var sdk = await HostSdk.StartAsync(["srcds", "-hostlocal", "inproc"], Env(PodEnv), o);
        Assert.Equal(HostMode.LocalInProc, sdk.Endpoint.Mode);
        var list = await sdk.Pumped(sdk.Characters.List("76561198000000001"));
        Assert.True(list.Ok, list.ToString());
        Assert.Single(starter.Dirs);
    }

    [Fact]
    public void The_local_host_contract_names_a_static_task_of_string()
    {
        // The lead's SourceSharp.Host.Local must match these strings exactly; pin them.
        Assert.Equal(("SourceSharp.Host.Local", "SourceSharp.Host.Local.LocalHost", "StartAsync"),
            (LocalHostContract.AssemblyName, LocalHostContract.TypeName, LocalHostContract.MethodName));
        Assert.NotNull(typeof(ReflectionLocalHostStarter).GetMethod(nameof(ILocalHostStarter.StartAsync), BindingFlags.Public | BindingFlags.Instance));
    }
}
