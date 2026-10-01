using System.Reflection;
using Microsoft.Extensions.Options;
using Prometheus;
using Serilog;
using SourceSharp.Host.Admin;
using SourceSharp.Host.Abstractions;

namespace Descent.Service;

/// <summary>The composition root (plan §1.1): Kestrel's listeners, logging, metrics, health.</summary>
public static class ServiceApp
{
    public static string Version { get; } =
        typeof(ServiceApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    /// <param name="configure">Tests override configuration and services here, before the app is built.</param>
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Configuration.AddEnvironmentVariables("DESCENT_");

        builder.Services.AddSerilog((services, lc) => lc
            .ReadFrom.Configuration(builder.Configuration)
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss.fff} {Level:u3} {SourceContext} {Message:lj}{NewLine}{Exception}"));

        builder.Services.AddOptions<ServiceOptions>()
            .Bind(builder.Configuration)
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<ServiceOptions>>(new DetailedValidator(builder.Environment.EnvironmentName));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddDescentService();
        builder.Services.AddHostAdmin(builder.Configuration);

        configure?.Invoke(builder);
        builder.Services.AddDescentWorkers();

        // Listeners are read after `configure`, so a test's ports are the ones bound.
        var listen = new ServiceOptions();
        builder.Configuration.Bind(listen);
        var bound = new BoundListeners();
        builder.Services.AddSingleton(bound);
        builder.WebHost.ConfigureKestrel(k => Listeners.Configure(k, listen, bound));

        var app = builder.Build();
        app.UseRouting();
        app.UseListenerRoles();
        app.UseHttpMetrics();

        app.MapGet("/healthz", Healthz).On(ListenerRole.Admin, ListenerRole.Internal);
        // The service's own registry (ServiceMetrics) plus prometheus-net's default one (HTTP metrics).
        app.MapMetrics("/metrics", app.Services.GetRequiredService<ServiceMetrics>().Registry).On(ListenerRole.Admin, ListenerRole.Internal);
        app.MapMetrics("/metrics/http").On(ListenerRole.Admin, ListenerRole.Internal);
        app.MapDescentService();
        app.MapOps();
        return app;
    }

    static async Task<IResult> Healthz(IEnumerable<IHealthReporter> reporters, CancellationToken ct)
    {
        var items = new List<HealthItem>();
        foreach (var r in reporters)
        {
            try { items.Add(await r.CheckAsync(ct)); }
            catch (Exception e) { items.Add(new HealthItem(r.GetType().Name, false, e.Message)); }
        }
        var ok = items.All(i => i.Ok);
        return Results.Json(new { version = Version, ok, items }, statusCode: ok ? 200 : 503);
    }

    /// <summary>ValidateOnStart with every failing rule named, not just "invalid".</summary>
    sealed class DetailedValidator(string environment) : IValidateOptions<ServiceOptions>
    {
        public ValidateOptionsResult Validate(string? name, ServiceOptions options)
        {
            var errors = ServiceOptionsValidator.Validate(options, environment);
            return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
        }
    }
}
