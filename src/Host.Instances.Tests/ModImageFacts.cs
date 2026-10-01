using System.Text.RegularExpressions;
using Microsoft.Extensions.Time.Testing;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Instances;
using SourceSharp.Host.Testing;

namespace SourceSharp.Host.Instances.Tests;

public class ModImageFacts
{
    static readonly Dictionary<string, string> Descent = new() { ["org.sourcesharp.mod"] = "descent" };

    static OciModImageInspector Inspector() => new(new HttpClient(), []);

    static ImageRef Image(FakeRegistry r, string tag = "1", string? digest = null) =>
        new() { Registry = r.Host, Name = "descent-mod", Tag = tag, Digest = digest };

    [Fact]
    public async Task Label_is_read_from_a_single_manifest()
    {
        await using var r = new FakeRegistry();
        r.Push("descent-mod", "1", Descent);
        Assert.Equal("descent", await Inspector().ModLabel(Image(r)));
    }

    [Fact]
    public async Task Label_is_read_through_an_image_index_choosing_linux_amd64()
    {
        await using var r = new FakeRegistry();
        r.Push("descent-mod", "1", Descent, asIndex: true);
        Assert.Equal("descent", await Inspector().ModLabel(Image(r)));
    }

    [Fact]
    public async Task Label_is_read_by_digest()
    {
        await using var r = new FakeRegistry();
        var digest = r.Push("descent-mod", "1", Descent);
        Assert.Equal("descent", await Inspector().ModLabel(Image(r, tag: "nope", digest: digest)));
        Assert.Contains($"/v2/descent-mod/manifests/{digest}", r.Requests);
    }

    [Fact]
    public async Task Missing_label_reads_as_null()
    {
        await using var r = new FakeRegistry();
        r.Push("descent-mod", "1", new Dictionary<string, string> { ["other"] = "x" });
        Assert.Null(await Inspector().ModLabel(Image(r)));
    }

    [Fact]
    public async Task Anonymous_bearer_challenge_is_answered()
    {
        await using var r = new FakeRegistry { RequireToken = true };
        r.Push("descent-mod", "1", Descent);
        Assert.Equal("descent", await Inspector().ModLabel(Image(r)));
        Assert.Contains("/token", r.Requests);
    }

    [Fact]
    public async Task Unknown_tag_throws_rather_than_passing()
    {
        await using var r = new FakeRegistry();
        await Assert.ThrowsAsync<ModImageException>(() => Inspector().ModLabel(Image(r, tag: "missing")));
    }

    [Fact]
    public async Task Config_blob_that_does_not_match_its_digest_is_refused()
    {
        await using var r = new FakeRegistry();
        r.Push("descent-mod", "1", Descent, corruptConfig: """{"config":{"Labels":{"org.sourcesharp.mod":"descent"}}}"""u8.ToArray());
        await Assert.ThrowsAsync<ModImageException>(() => Inspector().ModLabel(Image(r)));
    }

    [Theory]
    [InlineData("localhost:5000", "http://localhost:5000")]
    [InlineData("127.0.0.1:5000", "http://127.0.0.1:5000")]
    [InlineData("k3d-registry.localhost:5000", "http://k3d-registry.localhost:5000")]
    [InlineData("ghcr.io", "https://ghcr.io")]
    public void Scheme_is_http_only_for_local_registries(string registry, string expected)
    {
        Assert.Equal(expected, OciModImageInspector.Locate(new ImageRef { Registry = registry, Name = "m" }, []).BaseUrl);
    }

    [Fact]
    public void Configured_insecure_registry_is_reached_over_http()
    {
        Assert.Equal("http://registry.lan:5000", OciModImageInspector.Locate(new ImageRef { Registry = "registry.lan:5000", Name = "m" }, ["registry.lan:5000"]).BaseUrl);
    }

    [Fact]
    public void Docker_hub_short_names_get_the_library_prefix()
    {
        Assert.Equal(("https://registry-1.docker.io", "library/busybox"), OciModImageInspector.Locate(new ImageRef { Name = "busybox" }, []));
    }

    // ---- the guard ----

