using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using SourceSharp.Host.Abstractions;

namespace SourceSharp.Host.Admin.Components;

/// <summary>An action waiting for the admin's confirmation, with the before/after the dialog shows as a diff.</summary>
public sealed record AdminConfirm(string Title, string Before, string After, Func<Task<AdminResult>> Run);

/// <summary>
/// What every admin page shares: the stores and IAdminActions, the confirm dialog, the result
/// banner with "kick and edit" on a leased refusal (which kicks, releases, and re-runs the action
/// that was refused), and — for the live pages — a periodic refresh (<see cref="AdminUiOptions.Refresh"/>).
/// </summary>
public abstract class AdminPage : ComponentBase, IDisposable
{
    [Inject] protected IAdminActions Actions { get; set; } = null!;
    [Inject] protected IHostData Data { get; set; } = null!;
    [Inject] protected AdminUiOptions Ui { get; set; } = null!;
    [Inject] protected IServiceProvider Services { get; set; } = null!;

    protected ServiceOptions Options => AdminHost.OptionsOf(Services);
    protected T? Optional<T>() where T : class => Services.GetService<T>();
    protected DateTimeOffset Now => (Services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();

    public AdminResult? Last { get; protected set; }
    public AdminConfirm? Pending { get; protected set; }
    protected string? LoadError { get; private set; }
    /// <summary>True once the first load has finished (until then a page shows what it has, never "not wired").</summary>
    public bool Loaded { get; private set; }
    Func<Task<AdminResult>>? _lastRun;
    CancellationTokenSource? _live;

    /// <summary>Pages that refresh on a timer (the plan's "live updates"; v1 polls instead of pushing).</summary>
    protected virtual bool Live => false;

    protected abstract Task LoadAsync();

    protected override Task OnInitializedAsync() => Reload();

    protected async Task Reload()
    {
        try
        {
            await LoadAsync();
            LoadError = null;
        }
        catch (Exception e) when (e is HostRefusal or InvalidOperationException or HttpRequestException or IOException)
        {
            LoadError = e.Message;
        }
        Loaded = true;
    }

    /// <summary>Opens the confirm dialog: the action runs only on Confirm.</summary>
    protected void Ask(string title, object? before, object? after, Func<Task<AdminResult>> run) =>
        Pending = new AdminConfirm(title, AdminJson.Show(before), AdminJson.Show(after), run);

    public async Task ConfirmPending()
    {
        var p = Pending;
        Pending = null;
        if (p is not null) await Run(p.Run);
    }

    public void CancelPending() => Pending = null;

    protected async Task Run(Func<Task<AdminResult>> run)
    {
        _lastRun = run;
        Last = await run();
        await Reload();
    }

    /// <summary>Q21's "kick and edit": kick the player, release the lease, then the refused action again.</summary>
    public async Task KickAndEdit()
    {
        if (Last is not { Outcome: AdminOutcome.Leased, CharacterId: { } character }) return;
        var kicked = await Actions.KickAndRelease(character);
        Last = kicked.Ok && _lastRun is not null ? await _lastRun() : kicked;
        await Reload();
    }

    protected override void OnAfterRender(bool firstRender)
    {
        if (!firstRender || !Live || Ui.Refresh <= TimeSpan.Zero) return;
        _live = new CancellationTokenSource();
        _ = Poll(_live.Token);
    }

    async Task Poll(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Ui.Refresh);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                await InvokeAsync(async () => { await Reload(); StateHasChanged(); });
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        _live?.Cancel();
        _live?.Dispose();
        GC.SuppressFinalize(this);
    }

    protected static string Age(DateTimeOffset since, DateTimeOffset now)
    {
        var d = now - since;
        return d.TotalDays >= 1 ? $"{(int)d.TotalDays}d{d.Hours}h" : d.TotalHours >= 1 ? $"{(int)d.TotalHours}h{d.Minutes}m" : $"{(int)d.TotalMinutes}m{d.Seconds}s";
    }

    protected static string Short(string? s, int n = 12) => s is null ? "" : s.Length <= n ? s : s[..n] + "…";
}

/// <summary>JSON for display: indented, enums by name, byte arrays left out.</summary>
public static class AdminJson
{
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static string Show(object? value) => value switch
    {
        null => "",
        string s => s,
        _ => JsonSerializer.Serialize(value, Indented),
    };

    /// <summary>A line diff for the confirm dialog: '-' only before, '+' only after, ' ' both.</summary>
    public static IReadOnlyList<(char Mark, string Line)> Diff(string before, string after)
    {
        var a = before.Length == 0 ? [] : before.Split('\n');
        var b = after.Length == 0 ? [] : after.Split('\n');
        // Longest common subsequence: small inputs (one row), so the quadratic table is fine.
        var l = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                l[i, j] = a[i] == b[j] ? l[i + 1, j + 1] + 1 : Math.Max(l[i + 1, j], l[i, j + 1]);
        var result = new List<(char, string)>();
        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (a[x] == b[y]) { result.Add((' ', a[x])); x++; y++; }
            else if (l[x + 1, y] >= l[x, y + 1]) result.Add(('-', a[x++]));
            else result.Add(('+', b[y++]));
        }
        while (x < a.Length) result.Add(('-', a[x++]));
        while (y < b.Length) result.Add(('+', b[y++]));
        return result;
    }
}
