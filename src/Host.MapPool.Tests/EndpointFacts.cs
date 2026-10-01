using System.Net;
using System.Net.Http.Headers;
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
/// Q18's two endpoints and the bake's POST, served by a real Kestrel on loopback in-process,
/// wired through <see cref="MapPoolServices.AddHostMapPool"/>. Every 404 fact first shows the
/// same level's served file answers 200.
/// </summary>
public sealed class EndpointFacts : IAsyncLifetime
{
    const string Name = "descent-3-0123456789abcdef";
    WebApplication _app = null!;
    HttpClient _http = null!;
    readonly TestData _db = new();
    ServiceOptions _options = null!;

    public async Task InitializeAsync()
    {
        _options = new ServiceOptions { MapPool = { MapsPath = Path.Combine(Path.GetTempPath(), "lane-m-http-" + Guid.NewGuid().ToString("N")) } };
        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        b.Services.AddSingleton(_options);
        b.Services.AddSingleton<IHostData>(_db.Data);
        b.Services.AddSingleton<TimeProvider>(_db.Clock);
        b.Services.AddSingleton<IRulesProvider>(new SwitchableRules { Current = null });
        b.Services.AddSingleton<IPoolDemand, NoPoolDemand>();
        b.Services.AddSingleton<IMapCompiler, FakeMapCompiler>();
        b.Services.AddSingleton<IPackValidator>(new FakePackValidator("descent"));
        b.Services.AddHostMapPool();
        _app = b.Build();
        _app.MapFastDl();
        _app.MapInternalMaps();
        _app.MapInternalPacks();
        _app.MapPoolStatus();
        await _app.StartAsync();
        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _http = new HttpClient { BaseAddress = new Uri(address) };

        var storage = _app.Services.GetRequiredService<LevelStorage>();
        var files = await new FakeMapCompiler().LinkAsync(new LinkRequest(Name, 3, 0, 1, "crypt", "", null, Path.Combine(_options.MapPool.MapsPath, "staging", "x")));
        await storage.StoreAsync(files);
        File.Copy(storage.LevelFile(Name, ".bsp.bz2"), storage.LevelFile("othermod-3-0123456789abcdef", ".bsp.bz2"));
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        await _db.DisposeAsync();
    }

    Task<HttpResponseMessage> Get(string path) => _http.GetAsync(path);

    async Task AssertServedThen404(string path)
    {
        Assert.Equal(HttpStatusCode.OK, (await Get($"/maps/{Name}.bsp.bz2")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Get(path)).StatusCode);
    }

