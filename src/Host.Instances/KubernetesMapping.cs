using k8s.Models;
using SourceSharp.Host.Abstractions;

namespace SourceSharp.Host.Instances;

/// <summary>PodSpec / JobSpec → Kubernetes objects, and V1Pod → PodStatus. Pure.</summary>
public static class KubernetesMapping
{
    public static V1Pod ToPod(PodSpec s, string ns) => new()
    {
        ApiVersion = "v1",
        Kind = "Pod",
        Metadata = new V1ObjectMeta { Name = s.Name, NamespaceProperty = ns, Labels = new Dictionary<string, string>(s.Labels) },
        Spec = new V1PodSpec
        {
            RestartPolicy = "Never",
            TerminationGracePeriodSeconds = s.TerminationGraceSeconds,
            AutomountServiceAccountToken = false,
            InitContainers = s.InitContainers.Count == 0 ? null : s.InitContainers.Select(ToContainer).ToList(),
            Containers = s.Containers.Select(ToContainer).ToList(),
            Volumes = s.Volumes.Select(ToVolume).ToList(),
            NodeSelector = s.NodeSelector.Count == 0 ? null : new Dictionary<string, string>(s.NodeSelector),
            Tolerations = s.Tolerations is { Count: > 0 } t
                ? t.Select(x => new V1Toleration { Key = x.Key, OperatorProperty = x.Operator, Value = x.Value, Effect = x.Effect }).ToList()
                : null,
        },
    };

    /// <summary>The pod's Secret, owned by the pod (its uid), so deleting the pod deletes the token.</summary>
    public static V1Secret ToSecret(SecretSpec s, PodRef owner, string ns) => new()
    {
        ApiVersion = "v1",
        Kind = "Secret",
        Metadata = new V1ObjectMeta
        {
            Name = s.Name,
            NamespaceProperty = ns,
            Labels = new Dictionary<string, string> { [GamePodLabels.App] = GamePodLabels.AppValue },
            OwnerReferences = [new V1OwnerReference { ApiVersion = "v1", Kind = "Pod", Name = owner.Name, Uid = owner.Uid, BlockOwnerDeletion = true }],
        },
        Type = "Opaque",
        StringData = new Dictionary<string, string>(s.Data),
    };

    public static V1Job ToJob(JobSpec s, string ns) => new()
    {
        ApiVersion = "batch/v1",
        Kind = "Job",
        Metadata = new V1ObjectMeta { Name = s.Name, NamespaceProperty = ns, Labels = new Dictionary<string, string>(s.Labels) },
        Spec = new V1JobSpec
        {
            BackoffLimit = s.BackoffLimit,
            ActiveDeadlineSeconds = s.ActiveDeadline is { } d ? (long)d.TotalSeconds : null,
            TtlSecondsAfterFinished = s.TtlSecondsAfterFinished,
            Template = new V1PodTemplateSpec
            {
                Metadata = new V1ObjectMeta { Labels = new Dictionary<string, string>(s.Labels) },
                Spec = new V1PodSpec
                {
                    RestartPolicy = "Never",
                    Containers = [ToContainer(s.Container)],
                    Volumes = s.Volumes.Select(ToVolume).ToList(),
                },
            },
        },
    };

    static V1Container ToContainer(ContainerSpec c) => new()
    {
        Name = c.Name,
        Image = c.Image,
        Command = c.Command.Count == 0 ? null : [.. c.Command],
        Args = c.Args.Count == 0 ? null : [.. c.Args],
        Env = c.Env.Count == 0 ? null : c.Env.Select(e => new V1EnvVar
        {
            Name = e.Name,
            Value = e.FromSecret is null ? e.Value : null,
            ValueFrom = e.FromSecret is { } r ? new V1EnvVarSource { SecretKeyRef = new V1SecretKeySelector { Name = r.Secret, Key = r.Key } } : null,
        }).ToList(),
        Ports = c.Ports.Count == 0 ? null : c.Ports.Select(p => new V1ContainerPort { Name = p.Name, ContainerPort = p.Port, Protocol = p.Protocol }).ToList(),
        VolumeMounts = c.Mounts.Count == 0 ? null : c.Mounts.Select(m => new V1VolumeMount { Name = m.Volume, MountPath = m.Path, ReadOnlyProperty = m.ReadOnly ? true : null }).ToList(),
        Resources = c.Resources is { } r2 ? new V1ResourceRequirements
        {
            Requests = new Dictionary<string, ResourceQuantity> { ["cpu"] = new(r2.CpuRequest), ["memory"] = new(r2.MemoryRequest) },
            Limits = new Dictionary<string, ResourceQuantity> { ["cpu"] = new(r2.CpuLimit), ["memory"] = new(r2.MemoryLimit) },
        } : null,
        ReadinessProbe = ToProbe(c.Readiness),
        LivenessProbe = ToProbe(c.Liveness),
    };

    static V1Probe? ToProbe(ProbeSpec? p) => p is null ? null : new V1Probe
    {
        PeriodSeconds = p.PeriodSeconds,
        FailureThreshold = p.FailureThreshold,
        HttpGet = p.HttpPath is { } path ? new V1HTTPGetAction { Path = path, Port = p.HttpPort } : null,
        Exec = p.HttpPath is null && p.Exec is { Count: > 0 } ex ? new V1ExecAction { Command = [.. ex] } : null,
    };

    static V1Volume ToVolume(VolumeSpec v) => v.ClaimName is { } claim
        ? new V1Volume { Name = v.Name, PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource { ClaimName = claim, ReadOnlyProperty = v.ReadOnly } }
        : new V1Volume { Name = v.Name, EmptyDir = new V1EmptyDirVolumeSource() };

    public static PodStatus ToStatus(V1Pod pod)
    {
        var st = pod.Status;
        var phase = st?.Phase switch
        {
            "Pending" => PodPhase.Pending,
            "Running" => PodPhase.Running,
            "Succeeded" => PodPhase.Succeeded,
            "Failed" => PodPhase.Failed,
            _ => st?.Phase is null ? PodPhase.Pending : PodPhase.Unknown,
        };
        var ready = st?.Conditions?.Any(c => c.Type == "Ready" && c.Status == "True") ?? false;
        var restarts = st?.ContainerStatuses?.Sum(c => c.RestartCount) ?? 0;
        var game = st?.ContainerStatuses?.FirstOrDefault(c => c.Name == "game");
        int? exit = game?.State?.Terminated?.ExitCode ?? game?.LastState?.Terminated?.ExitCode;
        return new PodStatus(pod.Metadata.Name, pod.Metadata.Uid, phase, ready, st?.PodIP, restarts, exit,
            pod.Metadata.Labels is { } l ? new Dictionary<string, string>(l) : new Dictionary<string, string>());
    }
}
