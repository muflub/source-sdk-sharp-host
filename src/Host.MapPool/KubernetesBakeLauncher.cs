using SourceSharp.Host.Abstractions;

namespace SourceSharp.Host.MapPool;

/// <summary>
/// §9.2: a bake as a Kubernetes Job from the service image. The Job mounts the service's maps
/// volume (the library source under library/&lt;library&gt;, and the tools' incremental store
/// under bake-cache) and the content volume read-only, runs `bake`, and POSTs the pack to the
/// service's internal endpoint, which validates it as an upload. A library that ships its own
/// gameinfo.txt bakes against itself (the map tools' samples do); any other against the content.
/// </summary>
public sealed class KubernetesBakeLauncher(IInstanceHost host, ServiceOptions options, LevelStorage storage, TimeProvider clock) : IBakeLauncher
{
    public const string MapsVolume = "maps", ContentVolume = "content";

    public JobSpec Spec(string mod, string library)
    {
        var o = options.MapPool;
        var source = $"/maps/library/{library}";
        var selfContained = File.Exists(Path.Combine(storage.Root, "library", library, "gameinfo.txt"));
        var name = $"bake-{library}-{clock.GetUtcNow().ToUnixTimeSeconds()}".ToLowerInvariant().Replace('_', '-');
        var args = new List<string>
        {
            "bake", "--library", library, "--mod", mod, "--source", source,
            "--content", selfContained ? source : "/content/tf",
            "--cache", "/maps/bake-cache", "--out", "/tmp/bake", "--service", options.Instances.InternalMapsUrl,
        };
        return new JobSpec(name,
            new Dictionary<string, string> { ["app"] = "sourcesharp-bake", ["sourcesharp/library"] = library.Replace('_', '-') },
            new ContainerSpec("bake", o.BakeImage, [], args, [], [],
                [new MountSpec(MapsVolume, "/maps"), new MountSpec(ContentVolume, "/content", ReadOnly: true)]),
            [new VolumeSpec(MapsVolume, o.BakeMapsClaim), new VolumeSpec(ContentVolume, options.Instances.ContentClaim, ReadOnly: true)],
            BackoffLimit: 0, ActiveDeadline: o.BakeTimeout, TtlSecondsAfterFinished: 3600);
    }

    public async Task<string> BakeAsync(string mod, string library, CancellationToken ct)
    {
        var job = await host.RunJob(Spec(mod, library), ct);
        await foreach (var e in host.WatchJob(job.Name, ct))
        {
            if (e.Phase == JobPhase.Succeeded) return $"bake job {job.Name} succeeded";
            if (e.Phase == JobPhase.Failed) throw new InvalidOperationException($"bake job {job.Name} failed: {e.Message}");
        }
        throw new OperationCanceledException($"bake job {job.Name} not finished");
    }
}
