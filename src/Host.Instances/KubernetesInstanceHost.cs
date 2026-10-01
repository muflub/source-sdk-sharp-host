using System.Net;
using System.Runtime.CompilerServices;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SourceSharp.Host.Abstractions;

namespace SourceSharp.Host.Instances;

/// <summary>
/// The production <see cref="IInstanceHost"/> (§7.2) over the KubernetesClient package:
/// one namespace, pods by the app=descent-game label, deletes with a uid precondition, a
/// relisting watch that reconnects on its own.
/// </summary>
public sealed class KubernetesInstanceHost(IKubernetes client, string ns, TimeProvider clock, ILogger<KubernetesInstanceHost>? log = null) : IInstanceHost
{
    readonly ILogger _log = (ILogger?)log ?? NullLogger.Instance;

    /// <summary>How long the watch waits before relisting after an error or a closed stream.</summary>
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>In-cluster config when <paramref name="kubeconfig"/> is empty, else that kubeconfig file.</summary>
    public static IKubernetes Connect(string? kubeconfig) =>
        new Kubernetes(string.IsNullOrEmpty(kubeconfig)
            ? KubernetesClientConfiguration.InClusterConfig()
            : KubernetesClientConfiguration.BuildConfigFromConfigFile(kubeconfig));

    public async Task<PodRef> Create(PodSpec spec, CancellationToken ct = default)
    {
        var pod = await client.CoreV1.CreateNamespacedPodAsync(KubernetesMapping.ToPod(spec, ns), ns, cancellationToken: ct);
        var r = new PodRef(pod.Metadata.Name, pod.Metadata.Uid);
        if (spec.Secret is { } secret)
        {
            try
            {
                await client.CoreV1.CreateNamespacedSecretAsync(KubernetesMapping.ToSecret(secret, r, ns), ns, cancellationToken: ct);
            }
            catch
            {
                // A pod without its token can never register: take it back down, uid-checked.
                await Delete(r.Name, r.Uid, TimeSpan.Zero, CancellationToken.None);
                throw;
            }
        }
        return r;
    }

    public async Task<bool> Delete(string name, string uid, TimeSpan grace, CancellationToken ct = default)
    {
        try
        {
            await client.CoreV1.DeleteNamespacedPodAsync(name, ns,
                new V1DeleteOptions
                {
                    GracePeriodSeconds = (long)grace.TotalSeconds,
                    Preconditions = new V1Preconditions { Uid = uid },
                    PropagationPolicy = "Background",
                },
                cancellationToken: ct);
            return true;
        }
        catch (HttpOperationException e) when (e.Response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict)
        {
            // 404: gone. 409: the uid precondition failed — the name is another pod now; never touch it.
            return false;
        }
    }

    public async Task<string> Logs(string name, int tail, CancellationToken ct = default)
    {
        await using var stream = await client.CoreV1.ReadNamespacedPodLogAsync(name, ns, container: "game", tailLines: tail, cancellationToken: ct);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ct);
    }

    public async Task<IReadOnlyList<PodStatus>> List(string labelSelector, CancellationToken ct = default)
    {
        var list = await client.CoreV1.ListNamespacedPodAsync(ns, labelSelector: labelSelector, cancellationToken: ct);
        return list.Items.Select(KubernetesMapping.ToStatus).ToList();
    }

    public async IAsyncEnumerable<PodEvent> Watch([EnumeratorCancellation] CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // List, then watch from the list's resourceVersion: every (re)connect starts with the whole set.
            V1PodList? list = null;
            try { list = await client.CoreV1.ListNamespacedPodAsync(ns, labelSelector: GamePodLabels.Selector, cancellationToken: ct); }
            catch (Exception e) when (!ct.IsCancellationRequested) { _log.LogWarning(e, "pod list failed; retrying"); }
            if (list is not null)
            {
                foreach (var pod in list.Items) yield return new PodEvent(PodEventType.Added, KubernetesMapping.ToStatus(pod));

                var events = client.CoreV1.WatchListNamespacedPodAsync(ns, labelSelector: GamePodLabels.Selector,
                    resourceVersion: list.Metadata?.ResourceVersion, cancellationToken: ct);
                var e2 = events.GetAsyncEnumerator(ct);
                try
                {
                    while (true)
                    {
                        (WatchEventType Type, V1Pod Pod) item;
                        try
                        {
                            if (!await e2.MoveNextAsync()) break;
                            item = e2.Current;
                        }
                        catch (Exception e) when (!ct.IsCancellationRequested)
                        {
                            _log.LogWarning(e, "pod watch ended; relisting");
                            break;
                        }
                        var type = item.Type switch
                        {
                            WatchEventType.Added => PodEventType.Added,
                            WatchEventType.Modified => PodEventType.Modified,
                            WatchEventType.Deleted => PodEventType.Deleted,
                            _ => (PodEventType?)null,
                        };
                        if (type is { } t && item.Pod?.Metadata?.Name is not null)
                            yield return new PodEvent(t, KubernetesMapping.ToStatus(item.Pod));
                    }
                }
                finally { await e2.DisposeAsync(); }
            }
            if (ct.IsCancellationRequested) yield break;
            try { await Task.Delay(ReconnectDelay, clock, ct); }
            catch (OperationCanceledException) { yield break; }
        }
    }

    public async Task<JobRef> RunJob(JobSpec spec, CancellationToken ct = default)
    {
        var job = await client.BatchV1.CreateNamespacedJobAsync(KubernetesMapping.ToJob(spec, ns), ns, cancellationToken: ct);
        return new JobRef(job.Metadata.Name, job.Metadata.Uid);
    }

    public async IAsyncEnumerable<JobEvent> WatchJob(string name, [EnumeratorCancellation] CancellationToken ct)
    {
        JobPhase? last = null;
        while (!ct.IsCancellationRequested)
        {
            V1Job? job = null;
            try { job = await client.BatchV1.ReadNamespacedJobAsync(name, ns, cancellationToken: ct); }
            catch (Exception e) when (!ct.IsCancellationRequested) { _log.LogWarning(e, "job {Job} read failed", name); }
            if (job is not null)
            {
                var (phase, message) = JobPhaseOf(job);
                if (phase != last) { last = phase; yield return new JobEvent(name, phase, message); }
                if (phase is JobPhase.Succeeded or JobPhase.Failed) yield break;
            }
            try { await Task.Delay(ReconnectDelay, clock, ct); }
            catch (OperationCanceledException) { yield break; }
        }
    }

    internal static (JobPhase, string?) JobPhaseOf(V1Job job)
    {
        var conditions = job.Status?.Conditions ?? [];
        if (conditions.FirstOrDefault(c => c.Type == "Complete" && c.Status == "True") is not null) return (JobPhase.Succeeded, null);
        if (conditions.FirstOrDefault(c => c.Type == "Failed" && c.Status == "True") is { } f) return (JobPhase.Failed, f.Message ?? f.Reason);
        return (job.Status?.Active ?? 0) > 0 ? (JobPhase.Running, null) : (JobPhase.Pending, null);
    }
}
