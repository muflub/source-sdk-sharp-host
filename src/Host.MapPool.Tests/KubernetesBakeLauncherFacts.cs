using Microsoft.Extensions.Time.Testing;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Testing;

namespace SourceSharp.Host.MapPool.Tests;

/// <summary>§9.2: a bake as a Kubernetes Job from the service image.</summary>
public sealed class KubernetesBakeLauncherFacts : IDisposable
{
    readonly string _maps = Path.Combine(Path.GetTempPath(), $"sourcesharp-bake-{Guid.NewGuid():N}");
    readonly FakeInstanceHost _host = new(TimeProvider.System);
    readonly ServiceOptions _o = new();

    public KubernetesBakeLauncherFacts()
    {
        _o.MapPool.MapsPath = _maps;
        _o.MapPool.BakeImage = "localhost:30500/tf2-descent-service:abc";
        Directory.CreateDirectory(Path.Combine(_maps, "library", "crypt"));
    }

    public void Dispose() { try { Directory.Delete(_maps, true); } catch (IOException) { } }

    KubernetesBakeLauncher Launcher() => new(_host, _o, new LevelStorage(_o.MapPool), new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000)));

    static string ArgAfter(IReadOnlyList<string> args, string flag) => args[args.ToList().IndexOf(flag) + 1];

    [Fact]
    public void The_job_runs_the_service_images_bake_with_both_volumes()
    {
        var spec = Launcher().Spec("tf2_descent", "crypt");
        Assert.Equal("localhost:30500/tf2-descent-service:abc", spec.Container.Image);
        Assert.Equal("bake", spec.Container.Args[0]);
        Assert.Equal(("crypt", "tf2_descent", "/maps/library/crypt", "http://tf2-descent-service-internal:5000"),
            (ArgAfter(spec.Container.Args, "--library"), ArgAfter(spec.Container.Args, "--mod"), ArgAfter(spec.Container.Args, "--source"), ArgAfter(spec.Container.Args, "--service")));
        Assert.Equal([("maps", "maps-tf2-descent-service-0", false), ("content", "content", true)], spec.Volumes.Select(v => (v.Name, v.ClaimName, v.ReadOnly)));
        Assert.Equal(0, spec.BackoffLimit);
    }

    [Fact]
    public void A_library_without_its_own_gameinfo_bakes_against_the_content()
    {
        Assert.Equal("/content/tf", ArgAfter(Launcher().Spec("tf2_descent", "crypt").Container.Args, "--content"));
    }

    [Fact]
    public void A_library_with_its_own_gameinfo_bakes_against_itself()
    {
        File.WriteAllText(Path.Combine(_maps, "library", "crypt", "gameinfo.txt"), "GameInfo {}");
        Assert.Equal("/maps/library/crypt", ArgAfter(Launcher().Spec("tf2_descent", "crypt").Container.Args, "--content"));
    }

    [Fact]
    public async Task A_succeeded_job_is_a_finished_bake()
    {
        var bake = Launcher().BakeAsync("tf2_descent", "crypt", default);
        var job = Assert.Single(_host.Jobs).Name;
        _host.SetJobPhase(job, JobPhase.Succeeded);
        Assert.Contains("succeeded", await bake);
    }

    [Fact]
    public async Task A_failed_job_is_a_failed_bake_with_its_message()
    {
        var bake = Launcher().BakeAsync("tf2_descent", "crypt", default);
        _host.SetJobPhase(Assert.Single(_host.Jobs).Name, JobPhase.Failed, "BackoffLimitExceeded");
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => bake);
        Assert.Contains("BackoffLimitExceeded", e.Message);
    }
}
