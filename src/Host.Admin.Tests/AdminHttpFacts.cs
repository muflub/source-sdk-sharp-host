using System.Net;
using System.Text.RegularExpressions;
using Descent.Service;
using Microsoft.Extensions.DependencyInjection;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Testing;

namespace SourceSharp.Host.Admin.Tests;

/// <summary>The admin's plain HTTP endpoints on the real service host: CSV export, the pack upload, the devapi proxy, downloads.</summary>
public sealed partial class AdminHttpFacts : IAsyncLifetime
{
    AdminServiceHost _host = null!;
    AdminWorld W => _host.World;
    public async Task InitializeAsync() => _host = await AdminServiceHost.StartAsync();
    public async Task DisposeAsync() => await _host.DisposeAsync();
    HttpClient Admin() => _host.Http(ListenerRole.Admin);

    [Fact]
    public async Task Csv_export_writes_a_header_and_a_quoted_row()
    {
        await W.D.Write(tx => tx.Audit.Write("account.ban", "7656", new { note = "a, \"b\"" }, null, "admin@localhost"));
        using var http = Admin();
        var csv = await http.GetStringAsync("/admin/export/audit.csv");
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("id,at,actor,action,target,before,after", lines[0]);
        Assert.Contains("admin@localhost,account.ban,7656,\"{\"\"note\"\":\"\"a, \\u0022b\\u0022\"\"}\",", lines[1]);
    }

