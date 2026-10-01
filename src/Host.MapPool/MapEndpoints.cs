using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using SourceSharp.Host.Abstractions;

namespace SourceSharp.Host.MapPool;

/// <summary>
/// The pool's HTTP surface (plan Q18, §9.2). The composition root maps each on its listener:
/// <see cref="MapFastDl"/> on the public fastdl listener, the other two on the internal one.
/// Every handler 404s anything it does not serve, so the fastdl listener serves
/// <c>*.bsp.bz2</c> of this mod's levels and nothing else.
/// </summary>
public static class MapEndpoints
{
    /// <summary>The body part names of <c>POST /internal/packs/{mod}/{library}</c>, in the order they must come.</summary>
    public const string LibraryJsonPart = "library.json", PackPart = "pack";

    /// <summary>
    /// <c>GET /maps/&lt;mod&gt;-&lt;depth&gt;-&lt;hash&gt;.bsp.bz2</c>: the one public file, ETag = the level hash.
    /// Any other file name, extension or mod is 404.
    /// </summary>
    public static RouteHandlerBuilder MapFastDl(this IEndpointRouteBuilder app) =>
        app.MapGet("/maps/{file}", (string file, HttpContext http) =>
        {
            var storage = http.RequestServices.GetRequiredService<LevelStorage>();
            var mod = http.RequestServices.GetRequiredService<ServiceOptions>().Mod.Name;
            const string ext = ".bsp.bz2";
            // The path exactly as routed: no trailing slash, no encoded separators.
            if (http.Request.Path.Value != "/maps/" + file || !file.EndsWith(ext, StringComparison.Ordinal)) return Results.NotFound();
            var name = file[..^ext.Length];
            if (!LevelStorage.TryParseMapName(name, out var m, out _, out var hash) || m != mod) return Results.NotFound();
            var path = storage.LevelFile(name, ext);
            if (!File.Exists(path)) return Results.NotFound();
            return Results.File(path, "application/x-bzip2", fileDownloadName: null, lastModified: null,
                entityTag: new EntityTagHeaderValue($"\"{hash}\""), enableRangeProcessing: true);
        });

    /// <summary>
    /// <c>GET /internal/maps/&lt;mod&gt;-&lt;depth&gt;-&lt;hash&gt;.{bsp,nav3d,map2d}</c>: what a game pod's init
    /// container fetches. Never published (Q18): map it on the internal listener only.
    /// </summary>
    public static RouteHandlerBuilder MapInternalMaps(this IEndpointRouteBuilder app) =>
        app.MapGet("/internal/maps/{file}", (string file, HttpContext http) =>
        {
            var storage = http.RequestServices.GetRequiredService<LevelStorage>();
            var mod = http.RequestServices.GetRequiredService<ServiceOptions>().Mod.Name;
            var ext = LevelStorage.InternalExtensions.FirstOrDefault(e => file.EndsWith(e, StringComparison.Ordinal));
            if (ext is null || http.Request.Path.Value != "/internal/maps/" + file) return Results.NotFound();
            var name = file[..^ext.Length];
            if (!LevelStorage.TryParseMapName(name, out var m, out _, out var hash) || m != mod) return Results.NotFound();
            var path = storage.LevelFile(name, ext);
            if (!File.Exists(path)) return Results.NotFound();
            return Results.File(path, "application/octet-stream", entityTag: new EntityTagHeaderValue($"\"{hash}\""), enableRangeProcessing: true);
        });

    /// <summary>
    /// <c>POST /internal/packs/{mod}/{library}</c>: the bake Job's pack (§9.2), multipart with an
    /// optional <c>library.json</c> part first and the <c>pack</c> part, streamed, validated exactly
    /// as an admin upload. 200 with the version when accepted, 422 with the problems when not.
    /// </summary>
    public static RouteHandlerBuilder MapInternalPacks(this IEndpointRouteBuilder app, string source = PackCatalog.SourceBake) =>
        app.MapPost("/internal/packs/{mod}/{library}", async (string mod, string library, HttpContext http) =>
        {
            var catalog = http.RequestServices.GetRequiredService<PackCatalog>();
            var options = http.RequestServices.GetRequiredService<ServiceOptions>();
            if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size) size.MaxRequestBodySize = options.Admin.MaxUploadBytes + 1024 * 1024;
            var boundary = HeaderUtilities.RemoveQuotes(MediaTypeHeaderValue.Parse(http.Request.ContentType ?? "").Boundary).Value;
            if (string.IsNullOrEmpty(boundary)) return Results.BadRequest(new { error = "multipart/form-data with a library.json part and a pack part" });

            var reader = new MultipartReader(boundary, http.Request.Body);
            string? json = null;
            MultipartSection? section;
            while ((section = await reader.ReadNextSectionAsync(http.RequestAborted)) is not null)
            {
                var name = ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var cd) ? HeaderUtilities.RemoveQuotes(cd.Name).Value : null;
                if (name == LibraryJsonPart)
                {
                    using var sr = new StreamReader(section.Body);
                    json = await sr.ReadToEndAsync(http.RequestAborted);
                }
                else if (name == PackPart)
                {
                    try
                    {
                        var r = await catalog.UploadAsync(mod, library, section.Body, json, http.Request.Query["note"], source, $"{source}@internal", http.RequestAborted);
                        var body = new { accepted = r.Accepted, id = r.Pack.Id, version = r.Pack.Version, sha256 = r.Pack.Sha256, problems = r.Inspection.Problems };
                        return r.Accepted ? Results.Ok(body) : Results.UnprocessableEntity(body);
                    }
                    catch (HostRefusal e)
                    {
                        return Results.Json(new { accepted = false, error = e.Reason, message = e.Message },
                            statusCode: e.Code == RefusalCode.ResourceExhausted ? StatusCodes.Status413PayloadTooLarge : StatusCodes.Status400BadRequest);
                    }
                }
            }
            return Results.BadRequest(new { error = "no pack part" });
        });

    /// <summary>What the Dashboard reads (§11): <c>GET /internal/pool</c> as JSON.</summary>
    public static RouteHandlerBuilder MapPoolStatus(this IEndpointRouteBuilder app) =>
        app.MapGet("/internal/pool", (HttpContext http) =>
            Results.Json(http.RequestServices.GetRequiredService<IMapPool>().Status, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
}
