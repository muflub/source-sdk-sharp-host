using System.Net;
using Descent.Service;
using SourceSharp.Host.Abstractions;

namespace SourceSharp.Host.Admin.Tests;

/// <summary>
/// §11 gate: the admin UI is served on the loopback admin listener only. Run against the real
/// service host (ServiceApp.Build), with AddHostAdmin / MapHostAdmin().On(ListenerRole.Admin)
/// as the composition root will call them. /admin itself is still the lead's H1 stub in
/// ServiceApp, so these facts use the other pages; the stub goes when the lead wires this in.
/// </summary>
public sealed class AdminEndpointFacts : IAsyncLifetime
{
    AdminServiceHost _host = null!;
    public async Task InitializeAsync() => _host = await AdminServiceHost.StartAsync();
    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task An_admin_page_renders_on_the_admin_listener()
    {
        using var http = _host.Http(ListenerRole.Admin);
        var r = await http.GetAsync("/admin/audit");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("<h1>Audit log</h1>", await r.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(ListenerRole.Internal)]
    [InlineData(ListenerRole.FastDl)]
    public async Task An_admin_page_is_404_on_every_other_http_listener(ListenerRole role)
    {
        using var http = _host.Http(role);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/admin/audit")).StatusCode);
    }

    [Fact]
    public async Task The_csv_export_is_404_on_the_internal_listener()
    {
        using var http = _host.Http(ListenerRole.Internal);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/admin/export/audit.csv")).StatusCode);
    }

    [Fact]
    public async Task The_blazor_script_is_served_on_the_admin_listener()
    {
        using var http = _host.Http(ListenerRole.Admin);
        var r = await http.GetAsync("/_framework/blazor.web.js");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task The_blazor_circuit_hub_negotiates_on_the_admin_listener()
    {
        using var http = _host.Http(ListenerRole.Admin);
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsync("/_blazor/negotiate?negotiateVersion=1", null)).StatusCode);
    }

    [Fact]
    public async Task The_blazor_circuit_hub_is_404_on_the_internal_listener()
    {
        using var http = _host.Http(ListenerRole.Internal);
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsync("/_blazor/negotiate?negotiateVersion=1", null)).StatusCode);
    }
}
