using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Testing;

namespace TF2.Descent.Service.Tests.Api;

/// <summary>/ops/instances/{id}/exec, /drain and /ops/packs/{mod}/{library}/rebake: the admin UI's calls, for a script.</summary>
public class OpsActionFacts
{
    static async Task<(TestService Svc, AdminFakeLifecycle Life)> Start()
    {
        AdminFakeLifecycle? life = null;
        var svc = await TestService.StartAsync(b => b.Services.AddSingleton<IInstanceLifecycle>(sp => life = new AdminFakeLifecycle(sp.GetRequiredService<IHostData>())));
        svc.App.Services.GetRequiredService<IInstanceLifecycle>();
        return (svc, life!);
    }

    [Fact]
    public async Task Exec_sends_the_command_to_the_instance()
    {
        var (svc, life) = await Start();
        await using var _ = svc;
        var r = await svc.Http(ListenerRole.Admin).PostAsJsonAsync("/ops/instances/hub-1/exec", new { command = "tf2_descent_travel 1 down" });
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        Assert.Contains("exec:hub-1:tf2_descent_travel 1 down", life.Calls);
    }

    [Fact]
    public async Task Exec_to_an_instance_with_no_stream_is_a_named_conflict()
    {
        var (svc, life) = await Start();
        await using var _ = svc;
        life.HasStream = false;
        var r = await svc.Http(ListenerRole.Admin).PostAsJsonAsync("/ops/instances/hub-1/exec", new { command = "status" });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Contains("no_stream", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Exec_without_a_command_is_refused()
    {
        var (svc, life) = await Start();
        await using var _ = svc;
        var r = await svc.Http(ListenerRole.Admin).PostAsJsonAsync("/ops/instances/hub-1/exec", new { command = " " });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.DoesNotContain(life.Calls, c => c.StartsWith("exec:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Exec_is_not_served_on_the_internal_listener()
    {
        var (svc, life) = await Start();
        await using var _ = svc;
        var r = await svc.Http(ListenerRole.Internal).PostAsJsonAsync("/ops/instances/hub-1/exec", new { command = "status" });
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Empty(life.Calls);
    }

    [Fact]
    public async Task Drain_drains_the_instance()
    {
        var (svc, life) = await Start();
        await using var _ = svc;
        await svc.App.Services.GetRequiredService<IHostData>().WriteAsync((tx, _) => tx.Instances.Add(new InstanceRecord("hub-9", InstanceKind.Hub,
            InstanceState.Live, 0, null, null, "p", "u", "10.0.0.1", 27015, "h", null, null, null, 1, DateTimeOffset.UtcNow, null, null, null, null, null, null, null, 0, 27015)));
        var r = await svc.Http(ListenerRole.Admin).PostAsync("/ops/instances/hub-9/drain?reason=replace", null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("drain:hub-9", life.Calls);
        Assert.Contains("Draining", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Rebake_queues_a_bake_job_for_the_library()
    {
        var (svc, _) = await Start();
        await using var _s = svc;
        var data = svc.App.Services.GetRequiredService<IHostData>();
        var before = await data.ReadAsync((tx, _) => tx.MapJobs.List(null, 1000));
        var r = await svc.Http(ListenerRole.Admin).PostAsync("/ops/packs/tf2_descent/crypt2/rebake", null);
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        var after = await data.ReadAsync((tx, _) => tx.MapJobs.List(null, 1000));
        var added = Assert.Single(after, j => before.All(b => b.Id != j.Id));
        Assert.Equal(MapJobKind.Bake, added.Kind);
        Assert.StartsWith("bake:tf2_descent:crypt2:rebake-", added.Key);
    }

    [Fact]
    public async Task Rebake_is_not_served_on_the_internal_listener()
    {
        var (svc, _) = await Start();
        await using var _s = svc;
        var r = await svc.Http(ListenerRole.Internal).PostAsync("/ops/packs/tf2_descent/crypt2/rebake", null);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }
}