    [Fact]
    public async Task Fastdl_serves_a_levels_bz2_byte_for_byte()
    {
        var r = await Get($"/maps/{Name}.bsp.bz2");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var storage = _app.Services.GetRequiredService<LevelStorage>();
        Assert.Equal(await File.ReadAllBytesAsync(storage.LevelFile(Name, ".bsp.bz2")), await r.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Fastdl_tags_the_file_with_the_level_hash()
    {
        var r = await Get($"/maps/{Name}.bsp.bz2");
        Assert.Equal("\"0123456789abcdef\"", r.Headers.ETag!.Tag);
    }

    [Fact]
    public async Task Fastdl_answers_304_when_the_client_has_the_hash()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"/maps/{Name}.bsp.bz2");
        req.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"0123456789abcdef\""));
        Assert.Equal(HttpStatusCode.NotModified, (await _http.SendAsync(req)).StatusCode);
    }

    [Theory]
    [InlineData(".bsp")]
    [InlineData(".nav3d")]
    [InlineData(".map2d")]
    [InlineData(".yaml")]
    [InlineData(".bsp.bz2.writing")]
    [InlineData("")]
    public async Task Fastdl_404s_every_other_extension_of_the_same_level(string ext)
    {
        Assert.True(File.Exists(_app.Services.GetRequiredService<LevelStorage>().LevelFile(Name, ext == "" ? ".bsp" : ext == ".bsp.bz2.writing" ? ".bsp" : ext)));
        await AssertServedThen404($"/maps/{Name}{ext}");
    }

    [Theory]
    [InlineData("/maps/..%2Flevels%2F" + Name + ".bsp.bz2")]
    [InlineData("/maps/%2e%2e%2f" + Name + ".bsp.bz2")]
    [InlineData("/maps/..%2Finternal%2Fmaps%2F" + Name + ".bsp")]
    [InlineData("/maps/levels/" + Name + ".bsp.bz2")]
    [InlineData("/maps/" + Name + ".bsp.bz2/")]
    [InlineData("/levels/" + Name + ".bsp.bz2")]
    public async Task Fastdl_404s_traversal_attempts(string path) => await AssertServedThen404(path);

    [Fact]
    public async Task Fastdl_404s_another_mods_level_even_when_its_file_exists()
    {
        Assert.True(File.Exists(_app.Services.GetRequiredService<LevelStorage>().LevelFile("othermod-3-0123456789abcdef", ".bsp.bz2")));
        await AssertServedThen404("/maps/othermod-3-0123456789abcdef.bsp.bz2");
    }

    [Fact]
    public async Task Fastdl_404s_a_well_formed_name_with_no_level() =>
        await AssertServedThen404("/maps/descent-3-fedcba9876543210.bsp.bz2");

    [Theory]
    [InlineData(".bsp")]
    [InlineData(".nav3d")]
    [InlineData(".map2d")]
    public async Task Internal_serves_the_three_files_a_pod_needs(string ext)
    {
        var r = await Get($"/internal/maps/{Name}{ext}");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.True((await r.Content.ReadAsByteArrayAsync()).Length > 0);
    }

    [Theory]
    [InlineData(".yaml")]
    [InlineData(".bsp.bz2")]
    public async Task Internal_404s_what_a_pod_does_not_need(string ext)
    {
        Assert.Equal(HttpStatusCode.OK, (await Get($"/internal/maps/{Name}.bsp")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Get($"/internal/maps/{Name}{ext}")).StatusCode);
    }

    static MultipartFormDataContent Form(byte[] pack, string? json = "{}")
    {
        var form = new MultipartFormDataContent();
        if (json is not null) form.Add(new StringContent(json), MapEndpoints.LibraryJsonPart);
        form.Add(new ByteArrayContent(pack), MapEndpoints.PackPart, "crypt.roompack");
        return form;
    }

    [Fact]
    public async Task A_bakes_post_of_a_valid_pack_becomes_version_1_with_source_bake()
    {
        var r = await _http.PostAsync("/internal/packs/descent/crypt", Form(FakePack.Bytes("crypt", ["start"])));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var pack = (await _db.Read(tx => tx.Library.Packs("descent", "crypt"))).Single();
        Assert.Equal((1, PackState.Ready, "bake"), (pack.Version, pack.State, pack.Source));
    }

    [Fact]
    public async Task A_bakes_post_of_a_corrupt_pack_is_422_and_leaves_no_file()
    {
        Assert.Equal(HttpStatusCode.OK, (await _http.PostAsync("/internal/packs/descent/crypt", Form(FakePack.Bytes("crypt", ["start"])))).StatusCode);
        var r = await _http.PostAsync("/internal/packs/descent/crypt", Form("not a pack"u8.ToArray()));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Single(Directory.GetFiles(Path.Combine(_options.MapPool.MapsPath, "packs", "descent", "crypt"), "*.roompack"));
    }

    [Fact]
    public async Task The_pool_status_says_it_waits_for_the_rules_module()
    {
        var r = System.Text.Json.Nodes.JsonNode.Parse(await _http.GetStringAsync("/internal/pool"))!;
        Assert.Equal("waiting for the hub's rules module", r["message"]!.GetValue<string>());
    }
}
