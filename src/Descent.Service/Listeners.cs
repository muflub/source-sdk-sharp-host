using System.Net;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using SourceSharp.Host.Abstractions;
using KestrelListenOptions = Microsoft.AspNetCore.Server.Kestrel.Core.ListenOptions;

namespace Descent.Service;

/// <summary>
/// The service's listeners (plan §1 diagram, H1f). Each Kestrel endpoint tags its
/// connections with a role, and every HTTP endpoint names the roles it may be served
/// on, so /admin answers on the loopback listener and nowhere else whatever address or
/// port the others bind, and the fastdl listener serves *.bsp.bz2 and nothing else.
/// </summary>
public enum ListenerRole { Admin, Internal, GameApi, GatewayApi, FastDl }

public sealed record ServedOn(params ListenerRole[] Roles);

/// <summary>The listeners as Kestrel bound them: a configured port 0 reads back as the real one after start.</summary>
public sealed class BoundListeners
{
    readonly Dictionary<ListenerRole, KestrelListenOptions> _options = [];
    internal void Add(ListenerRole role, KestrelListenOptions options) => _options[role] = options;
    public int Port(ListenerRole role) => ((IPEndPoint)_options[role].EndPoint).Port;
}

public static class Listeners
{
    const string RoleKey = "descent.listener";

    public static void Configure(KestrelServerOptions kestrel, ServiceOptions o, BoundListeners? bound = null)
    {
        Listen(kestrel, bound, o.Listen.Admin, ListenerRole.Admin, HttpProtocols.Http1AndHttp2);
        if (o.Listen.Internal.Length > 0)
            Listen(kestrel, bound, o.Listen.Internal, ListenerRole.Internal, HttpProtocols.Http1AndHttp2);
        Listen(kestrel, bound, o.Listen.GameApi, ListenerRole.GameApi, HttpProtocols.Http2);
        Listen(kestrel, bound, o.Listen.GatewayApi, ListenerRole.GatewayApi, HttpProtocols.Http2);
        Listen(kestrel, bound, o.Listen.FastDl, ListenerRole.FastDl, HttpProtocols.Http1);
    }

    static void Listen(KestrelServerOptions kestrel, BoundListeners? bound, string endpoint, ListenerRole role, HttpProtocols protocols)
    {
        var ep = IPEndPoint.Parse(endpoint);
        kestrel.Listen(ep, l =>
        {
            bound?.Add(role, l);
            l.Protocols = protocols;
            l.Use(next => connection =>
            {
                connection.Items[RoleKey] = role;
                return next(connection);
            });
        });
    }

    public static ListenerRole? RoleOf(HttpContext http) =>
        http.Features.Get<IConnectionItemsFeature>()?.Items.TryGetValue(RoleKey, out var r) == true ? (ListenerRole)r! : null;

    /// <summary>An endpoint with <see cref="ServedOn"/> metadata answers 404 on any other listener; one without it, 404 everywhere.</summary>
    public static IApplicationBuilder UseListenerRoles(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        var endpoint = http.GetEndpoint();
        if (endpoint is not null)
        {
            var served = endpoint.Metadata.GetMetadata<ServedOn>();
            var role = RoleOf(http);
            if (served is null || role is null || !served.Roles.Contains(role.Value))
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
        }
        await next(http);
    });

    public static TBuilder On<TBuilder>(this TBuilder builder, params ListenerRole[] roles) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new ServedOn(roles));
}
