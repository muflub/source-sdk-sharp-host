using System.Net;
using System.Text;
using System.Text.Json;
using k8s;
using k8s.Models;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Instances;

namespace SourceSharp.Host.Instances.Tests;

/// <summary>
/// KubernetesInstanceHost against an in-process fake API server: a handler at the end of the
/// client's pipeline that answers each request from a script and records it.
/// </summary>
public class KubernetesInstanceHostFacts
{
    sealed record Seen(string Method, string PathAndQuery, string? Body);

    sealed class FakeApi : DelegatingHandler
    {
        public List<Seen> Requests { get; } = [];
        public Func<HttpRequestMessage, string?, HttpResponseMessage> Answer { get; set; } = (_, _) => new HttpResponseMessage(HttpStatusCode.NotFound);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            lock (Requests) Requests.Add(new Seen(request.Method.Method, request.RequestUri!.PathAndQuery, body));
            var resp = Answer(request, body);
            resp.RequestMessage = request;
            return resp;
        }
    }

    static (KubernetesInstanceHost Host, FakeApi Api) Make()
    {
        var api = new FakeApi();
        var client = new Kubernetes(new KubernetesClientConfiguration { Host = "http://fake-apiserver" }, api);
        return (new KubernetesInstanceHost(client, "descent", TimeProvider.System) { ReconnectDelay = TimeSpan.Zero }, api);
    }

    static HttpResponseMessage Json(object o, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(KubernetesJson.Serialize(o), Encoding.UTF8, "application/json") };

    static HttpResponseMessage Status(int code) =>
        new((HttpStatusCode)code) { Content = new StringContent($$"""{"kind":"Status","apiVersion":"v1","status":"Failure","code":{{code}}}""", Encoding.UTF8, "application/json") };

    static V1Pod PodObj(string name, string uid, string phase = "Running", bool ready = true) => new()
    {
        Metadata = new V1ObjectMeta { Name = name, Uid = uid, Labels = new Dictionary<string, string> { ["app"] = "descent-game" } },
        Status = new V1PodStatus
        {
            Phase = phase,
            PodIP = "10.42.0.9",
            Conditions = [new V1PodCondition { Type = "Ready", Status = ready ? "True" : "False" }],
            ContainerStatuses =
            [
                new V1ContainerStatus { Name = "game", RestartCount = 1, State = new V1ContainerState { Terminated = new V1ContainerStateTerminated { ExitCode = 137 } } },
                new V1ContainerStatus { Name = "sidecar", RestartCount = 0 },
            ],
        },
    };

    static PodSpec Spec() => GamePodBuilder.Build(new InstanceOptions(), new ModOptions(), PodSpecFacts.Row(InstanceKind.Hub), "tok");

    [Fact]
    public async Task Delete_carries_the_uid_precondition_and_the_grace()
    {
        var (host, api) = Make();
        api.Answer = (_, _) => Json(PodObj("p", "u1"));
        Assert.True(await host.Delete("p", "u1", TimeSpan.FromSeconds(60)));
        var req = api.Requests.Single();
        Assert.Equal(("DELETE", "/api/v1/namespaces/descent/pods/p"), (req.Method, req.PathAndQuery));
        using var body = JsonDocument.Parse(req.Body!);
        Assert.Equal("u1", body.RootElement.GetProperty("preconditions").GetProperty("uid").GetString());
        Assert.Equal(60, body.RootElement.GetProperty("gracePeriodSeconds").GetInt64());
    }

    [Fact]
    public async Task Delete_of_a_pod_with_another_uid_reports_false()
    {
        var (host, api) = Make();
        api.Answer = (_, _) => Status(409);
        Assert.False(await host.Delete("p", "stale-uid", TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public async Task Delete_of_a_missing_pod_reports_false()
    {
        var (host, api) = Make();
        api.Answer = (_, _) => Status(404);
        Assert.False(await host.Delete("p", "u1", TimeSpan.Zero));
    }

    [Fact]
    public async Task Create_posts_the_pod_then_a_secret_owned_by_its_uid()
    {
        var (host, api) = Make();
        api.Answer = (req, body) => req.RequestUri!.AbsolutePath.EndsWith("/pods")
            ? Json(PodObj(Spec().Name, "uid-7"), HttpStatusCode.Created)
            : Json(new V1Secret { Metadata = new V1ObjectMeta { Name = "s" } }, HttpStatusCode.Created);
        var r = await host.Create(Spec());
        Assert.Equal(new PodRef(Spec().Name, "uid-7"), r);
        Assert.Equal(["/api/v1/namespaces/descent/pods", "/api/v1/namespaces/descent/secrets"], api.Requests.Select(x => x.PathAndQuery));
        using var secret = JsonDocument.Parse(api.Requests[1].Body!);
        Assert.Equal("uid-7", secret.RootElement.GetProperty("metadata").GetProperty("ownerReferences")[0].GetProperty("uid").GetString());
    }

    [Fact]
    public async Task Create_deletes_the_pod_when_its_secret_cannot_be_created()
    {
        var (host, api) = Make();
        api.Answer = (req, _) => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/pods")
            ? Json(PodObj(Spec().Name, "uid-7"), HttpStatusCode.Created)
            : req.Method == HttpMethod.Delete ? Json(PodObj(Spec().Name, "uid-7"))
            : Status(403);
        await Assert.ThrowsAnyAsync<Exception>(() => host.Create(Spec()));
        var delete = api.Requests.Last();
        Assert.Equal("DELETE", delete.Method);
        Assert.Contains("uid-7", delete.Body);
    }

    [Fact]
    public async Task Watch_starts_with_the_list_and_relists_after_the_stream_ends()
    {
        var (host, api) = Make();
        var lists = 0;
        api.Answer = (req, _) =>
        {
            if (req.RequestUri!.Query.Contains("watch=true"))
            {
                var line = JsonSerializer.Serialize(new { type = "MODIFIED", @object = JsonDocument.Parse(KubernetesJson.Serialize(PodObj("a", "ua", ready: false))).RootElement });
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(line + "\n", Encoding.UTF8, "application/json") };
            }
            lists++;
            return Json(new V1PodList { Metadata = new V1ListMeta { ResourceVersion = $"{lists}" }, Items = [PodObj("a", "ua")] });
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var seen = new List<(PodEventType, string, bool)>();
        await foreach (var e in host.Watch(cts.Token))
        {
            seen.Add((e.Type, e.Pod.Name, e.Pod.Ready));
            if (seen.Count == 4) break;
        }
        Assert.Equal([(PodEventType.Added, "a", true), (PodEventType.Modified, "a", false), (PodEventType.Added, "a", true), (PodEventType.Modified, "a", false)], seen);
        Assert.Equal(2, lists);
        var watches = api.Requests.Where(r => r.PathAndQuery.Contains("watch=true")).ToList();
        Assert.Contains("resourceVersion=1", watches[0].PathAndQuery);
        Assert.Contains("labelSelector=app%3Ddescent-game", watches[0].PathAndQuery);
    }

    [Fact]
    public async Task List_maps_phase_ready_ip_restarts_and_the_game_exit_code()
    {
        var (host, api) = Make();
        api.Answer = (_, _) => Json(new V1PodList { Items = [PodObj("a", "ua", phase: "Failed", ready: false)] });
        var s = (await host.List("app=descent-game")).Single();
        Assert.Equal(("a", "ua", PodPhase.Failed, false, "10.42.0.9", 1, (int?)137), (s.Name, s.Uid, s.Phase, s.Ready, s.Ip, s.Restarts, s.ExitCode));
    }

    [Fact]
    public void Job_phase_follows_its_conditions()
    {
        var job = new V1Job { Status = new V1JobStatus { Active = 1 } };
        Assert.Equal(JobPhase.Running, KubernetesInstanceHost.JobPhaseOf(job).Item1);
        job.Status.Conditions = [new V1JobCondition { Type = "Failed", Status = "True", Message = "backoff" }];
        Assert.Equal((JobPhase.Failed, "backoff"), KubernetesInstanceHost.JobPhaseOf(job));
        job.Status.Conditions = [new V1JobCondition { Type = "Complete", Status = "True" }];
        Assert.Equal(JobPhase.Succeeded, KubernetesInstanceHost.JobPhaseOf(job).Item1);
    }
}