    sealed class StubInspector(Func<string?> label) : IModImageInspector
    {
        public int Calls;
        public Task<string?> ModLabel(ImageRef image, CancellationToken ct = default) { Calls++; return Task.FromResult(label()); }
    }

    [Fact]
    public async Task Guard_accepts_a_matching_label()
    {
        var guard = new ModImageGuard(new StubInspector(() => "descent"), new FakeTimeProvider());
        Assert.Null(await guard.Refusal(new ImageRef { Name = "m" }, "descent", default));
    }

    [Fact]
    public async Task Guard_refuses_another_mods_label()
    {
        var guard = new ModImageGuard(new StubInspector(() => "othermod"), new FakeTimeProvider());
        Assert.Contains("'othermod'", await guard.Refusal(new ImageRef { Name = "m" }, "descent", default));
    }

    [Fact]
    public async Task Guard_refuses_an_unlabelled_image()
    {
        var guard = new ModImageGuard(new StubInspector(() => null), new FakeTimeProvider());
        Assert.Contains("no org.sourcesharp.mod label", await guard.Refusal(new ImageRef { Name = "m" }, "descent", default));
    }

    [Fact]
    public async Task Guard_refuses_when_the_registry_cannot_answer()
    {
        var guard = new ModImageGuard(new StubInspector(() => throw new ModImageException("down")), new FakeTimeProvider());
        Assert.Contains("could not be inspected", await guard.Refusal(new ImageRef { Name = "m" }, "descent", default));
    }

    [Fact]
    public async Task Guard_caches_an_accepted_image_for_a_minute()
    {
        var clock = new FakeTimeProvider();
        var stub = new StubInspector(() => "descent");
        var guard = new ModImageGuard(stub, clock);
        await guard.Refusal(new ImageRef { Name = "m" }, "descent", default);
        await guard.Refusal(new ImageRef { Name = "m" }, "descent", default);
        Assert.Equal(1, stub.Calls);
        clock.Advance(ModImageGuard.CacheFor);
        await guard.Refusal(new ImageRef { Name = "m" }, "descent", default);
        Assert.Equal(2, stub.Calls);
    }

    // ---- the contract against a tarball listing ----

    static string[] Listing(string name) => File.ReadAllLines(RepoFiles.Path_("src", "Host.Instances.Tests", "Fixtures", name));

    [Fact]
    public void Good_listing_conforms()
    {
        var listing = Listing("mod-image-good.txt");
        Assert.NotEmpty(listing);
        Assert.Empty(ModImageContract.Check(listing, "descent"));
    }

    [Fact]
    public void Bad_listing_reports_each_missing_and_forbidden_path()
    {
        Assert.Equal(
            ["missing /game/bin/dotnet/", "missing /game/mods/descent/", "missing /bin/cp or /usr/bin/cp", "forbidden /game/bin/srcds_linux64"],
            ModImageContract.Check(Listing("mod-image-bad.txt"), "descent"));
    }

    [Fact]
    public void Empty_directory_entry_does_not_satisfy_a_required_directory()
    {
        var listing = Listing("mod-image-good.txt").Where(l => !l.StartsWith("./game/bin/managed/", StringComparison.Ordinal) || l == "./game/bin/managed/").ToList();
        Assert.Contains("./game/bin/managed/", listing);
        Assert.Equal(["missing /game/bin/managed/"], ModImageContract.Check(listing, "descent"));
    }

    [Fact]
    public void Checker_requires_every_path_the_contract_document_names()
    {
        // The "What the image must contain" table of docs/mod-image.md: its backticked absolute paths.
        var doc = File.ReadAllText(RepoFiles.Path_("docs", "mod-image.md"));
        var table = doc[doc.IndexOf("## What the image must contain", StringComparison.Ordinal)..doc.IndexOf("## What it must not contain", StringComparison.Ordinal)];
        var documented = table.Split('\n').Where(l => l.StartsWith("| `/", StringComparison.Ordinal))
            .SelectMany(l => Regex.Matches(l.Split('|')[1], "`(/[^`]+)`").Select(m => m.Groups[1].Value.Replace("<Mod.Name>", "descent")))
            .ToList();
        Assert.Equal(6, documented.Count);
        Assert.Equal(documented.Order(), ModImageContract.Required("descent").Order());
    }
}
