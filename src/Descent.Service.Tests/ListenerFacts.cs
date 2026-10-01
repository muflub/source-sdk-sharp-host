using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Descent.Service.Tests;

public class ListenerFacts
{
    [Fact]
    public async Task Admin_answers_on_the_admin_listener()
    {
        await using var svc = await TestService.StartAsync();
        var r = await svc.Http(ListenerRole.Admin).GetAsync("/admin");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Admin_is_404_on_the_internal_listener_at_the_same_address()
    {
        await using var svc = await TestService.StartAsync();
        var r = await svc.Http(ListenerRole.Internal).GetAsync("/admin");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Admin_is_404_on_the_fastdl_listener()
    {
        await using var svc = await TestService.StartAsync();
        var r = await svc.Http(ListenerRole.FastDl).GetAsync("/admin");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Healthz_reports_the_version_on_the_internal_listener()
    {
        await using var svc = await TestService.StartAsync();
        var r = await svc.Http(ListenerRole.Internal).GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        Assert.Equal(ServiceApp.Version, doc.RootElement.GetProperty("version").GetString());
    }

    [Fact]
    public async Task Healthz_is_503_when_a_reporter_fails()
    {
        await using var svc = await TestService.StartAsync(b =>
            b.Services.AddSingleton<SourceSharp.Host.Abstractions.IHealthReporter>(new FailingReporter()));
        var r = await svc.Http(ListenerRole.Admin).GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
    }

    [Fact]
    public async Task Metrics_are_not_served_on_the_fastdl_listener()
    {
        await using var svc = await TestService.StartAsync();
        Assert.Equal(HttpStatusCode.OK, (await svc.Http(ListenerRole.Internal).GetAsync("/metrics")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await svc.Http(ListenerRole.FastDl).GetAsync("/metrics")).StatusCode);
    }

    [Fact]
    public async Task A_non_loopback_admin_address_fails_on_start()
    {
        var app = TestService.Build(new()
        {
            ["Listen:Admin"] = "0.0.0.0:0",
            ["Listen:GameApi"] = "127.0.0.1:0",
            ["Listen:GatewayApi"] = "127.0.0.1:0",
            ["Listen:FastDl"] = "127.0.0.1:0",
        });
        var e = await Assert.ThrowsAsync<OptionsValidationException>(() => app.StartAsync());
        Assert.Contains("loopback", e.Message);
        await app.DisposeAsync();
    }

    [Fact]
    public async Task Ops_json_answers_on_the_admin_listener_only()
    {
        await using var svc = await TestService.StartAsync();
        var totals = await svc.Http(ListenerRole.Admin).GetStringAsync("/ops/items/totals");
        Assert.Contains("\"balanced\":true", totals);
        Assert.Equal(HttpStatusCode.NotFound, (await svc.Http(ListenerRole.Internal).GetAsync("/ops/items/totals")).StatusCode);
    }

    sealed class FailingReporter : SourceSharp.Host.Abstractions.IHealthReporter
    {
        public Task<SourceSharp.Host.Abstractions.HealthItem> CheckAsync(CancellationToken ct) =>
            Task.FromResult(new SourceSharp.Host.Abstractions.HealthItem("db", false, "unreachable"));
    }
}
