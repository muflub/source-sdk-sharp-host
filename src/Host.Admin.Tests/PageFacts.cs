using System.Reflection;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.MapPool;

namespace SourceSharp.Host.Admin.Tests;

/// <summary>
/// IAdminActions that records every call with its arguments and forwards it to the real
/// AdminActions, so a page fact can assert both "the action got the shown values" and what the
/// page then shows.
/// </summary>
public class RecordingActions : DispatchProxy
{
    public List<(string Method, object?[] Args)> Calls { get; } = [];
    public IAdminActions Inner { get; set; } = null!;

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        Calls.Add((method!.Name, args ?? []));
        return method.Invoke(Inner, args);
    }

    public static (IAdminActions Proxy, RecordingActions Recorder) Over(IAdminActions inner)
    {
        var proxy = Create<IAdminActions, RecordingActions>();
        var rec = (RecordingActions)(object)proxy;
        rec.Inner = inner;
        return (proxy, rec);
    }

    /// <summary>The arguments of the one call to <paramref name="method"/>, without the trailing CancellationToken.</summary>
    public object?[] Single(string method)
    {
        var call = Assert.Single(Calls, c => c.Method == method);
        return call.Args.Where(a => a is not CancellationToken).ToArray();
    }
}

/// <summary>An antiforgery provider for bunit: the upload form renders its hidden token field.</summary>
public sealed class TestAntiforgery : AntiforgeryStateProvider
{
    public override AntiforgeryRequestToken? GetAntiforgeryToken() => new("test-token", "__RequestVerificationToken");
}

/// <summary>A bunit context over the real stores (TestData on :memory:) and the admin fakes.</summary>
public abstract class PageFacts : IAsyncLifetime
{
    protected AdminWorld W { get; } = new();
    protected BunitContext Ctx { get; } = new();
    protected RecordingActions Rec { get; }

    protected PageFacts()
    {
        var (proxy, rec) = RecordingActions.Over(W.Actions);
        Rec = rec;
        Ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var s = Ctx.Services;
        s.AddSingleton(proxy);
        s.AddSingleton<IHostData>(W.D.Data);
        s.AddSingleton<IRulesProvider>(new SingleRulesProvider(W.Rules, "fake-sha"));
        s.AddSingleton(W.Options);
        s.AddSingleton(new AdminUiOptions { Refresh = TimeSpan.Zero, PageSize = 50 });
        s.AddSingleton<TimeProvider>(W.D.Clock);
        s.AddSingleton<IAdminLedger>(W.Ledger);
        s.AddSingleton<IAdminGateway>(W.Gateway);
        s.AddSingleton<IAdminModules>(W.Modules);
        s.AddSingleton<IAdminBackups>(W.Backups);
        s.AddSingleton<IInstanceLifecycle>(W.Lifecycle);
        s.AddSingleton<IInstanceHost>(W.Pods);
        s.AddSingleton(W.Catalog);
        s.AddSingleton<AntiforgeryStateProvider>(new TestAntiforgery());
    }

    public virtual Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await Ctx.DisposeAsync();
        await W.DisposeAsync();
    }

    /// <summary>Renders the page and waits until its first load has put <paramref name="selector"/> on it.</summary>
    protected IRenderedComponent<T> Page<T>(string selector) where T : IComponent
    {
        var cut = Ctx.Render<T>();
        cut.WaitForState(() => cut.Instance is not SourceSharp.Host.Admin.Components.AdminPage { Loaded: false });
        cut.WaitForElement(selector);
        return cut;
    }

    /// <summary>Confirms the open dialog, after checking it shows <paramref name="diffNeedle"/>.</summary>
    protected static void Confirm<T>(IRenderedComponent<T> cut, string? diffNeedle = null) where T : IComponent
    {
        var dialog = cut.WaitForElement("#confirm-dialog");
        if (diffNeedle is not null) Assert.Contains(diffNeedle, dialog.TextContent);
        cut.Find("#confirm").Click();
    }

    protected static void WaitResult<T>(IRenderedComponent<T> cut, string needle) where T : IComponent =>
        cut.WaitForAssertion(() => Assert.Contains(needle, cut.Find("#result").TextContent));
}