    [Fact]
    public async Task Csv_export_applies_the_pages_filters()
    {
        await W.D.Write(async tx => { await tx.Audit.Write("account.ban", "a", null, null, "x"); await tx.Audit.Write("pack.retire", "b", null, null, "x"); });
        using var http = Admin();
        var csv = await http.GetStringAsync("/admin/export/audit.csv?action=pack.retire");
        Assert.Equal(2, csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public async Task Csv_export_of_an_unknown_table_is_404()
    {
        using var http = Admin();
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/admin/export/secrets.csv")).StatusCode);
    }

    static MultipartFormDataContent Form(string? token, byte[] pack)
    {
        var form = new MultipartFormDataContent();
        if (token is not null) form.Add(new StringContent(token), "__RequestVerificationToken");
        form.Add(new StringContent("descent"), "mod");
        form.Add(new StringContent("crypt"), "library");
        form.Add(new StringContent("{}"), "libraryJson");
        form.Add(new ByteArrayContent(pack), "pack", "crypt.roompack");
        return form;
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex TokenField();

    [Fact]
    public async Task An_upload_without_the_antiforgery_token_is_refused()
    {
        using var http = Admin();
        var r = await http.PostAsync("/admin/packs/upload", Form(null, FakePack.Bytes("crypt", ["start", "stairs"])));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Empty(await W.D.Read(tx => tx.Library.Packs("descent")));
    }

    [Fact]
    public async Task An_upload_with_a_forged_antiforgery_token_is_refused()
    {
        using var http = Admin();
        var r = await http.PostAsync("/admin/packs/upload", Form("forged", FakePack.Bytes("crypt", ["start", "stairs"])));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Empty(await W.D.Read(tx => tx.Library.Packs("descent")));
    }

    [Fact]
    public async Task An_upload_with_the_pages_token_streams_the_pack_into_a_new_version()
    {
        var cookies = new CookieContainer();
        var port = new Uri(Admin().BaseAddress!.ToString()).Port;
        using var http = new HttpClient(new HttpClientHandler { CookieContainer = cookies, AllowAutoRedirect = false }) { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var page = await http.GetStringAsync("/admin/packs");
        var token = WebUtility.HtmlDecode(TokenField().Match(page).Groups[1].Value);
        Assert.NotEmpty(token);

        var r = await http.PostAsync("/admin/packs/upload", Form(token, FakePack.Bytes("crypt", ["start", "stairs"])));

        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Contains("ok%3A%20accepted", r.Headers.Location!.ToString());
        var pack = Assert.Single(await W.D.Read(tx => tx.Library.Packs("descent")));
        Assert.Equal((PackState.Ready, 1), (pack.State, pack.Version));
        Assert.Equal("admin@localhost", Assert.Single(await W.Audit("pack.upload")).Actor);
    }

    [Fact]
    public async Task The_devapi_proxy_answers_501_while_the_port_is_not_configured()
    {
        await W.Instance("lvl-1");
        using var http = Admin();
        var r = await http.GetAsync("/admin/instances/lvl-1/devapi/");
        Assert.Equal(HttpStatusCode.NotImplemented, r.StatusCode);
        Assert.Contains("DevApiPort is not configured", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_devapi_proxy_answers_501_with_the_reason_when_the_pod_is_not_reachable()
    {
        await W.D.Write(tx => tx.Instances.Add(new InstanceRecord("lvl-1", InstanceKind.Level, InstanceState.Live, 3, null, null, "descent-lvl-1", "uid",
            "127.0.0.1", 27015, "hash", null, null, "fake", 1, tx.Now, null, null, null, null, null, null, null, 0, 27015)));
        // The discard port: below the ephemeral range, so no port-0 listener or outgoing connection in
        // the suite can take it (a picked-and-released ephemeral port could be taken meanwhile and answer).
        const int port = 9;
        using (var probe = new System.Net.Sockets.TcpClient())
            Assert.Equal(System.Net.Sockets.SocketError.ConnectionRefused,
                Assert.Throws<System.Net.Sockets.SocketException>(() => probe.Connect(IPAddress.Loopback, port)).SocketErrorCode);
        _host.App.Services.GetRequiredService<AdminUiOptions>().DevApiPort = port;
        using var http = Admin();
        var r = await http.GetAsync("/admin/instances/lvl-1/devapi/index.html");
        Assert.Equal(HttpStatusCode.NotImplemented, r.StatusCode);
        Assert.Contains("is not reachable", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_devapi_proxy_forwards_a_get_to_the_pods_port()
    {
        await W.D.Write(tx => tx.Instances.Add(new InstanceRecord("lvl-1", InstanceKind.Level, InstanceState.Live, 3, null, null, "descent-lvl-1", "uid",
            "127.0.0.1", 27015, "hash", null, null, "fake", 1, tx.Now, null, null, null, null, null, null, null, 0, 27015)));
        // The "pod" is this service's own admin listener: /admin/audit through the proxy is the page itself.
        _host.App.Services.GetRequiredService<AdminUiOptions>().DevApiPort = new Uri(Admin().BaseAddress!.ToString()).Port;
        using var http = Admin();
        var r = await http.GetAsync("/admin/instances/lvl-1/devapi/admin/audit");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("<h1>Audit log</h1>", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_devapi_proxy_is_404_for_an_unknown_instance()
    {
        using var http = Admin();
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/admin/instances/nope/devapi/")).StatusCode);
    }

    [Fact]
    public async Task A_backup_downloads_its_bytes()
    {
        var b = await W.Backups.RunNow();
        using var http = Admin();
        Assert.Equal(W.Backups.Bytes, await http.GetByteArrayAsync($"/admin/backups/{b.Name}"));
    }

    [Fact]
    public async Task A_level_file_downloads_and_the_fastdl_only_extension_is_refused()
    {
        await W.D.Write(tx => tx.Levels.Add(new LevelRecord("aa", 1, 2, "crypt", 1, 0, LevelState.Ready, tx.Now, null, 10, null, "fake")));
        var storage = W.Catalog.Storage;
        Directory.CreateDirectory(storage.LevelsDirectory);
        await File.WriteAllTextAsync(storage.LevelFile(MapIdentity.MapName("descent", 2, "aa"), ".nav3d"), "nav");
        using var http = Admin();
        Assert.Equal("nav", await http.GetStringAsync("/admin/levels/aa/file.nav3d"));
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/admin/levels/aa/file.exe")).StatusCode);
    }
}
