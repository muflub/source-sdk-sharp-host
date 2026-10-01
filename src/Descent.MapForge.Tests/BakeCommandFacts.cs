using System.Net;
using Descent.MapForge;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.MapPool;
using SourceSharp.Host.Testing;

namespace Descent.MapForge.Tests;

/// <summary>§9.2 the bake entry point: argument handling, and the POST (with a fake and against the real endpoint).</summary>
public sealed class BakeCommandFacts
{
    static readonly string[] Minimal = ["--library", "crypt", "--out", "/out"];

    [Fact]
    public void The_minimal_arguments_parse_with_the_Job_defaults()
    {
        var a = BakeCommand.Parse(Minimal, out var error)!;
        Assert.Null(error);
        Assert.Equal(("crypt", "/library/crypt", "/content/tf", null, "/out/crypt.roompack", null, "descent", true),
            (a.Library, a.Source, a.Content, a.Cache, a.PackPath, a.Service, a.Mod, a.Light));
    }

    [Fact]
    public void Every_option_parses()
    {
        var a = BakeCommand.Parse(["--library", "caves", "--source", "/s", "--content", "/c", "--cache", "/cache", "--out", "/o",
            "--service", "http://svc:5000", "--mod", "descent", "--threads", "4", "--nolight"], out _)!;
        Assert.Equal(("/s", "/c", "/cache", new Uri("http://svc:5000"), 4, false), (a.Source, a.Content, a.Cache, a.Service, a.Threads, a.Light));
    }

    [Theory]
    [InlineData("--out", "/out")]
    [InlineData("--library", "crypt")]
    public void A_missing_required_argument_is_a_usage_error(params string[] args)
    {
        Assert.NotNull(BakeCommand.Parse(Minimal, out _));
        Assert.Null(BakeCommand.Parse(args, out var error));
        Assert.Contains("is required", error);
    }

    [Theory]
    [InlineData("--frobnicate", "x")]
    [InlineData("--library", "Crypt")]
    [InlineData("--library", "../crypt")]
    [InlineData("--service", "ftp://x")]
    [InlineData("--threads", "0")]
    [InlineData("--cache")]
    public void A_bad_argument_is_a_usage_error(params string[] extra)
    {
        Assert.NotNull(BakeCommand.Parse(Minimal, out _));
        Assert.Null(BakeCommand.Parse([.. Minimal, .. extra], out var error));
        Assert.StartsWith("bake: ", error);
    }

    [Fact]
    public async Task A_usage_error_exits_2_and_prints_the_usage()
    {
        var output = new StringWriter();
        Assert.Equal(BakeCommand.Usage, await new BakeCommand(new FakeMapCompiler()).RunAsync(["--library"], output));
        Assert.Contains("usage: bake", output.ToString());
    }

    sealed class Capture : HttpMessageHandler
    {
        public List<(Uri Url, List<string?> Parts, byte[] Pack)> Posts { get; } = [];
        public HttpStatusCode Answer { get; set; } = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var form = (MultipartFormDataContent)request.Content!;
            var parts = form.Select(p => p.Headers.ContentDisposition?.Name?.Trim('"')).ToList();
            var pack = await form.Last().ReadAsByteArrayAsync(ct);
            Posts.Add((request.RequestUri!, parts, pack));
            return new HttpResponseMessage(Answer) { Content = new StringContent("{}") };
        }
    }

    [Fact]
    public async Task A_bake_posts_library_json_then_the_pack_to_the_services_internal_endpoint()
    {
        var lib = Samples.CopyLibrary();
        var output = Samples.TempDir();
        var capture = new Capture();
        var rc = await new BakeCommand(new FakeMapCompiler(), capture).RunAsync(
            ["--library", "crypt", "--source", lib.Directory, "--out", output, "--service", "http://svc:5000/"], new StringWriter());
        Assert.Equal(BakeCommand.Ok, rc);
        var post = Assert.Single(capture.Posts);
        Assert.Equal("http://svc:5000/internal/packs/descent/crypt", post.Url.ToString());
        Assert.Equal(["library.json", "pack"], post.Parts);
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(output, "crypt.roompack")), post.Pack);
    }

    [Fact]
    public async Task Without_a_service_the_bake_posts_nothing()
    {
        var capture = new Capture();
        var rc = await new BakeCommand(new FakeMapCompiler(), capture).RunAsync(
            ["--library", "crypt", "--source", Samples.CopyLibrary().Directory, "--out", Samples.TempDir()], new StringWriter());
        Assert.Equal((BakeCommand.Ok, 0), (rc, capture.Posts.Count));
    }

    [Fact]
    public async Task A_rejected_pack_exits_3()
    {
        var capture = new Capture { Answer = HttpStatusCode.UnprocessableEntity };
        var rc = await new BakeCommand(new FakeMapCompiler(), capture).RunAsync(
            ["--library", "crypt", "--source", Samples.CopyLibrary().Directory, "--out", Samples.TempDir(), "--service", "http://svc"], new StringWriter());
        Assert.Equal(BakeCommand.Rejected, rc);
    }

    [Fact]
    public async Task A_real_bake_posted_to_the_real_endpoint_becomes_a_ready_version()
    {
        await using var db = new TestData();
        var options = new ServiceOptions { MapPool = { MapsPath = Samples.TempDir() } };
        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        b.Services.AddSingleton(options);
        b.Services.AddSingleton(new PackCatalog(db.Data, new PackValidator("descent", 15, new LinkSettings { Transitions = false }),
            new LevelStorage(options.MapPool), options));
        await using var app = b.Build();
        app.MapInternalPacks();
        await app.StartAsync();
        var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

        var lib = Samples.CopyLibrary();
        var log = new StringWriter();
        var rc = await new BakeCommand(new LinkedMapCompiler()).RunAsync(
            ["--library", "crypt", "--source", lib.Directory, "--content", lib.Directory, "--out", Samples.TempDir(), "--service", url, "--nolight", "--threads", "2"], log);
        await app.StopAsync();
        Assert.True(rc == BakeCommand.Ok, log.ToString());
        var pack = (await db.Read(tx => tx.Library.Packs("descent", "crypt"))).Single();
        Assert.Equal((PackState.Ready, "bake", 1), (pack.State, pack.Source, pack.Version));
    }

    [Fact]
    public async Task A_second_bake_with_a_cache_reuses_every_room()
    {
        var lib = Samples.CopyLibrary();
        var cache = Samples.TempDir();
        var c = new LinkedMapCompiler();
        var first = await c.BakeAsync(lib, new BakeSettings(lib.Directory, cache, Path.Combine(Samples.TempDir(), "a.roompack"), 2, Light: false));
        var second = await c.BakeAsync(lib, new BakeSettings(lib.Directory, cache, Path.Combine(Samples.TempDir(), "b.roompack"), 2, Light: false));
        Assert.Equal((5, 0), (first.Compiled, first.Reused));
        Assert.Equal((0, 5), (second.Compiled, second.Reused));
        Assert.Equal(await File.ReadAllBytesAsync(first.PackPath), await File.ReadAllBytesAsync(second.PackPath));
    }
}
