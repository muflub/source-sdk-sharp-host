using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SourceSharp.Host.Abstractions;

namespace TF2.Descent.Service.Tests.Api;

/// <summary>/ops/instances/{id}/modules: what an instance's server reported running at its last boot.</summary>
public class OpsModulesFacts
{
    static Task Report(TestService svc, string instance, string version) =>
        svc.App.Services.GetRequiredService<IHostData>().WriteAsync(async (tx, _) =>
        {
            await tx.Audit.Write(TF2.Descent.Service.Api.InstanceApi.ModulesAction, instance, null,
                new { sdk = "2.3.0", map = "m", loaded = new[] { new { Name = "TF2.Descent.Server", Version = version, Kind = "assembly" } } }, $"instance:{instance}");
            return true;
        });

    [Fact]
    public async Task The_last_boots_report_is_served()
    {
        await using var svc = await TestService.StartAsync();
        await Report(svc, "lvl-1", "1.0.0");
        await Report(svc, "lvl-1", "1.1.0"); // a second boot of the same instance (a same-process hop back)
        var json = await svc.Http(ListenerRole.Admin).GetFromJsonAsync<JsonElement>("/ops/instances/lvl-1/modules");
        Assert.Equal("1.1.0", json.GetProperty("loaded")[0].GetProperty("Version").GetString());
    }

    [Fact]
    public async Task An_instance_that_never_reported_is_404()
    {
        await using var svc = await TestService.StartAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await svc.Http(ListenerRole.Admin).GetAsync("/ops/instances/nope/modules")).StatusCode);
    }
}
