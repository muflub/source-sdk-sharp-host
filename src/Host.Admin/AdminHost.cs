using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Admin.Components;
using SourceSharp.Host.MapPool;

namespace SourceSharp.Host.Admin;

/// <summary>Every endpoint MapHostAdmin mapped, as one builder, so the composition root can restrict them all at once.</summary>
public sealed class AdminEndpoints(IReadOnlyList<IEndpointConventionBuilder> builders) : IEndpointConventionBuilder
{
    public IReadOnlyList<IEndpointConventionBuilder> Builders => builders;
    public void Add(Action<EndpointBuilder> convention) { foreach (var b in builders) b.Add(convention); }
    public void Finally(Action<EndpointBuilder> convention) { foreach (var b in builders) b.Finally(convention); }
}

/// <summary>
/// The admin UI's wiring (plan §11). The composition root registers what it owns first
/// (<see cref="IHostData"/>, <see cref="IRulesProvider"/>, <see cref="ServiceOptions"/> or
/// <c>IOptions&lt;ServiceOptions&gt;</c>, <see cref="TimeProvider"/>) and, as they exist, the
/// optional collaborators (<see cref="IInstanceLifecycle"/>, <see cref="IInstanceHost"/>,
/// <see cref="IMapPool"/>, the pool's <see cref="PackCatalog"/> / <see cref="MapPoolWorker"/> /
/// <see cref="PoolSignal"/>, <see cref="IAdminLedger"/>, <see cref="IAdminGateway"/>,
/// <see cref="IAdminModules"/>, <see cref="IAdminBackups"/>, <see cref="IHealthReporter"/>s).
/// Then <c>services.AddHostAdmin(configuration)</c> and
/// <c>app.MapHostAdmin().On(ListenerRole.Admin)</c>.
/// </summary>
public static class AdminHost
{
    /// <summary>Pages live under /admin; Blazor's own endpoints (/_blazor, /_framework) at the root of the admin listener.</summary>
    public const string Root = "/admin";

    public static IServiceCollection AddHostAdmin(this IServiceCollection services, IConfiguration? configuration = null)
    {
        var ui = new AdminUiOptions();
        configuration?.GetSection("AdminUi").Bind(ui);
        services.AddSingleton(ui);
        services.AddRazorComponents().AddInteractiveServerComponents();
        services.AddHttpClient(DevApiProxy.ClientName, c => c.Timeout = TimeSpan.FromSeconds(10));
        services.AddSingleton(sp => new AdminCollaborators(
            sp.GetService<IInstanceLifecycle>(), sp.GetService<IInstanceHost>(), sp.GetService<PackCatalog>(), sp.GetService<MapPoolWorker>(),
            sp.GetService<PoolSignal>(), sp.GetService<IAdminLedger>(), sp.GetService<IAdminGateway>(), sp.GetService<IAdminModules>(),
            sp.GetService<IAdminBackups>()));
        services.AddSingleton<IAdminActions>(sp => new AdminActions(sp.GetRequiredService<IHostData>(), sp.GetRequiredService<IRulesProvider>(),
            OptionsOf(sp), sp.GetRequiredService<AdminCollaborators>()));
        return services;
    }

    /// <summary>ServiceOptions as registered, or bound through IOptions.</summary>
    public static ServiceOptions OptionsOf(IServiceProvider sp) =>
        sp.GetService<ServiceOptions>() ?? sp.GetService<IOptions<ServiceOptions>>()?.Value ?? new ServiceOptions();

    public static AdminEndpoints MapHostAdmin(this IEndpointRouteBuilder app)
    {
        var list = new List<IEndpointConventionBuilder>();

        // Antiforgery on the component endpoints is off: they only render (GET); the one
        // form post, the pack upload, validates its token itself, so the service needs no
        // UseAntiforgery in its pipeline.
        list.Add(app.MapRazorComponents<App>().AddInteractiveServerRenderMode().DisableAntiforgery());
        list.Add(app.MapGet("/_framework/blazor.web.js", BlazorScript));

        list.Add(app.Map(Root + "/instances/{id}/devapi/{**rest}", DevApiProxy.Handle));
        list.Add(app.MapGet(Root + "/export/{table}.csv", AdminCsv.Handle));
        list.Add(app.MapPost(Root + "/packs/upload", UploadPack).DisableAntiforgery());
        list.Add(app.MapGet(Root + "/backups/{name}", DownloadBackup));
        list.Add(app.MapGet(Root + "/levels/{hash}/file{ext}", DownloadLevel));
        return new AdminEndpoints(list);
    }

