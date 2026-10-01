using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.MapPool;
using SourceSharp.Host.Testing;

namespace SourceSharp.Host.MapPool.Tests;

/// <summary>
/// The fastdl listener as it runs: an app with <see cref="MapEndpoints.MapFastDl"/> and nothing
/// else. Requests are written raw on a socket, so dot segments reach the server as sent.
/// </summary>
public sealed class FastDlListenerFacts : IAsyncLifetime
{
    const string Name = "descent-2-00112233445566ff";
    WebApplication _app = null!;
    Uri _base = null!;

    public async Task InitializeAsync()
    {
        var options = new ServiceOptions { MapPool = { MapsPath = Path.Combine(Path.GetTempPath(), "lane-m-fastdl-" + Guid.NewGuid().ToString("N")) } };
        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        b.Services.AddSingleton(options);
        b.Services.AddSingleton(new LevelStorage(options.MapPool));
        _app = b.Build();
        _app.MapFastDl();
        await _app.StartAsync();
        _base = new Uri(_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
        var storage = _app.Services.GetRequiredService<LevelStorage>();
        await storage.StoreAsync(await new FakeMapCompiler().LinkAsync(
            new LinkRequest(Name, 2, 0, 1, "crypt", "", null, Path.Combine(options.MapPool.MapsPath, "staging", "x"))));
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    /// <summary>One raw HTTP/1.1 GET; returns the status code.</summary>
    async Task<int> Raw(string target)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(_base.Host, _base.Port);
        var s = tcp.GetStream();
        await s.WriteAsync(Encoding.ASCII.GetBytes($"GET {target} HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n"));
        using var r = new StreamReader(s, Encoding.ASCII);
        var line = await r.ReadLineAsync() ?? "";
        return int.Parse(line.Split(' ')[1]);
    }

    [Fact]
    public async Task The_listener_serves_the_levels_bz2() => Assert.Equal(200, await Raw($"/maps/{Name}.bsp.bz2"));

    [Theory]
    [InlineData("/maps/../internal/maps/" + Name + ".bsp")]
    [InlineData("/maps/../levels/" + Name + ".bsp")]
    [InlineData("/maps/./" + Name + ".bsp")]
    [InlineData("/internal/maps/" + Name + ".bsp")]
    [InlineData("/internal/pool")]
    [InlineData("/admin")]
    [InlineData("/levels/" + Name + ".bsp.bz2")]
    [InlineData("/")]
    public async Task The_listener_404s_everything_else(string target)
    {
        Assert.Equal(200, await Raw($"/maps/{Name}.bsp.bz2"));
        Assert.Equal(404, await Raw(target));
    }
}
