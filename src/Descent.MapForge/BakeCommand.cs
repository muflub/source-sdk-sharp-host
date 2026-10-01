using System.Net.Http.Headers;
using SourceSharp.Host.Abstractions;

namespace Descent.MapForge;

/// <summary>The bake's arguments (plan §9.2).</summary>
/// <param name="Library">The tileset: the library key, the pack's namespace.</param>
/// <param name="Source">The library's folder (rooms.vmf + library.json); default <c>/library/&lt;library&gt;</c>.</param>
/// <param name="Content">The game folder holding gameinfo.txt (the content PVC); default <c>/content/tf</c>.</param>
/// <param name="Cache">The tools' incremental store folder (the bake-cache PVC), or null to compile every room.</param>
/// <param name="Out">Where the pack is written: <c>&lt;out&gt;/&lt;library&gt;.roompack</c>.</param>
/// <param name="Service">The service's internal base URL the pack is POSTed to; null only bakes.</param>
public sealed record BakeArgs(string Library, string Source, string Content, string? Cache, string Out, Uri? Service, string Mod, int? Threads, bool Light)
{
    public string PackPath => Path.Combine(Out, Library + ".roompack");
}

/// <summary>
/// <c>Descent.Service bake --library &lt;tileset&gt; --cache /cache --out /out --service &lt;url&gt;</c>: what
/// the bake Job runs (plan §9.2). Mounts the content, bakes with the tools' room compiler and
/// incremental store, then POSTs the pack (library.json first) to
/// <c>/internal/packs/&lt;mod&gt;/&lt;library&gt;</c>, which validates it exactly as an upload.
/// </summary>
public sealed class BakeCommand(IMapCompiler compiler, HttpMessageHandler? http = null)
{
    public const int Ok = 0, BakeFailed = 1, Usage = 2, Rejected = 3;

    public const string UsageText =
        "usage: bake --library <tileset> [--source <dir>] [--content <game dir>] [--cache <dir>] --out <dir> [--service <url>] [--mod <name>] [--threads <n>] [--nolight]";

    /// <summary>Parses the arguments after <c>bake</c>; null with <paramref name="error"/> set when they are not a bake.</summary>
    public static BakeArgs? Parse(IReadOnlyList<string> args, out string? error)
    {
        error = null;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var light = true;
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a == "--nolight") { light = false; continue; }
            if (a is not ("--library" or "--source" or "--content" or "--cache" or "--out" or "--service" or "--mod" or "--threads"))
            {
                error = $"bake: unknown argument '{a}'";
                return null;
            }
            if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                error = $"bake: {a} needs a value";
                return null;
            }
            values[a[2..]] = args[++i];
        }
        foreach (var required in new[] { "library", "out" })
            if (!values.ContainsKey(required)) { error = $"bake: --{required} is required"; return null; }
        var library = values["library"];
        if (library.Length == 0 || library.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '_' or '-')))
        {
            error = $"bake: --library '{library}' is not a library key (lower-case letters, digits, '_' and '-')";
            return null;
        }
        Uri? service = null;
        if (values.TryGetValue("service", out var s) && (!Uri.TryCreate(s, UriKind.Absolute, out service) || service.Scheme is not ("http" or "https")))
        {
            error = $"bake: --service '{s}' is not an http(s) URL";
            return null;
        }
        int? threads = null;
        if (values.TryGetValue("threads", out var t))
        {
            if (!int.TryParse(t, out var n) || n < 1) { error = $"bake: --threads '{t}' is not a positive number"; return null; }
            threads = n;
        }
        return new BakeArgs(library, values.GetValueOrDefault("source") ?? $"/library/{library}", values.GetValueOrDefault("content") ?? "/content/tf",
            values.GetValueOrDefault("cache"), values["out"], service, values.GetValueOrDefault("mod") ?? "descent", threads, light);
    }

    public async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter output, CancellationToken ct = default)
    {
        var parsed = Parse(args, out var error);
        if (parsed is null)
        {
            await output.WriteLineAsync(error);
            await output.WriteLineAsync(UsageText);
            return Usage;
        }
        return await RunAsync(parsed, output, ct);
    }

    public async Task<int> RunAsync(BakeArgs a, TextWriter output, CancellationToken ct = default)
    {
        var source = new LibrarySource(a.Mod, a.Library, a.Source);
        BakeResult result;
        try
        {
            result = await compiler.BakeAsync(source, new BakeSettings(a.Content, a.Cache, a.PackPath, a.Threads, a.Light), ct);
        }
        catch (Exception e) when (e is MapCompileFailure or IOException or InvalidDataException)
        {
            await output.WriteLineAsync($"bake: {e.Message}");
            return BakeFailed;
        }
        await output.WriteAsync(result.Log);
        await output.WriteLineAsync($"bake: {result.Rooms} rooms, {result.Compiled} compiled, {result.Reused} reused, {result.Failed} failed in {result.Elapsed.TotalSeconds:F1} s");
        if (!result.Ok) return BakeFailed;
        if (a.Service is null) return Ok;

        using var client = http is null ? new HttpClient() : new HttpClient(http, disposeHandler: false);
        client.Timeout = Timeout.InfiniteTimeSpan;
        using var form = new MultipartFormDataContent();
        if (File.Exists(source.Manifest))
            form.Add(new StringContent(await File.ReadAllTextAsync(source.Manifest, ct)), "library.json");
        var pack = new StreamContent(File.OpenRead(result.PackPath));
        pack.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(pack, "pack", Path.GetFileName(result.PackPath));
        var url = new Uri($"{a.Service.ToString().TrimEnd('/')}/internal/packs/{Uri.EscapeDataString(a.Mod)}/{Uri.EscapeDataString(a.Library)}");
        try
        {
            using var response = await client.PostAsync(url, form, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            await output.WriteLineAsync($"bake: POST {url} -> {(int)response.StatusCode} {body}");
            return response.IsSuccessStatusCode ? Ok : Rejected;
        }
        catch (HttpRequestException e)
        {
            await output.WriteLineAsync($"bake: POST {url} failed: {e.Message}");
            return Rejected;
        }
    }
}