    /// <summary>The Blazor script embedded at build time from the framework's assets pack (see Host.Admin.csproj).</summary>
    static IResult BlazorScript() =>
        typeof(AdminHost).Assembly.GetManifestResourceStream("admin/blazor.web.js") is { } s
            ? Results.Stream(s, "text/javascript")
            : Results.Problem("blazor.web.js was not embedded in this build", statusCode: StatusCodes.Status500InternalServerError);

    // ---- /admin/packs/upload: multipart streamed, never buffered (a pack may be 2 GiB)

    /// <summary>
    /// The form's fields in order: the antiforgery token first (AntiforgeryToken renders first),
    /// then mod, library, note, library.json, then the pack. The token is moved into the
    /// antiforgery header before validating, so validation never reads (and buffers) the form:
    /// the pack streams from the request straight to PackCatalog.
    /// </summary>
    static async Task<IResult> UploadPack(HttpContext http, IAntiforgery antiforgery, IAdminActions actions, IServiceProvider sp)
    {
        var options = OptionsOf(sp);
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = options.Admin.MaxUploadBytes + 1024 * 1024;
        if (!MediaTypeHeaderValue.TryParse(http.Request.ContentType, out var type) || HeaderUtilities.RemoveQuotes(type.Boundary).Value is not { Length: > 0 } boundary)
            return Results.BadRequest("expected multipart/form-data");

        var fields = new Dictionary<string, string>();
        var reader = new MultipartReader(boundary, http.Request.Body);
        var validated = false;
        AdminResult? result = null;
        while (await reader.ReadNextSectionAsync(http.RequestAborted) is { } section)
        {
            if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var cd)) continue;
            var name = HeaderUtilities.RemoveQuotes(cd.Name).Value ?? "";
            if (!validated)
            {
                if (name != AntiforgeryField) return Results.BadRequest("antiforgery token missing: it must be the form's first field");
                http.Request.Headers[AntiforgeryHeader] = await ReadField(section, http.RequestAborted);
                try { await antiforgery.ValidateRequestAsync(http); }
                catch (AntiforgeryValidationException) { return Results.BadRequest("antiforgery token invalid"); }
                validated = true;
                continue;
            }
            if (name == "pack" && cd.IsFileDisposition())
            {
                result = await actions.UploadPack(Field(fields, "mod") ?? options.Mod.Name, Field(fields, "library") ?? "",
                    section.Body, Field(fields, "libraryJson"), Field(fields, "note"), http.RequestAborted);
                break;
            }
            fields[name] = await ReadField(section, http.RequestAborted);
        }
        if (!validated) return Results.BadRequest("antiforgery token missing");
        result ??= AdminResult.Refused("no_file", "the form carried no pack file (it must come after the other fields)");
        return Results.Redirect($"{Root}/packs?result={Uri.EscapeDataString((result.Ok ? "ok: " : "refused: ") + result.Message)}");
    }

    const string AntiforgeryField = "__RequestVerificationToken", AntiforgeryHeader = "RequestVerificationToken";

    static async Task<string> ReadField(MultipartSection section, CancellationToken ct)
    {
        using var sr = new StreamReader(section.Body);
        var buffer = new char[1024 * 1024];
        var n = await sr.ReadBlockAsync(buffer, ct);
        return new string(buffer, 0, n);
    }

    static string? Field(Dictionary<string, string> f, string key) => f.TryGetValue(key, out var v) && v.Length > 0 ? v : null;

    static IResult DownloadBackup(string name, IServiceProvider sp)
    {
        if (sp.GetService<IAdminBackups>() is not { } backups) return Results.StatusCode(StatusCodes.Status501NotImplemented);
        if (name.Contains('/') || name.Contains("..") || backups.Open(name) is not { } s) return Results.NotFound();
        return Results.Stream(s, "application/octet-stream", name);
    }

    static readonly string[] Downloadable = [".bsp", ".yaml", ".nav3d", ".map2d"];

    static async Task<IResult> DownloadLevel(string hash, string ext, IHostData data, IServiceProvider sp)
    {
        if (!Downloadable.Contains(ext)) return Results.NotFound();
        if (sp.GetService<PackCatalog>() is not { } catalog) return Results.StatusCode(StatusCodes.Status501NotImplemented);
        var level = await data.ReadAsync((tx, _) => tx.Levels.Get(hash));
        if (level is null) return Results.NotFound();
        var name = MapIdentity.MapName(OptionsOf(sp).Mod.Name, level.Depth, hash);
        var path = catalog.Storage.LevelFile(name, ext);
        return File.Exists(path) ? Results.File(path, "application/octet-stream", name + ext) : Results.NotFound();
    }
}
