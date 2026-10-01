namespace SourceSharp.Host.Abstractions;

// Plan §7.2: the seam between the instance manager and whatever runs game pods.
// KubernetesInstanceHost is production; FakeInstanceHost (Host.Testing) is what facts
// run against; LocalProcessInstanceHost is the dev path. The pod description below is
// platform-neutral on purpose: Host.Abstractions references no Kubernetes type, and
// Host.Instances maps a PodSpec to a V1Pod.

/// <summary>A pod is addressed by name AND uid: the same name with another uid is another pod (CLAUDE.md).</summary>
public sealed record PodRef(string Name, string Uid);

public enum PodPhase { Pending = 1, Running, Succeeded, Failed, Unknown }

public enum PodEventType { Added = 1, Modified, Deleted }

/// <summary>What the host reports about one pod.</summary>
/// <param name="Restarts">Container restarts summed over the pod's containers (restartPolicy Never: any non-zero is a crash).</param>
/// <param name="ExitCode">The game container's exit code once it has terminated.</param>
public sealed record PodStatus(
    string Name, string Uid, PodPhase Phase, bool Ready, string? Ip, int Restarts, int? ExitCode,
    IReadOnlyDictionary<string, string> Labels)
{
    public PodRef Ref => new(Name, Uid);
}

/// <summary>One watch event. A (re)connected watch starts with Added for every pod that exists.</summary>
public sealed record PodEvent(PodEventType Type, PodStatus Pod);

// ---- the pod description ----

public sealed record EnvVar(string Name, string? Value = null, SecretKeyRef? FromSecret = null);
public sealed record SecretKeyRef(string Secret, string Key);
public sealed record PortSpec(string Name, int Port, string Protocol = "TCP");
public sealed record MountSpec(string Volume, string Path, bool ReadOnly = false);

/// <summary>A volume: an emptyDir when <paramref name="ClaimName"/> is null, else that PersistentVolumeClaim.</summary>
public sealed record VolumeSpec(string Name, string? ClaimName = null, bool ReadOnly = false);

/// <summary>A readiness probe: an HTTP GET when <paramref name="HttpPath"/> is set, else an exec of <paramref name="Exec"/>.</summary>
public sealed record ProbeSpec(string? HttpPath = null, int HttpPort = 0, IReadOnlyList<string>? Exec = null, int PeriodSeconds = 2, int FailureThreshold = 3);

public sealed record ContainerSpec(
    string Name, string Image,
    IReadOnlyList<string> Command, IReadOnlyList<string> Args,
    IReadOnlyList<EnvVar> Env, IReadOnlyList<PortSpec> Ports, IReadOnlyList<MountSpec> Mounts,
    PodResources? Resources = null, ProbeSpec? Readiness = null, ProbeSpec? Liveness = null);

/// <summary>A Secret created after the pod, owned by it (so deleting the pod deletes it). The only place a token lives.</summary>
public sealed record SecretSpec(string Name, IReadOnlyDictionary<string, string> Data);

public sealed record PodSpec(
    string Name,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyList<ContainerSpec> InitContainers,
    IReadOnlyList<ContainerSpec> Containers,
    IReadOnlyList<VolumeSpec> Volumes,
    IReadOnlyDictionary<string, string> NodeSelector,
    int TerminationGraceSeconds,
    SecretSpec? Secret,
    IReadOnlyList<PodToleration>? Tolerations = null);

/// <summary>A toleration passed through to game pods (§7.5), so they can be pinned to a node pool.</summary>
public sealed class PodToleration
{
    public string Key { get; set; } = "";
    public string Operator { get; set; } = "Equal";
    public string? Value { get; set; }
    public string? Effect { get; set; }
}

// ---- jobs (the bake, §9.2) ----

public sealed record JobSpec(
    string Name, IReadOnlyDictionary<string, string> Labels, ContainerSpec Container, IReadOnlyList<VolumeSpec> Volumes,
    int BackoffLimit = 0, TimeSpan? ActiveDeadline = null, int? TtlSecondsAfterFinished = null);

public sealed record JobRef(string Name, string Uid);

public enum JobPhase { Pending = 1, Running, Succeeded, Failed }

public sealed record JobEvent(string Name, JobPhase Phase, string? Message);

/// <summary>
/// Runs game pods (§7.2). Every method addresses a pod by name and, where it acts, by uid.
/// </summary>
public interface IInstanceHost
{
    /// <summary>Creates the pod, then its Secret owned by the pod's uid. Returns the pod's name and uid.</summary>
    Task<PodRef> Create(PodSpec spec, CancellationToken ct = default);

    /// <summary>
    /// Pods labelled app=descent-game, forever: reconnects on its own, and every (re)connect
    /// starts with an Added event per existing pod. Ends only when <paramref name="ct"/> fires.
    /// </summary>
    IAsyncEnumerable<PodEvent> Watch(CancellationToken ct);

    /// <summary>Deletes the pod if its uid is still <paramref name="uid"/>, with that grace. False when no such pod (gone, or another uid).</summary>
    Task<bool> Delete(string name, string uid, TimeSpan grace, CancellationToken ct = default);

    /// <summary>The last <paramref name="tail"/> lines of the pod's game container.</summary>
    Task<string> Logs(string name, int tail, CancellationToken ct = default);

    Task<IReadOnlyList<PodStatus>> List(string labelSelector, CancellationToken ct = default);

    Task<JobRef> RunJob(JobSpec spec, CancellationToken ct = default);

    /// <summary>The job's phase changes until it succeeds or fails (then the stream ends).</summary>
    IAsyncEnumerable<JobEvent> WatchJob(string name, CancellationToken ct);
}

/// <summary>The labels every game pod carries (§7.1).</summary>
public static class GamePodLabels
{
    public const string App = "app";
    public const string AppValue = "descent-game";
    public const string Instance = "descent/instance";
    public const string Kind = "descent/kind";
    public const string Selector = App + "=" + AppValue;
}
