using Microsoft.AspNetCore.Http;
using SourceSharp.Host.Abstractions;

namespace SourceSharp.Host.Admin;

/// <summary>
/// <c>/admin/instances/&lt;id&gt;/devapi/…</c> (Q19, Q22): the pod's devapi web UI through the service,
/// so it is reached over the loopback admin listener like the rest of the admin. v1 forwards GET
/// and HEAD only; anything it cannot reach answers 501 with the reason in the body.
/// </summary>
public static class DevApiProxy
{
    public const string ClientName = "admin-devapi";

    public static async Task Handle(HttpContext http, string id, string? rest, IHostData data, AdminUiOptions ui, IHttpClientFactory clients)
    {
        var instance = await data.ReadAsync((tx, _) => tx.Instances.Get(id), http.RequestAborted);
        if (instance is null) { await Answer(http, StatusCodes.Status404NotFound, $"no instance {id}"); return; }
        if (ui.DevApiPort <= 0) { await Answer(http, StatusCodes.Status501NotImplemented, "devapi proxy: AdminUi:DevApiPort is not configured"); return; }
        if (instance.Terminal || string.IsNullOrEmpty(instance.PodIp))
        {
            await Answer(http, StatusCodes.Status501NotImplemented, $"devapi proxy: instance {id} has no reachable pod ({instance.State})");
            return;
        }
        if (!HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method))
        {
            await Answer(http, StatusCodes.Status501NotImplemented, "devapi proxy: v1 forwards GET and HEAD only");
            return;
        }

        var target = new Uri($"http://{instance.PodIp}:{ui.DevApiPort}/{rest}{http.Request.QueryString}");
        try
        {
            using var request = new HttpRequestMessage(new HttpMethod(http.Request.Method), target);
            using var response = await clients.CreateClient(ClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, http.RequestAborted);
            http.Response.StatusCode = (int)response.StatusCode;
            if (response.Content.Headers.ContentType is { } type) http.Response.ContentType = type.ToString();
            await response.Content.CopyToAsync(http.Response.Body, http.RequestAborted);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !http.RequestAborted.IsCancellationRequested)
        {
            await Answer(http, StatusCodes.Status501NotImplemented, $"devapi proxy: {target} is not reachable: {e.Message}");
        }
    }

    static Task Answer(HttpContext http, int status, string reason)
    {
        http.Response.StatusCode = status;
        http.Response.ContentType = "text/plain; charset=utf-8";
        return http.Response.WriteAsync(reason);
    }
}
