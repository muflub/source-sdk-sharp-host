using System.Text.RegularExpressions;
using SourceSharp.Host.Abstractions;
using G = SourceSharp.Host.Proto.Gateway;
using P = SourceSharp.Host.Proto;

namespace TF2.Descent.Service.Gateway;

/// <summary>
/// The mod's browser block (plan_steam_auth S-18): its fields are templates over the world-status keys this host
/// publishes, and a key it does not publish is refused when the block is reported, never rendered blank.
/// </summary>
public static partial class BrowserTemplate
{
    /// <summary>The world-status keys (docs/mod-image.md lists them for mod authors).</summary>
    public static readonly IReadOnlySet<string> Keys = new HashSet<string>(StringComparer.Ordinal)
    {
        "hub_map", "players", "max_players", "levels_open", "deepest", "players_by_level", "pool_ready",
    };

    [GeneratedRegex(@"\{([^{}]*)\}")]
    private static partial Regex Placeholder();

    static IEnumerable<string> Texts(P.Browser b) => [b.Name, b.Description, b.Tags, b.Map, .. b.Rules.Values];

    /// <summary>Every placeholder in the block that is not a published key, in order, once.</summary>
    public static IReadOnlyList<string> UnknownKeys(P.Browser b) =>
        Texts(b).SelectMany(t => Placeholder().Matches(t).Select(m => m.Groups[1].Value)).Where(k => !Keys.Contains(k)).Distinct().ToList();

    public static string Render(string template, IReadOnlyDictionary<string, string> values) =>
        Placeholder().Replace(template, m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
}

/// <summary>
/// Renders the server-browser entry (S-18) from the mod's block and the world as it is now, and pushes it to the
/// gateway (its A2S answers and the Steam game server's listing) whenever it changed.
/// </summary>
public sealed class BrowserPublisher(IHostData data, IGatewayControl control, ServiceOptions options, TimeProvider clock,
                                     ILogger<BrowserPublisher> log) : BackgroundService
{
    /// <summary>The settings key of the last block a game server reported (protobuf JSON).</summary>
    public const string BlockKey = "browser.block";
    G.ServerInfo? _last;

    /// <summary>The world-status values now.</summary>
    public async Task<IReadOnlyDictionary<string, string>> Status(CancellationToken ct) => await data.ReadAsync(async (tx, _) =>
    {
        var instances = await tx.Instances.NonTerminal();
        var levels = instances.Where(i => i.Kind == InstanceKind.Level && i.State == InstanceState.Live).OrderBy(i => i.Depth).ToList();
        var players = (await tx.Sessions.Open()).Count(s => s.SteamId is not null);
        var ready = (await tx.Levels.List(state: LevelState.Ready)).Count;
        var capacity = options.Instances.HubMaxPlayers + levels.Count * options.Instances.LevelMaxPlayers;
        return new Dictionary<string, string>
        {
            ["hub_map"] = options.Mod.TownMap,
            ["players"] = players.ToString(),
            ["max_players"] = capacity.ToString(),
            ["levels_open"] = levels.Count.ToString(),
            ["deepest"] = (levels.Count == 0 ? 0 : levels.Max(l => l.Depth)).ToString(),
            ["players_by_level"] = string.Join(" ", levels.Select(l => $"{l.Depth}:{l.Players}")),
            ["pool_ready"] = ready.ToString(),
        };
    }, ct);

    /// <summary>The entry as it would be pushed now: the block rendered, or today's defaults without one.</summary>
    public async Task<G.ServerInfo> Build(CancellationToken ct)
    {
        var status = await Status(ct);
        var setting = await data.ReadAsync((tx, _) => tx.Settings.Get(BlockKey), ct);
        var block = setting is null ? new P.Browser() : Google.Protobuf.JsonParser.Default.Parse<P.Browser>(setting.ValueJson);
        string R(string template, string fallback) => BrowserTemplate.Render(template.Length > 0 ? template : fallback, status);
        var info = new G.ServerInfo
        {
            Name = R(block.Name, options.Mod.Name),
            Map = R(block.Map, "{hub_map}"),
            Folder = options.Mod.Name,
            Game = R(block.Description, options.Mod.Name),
            Players = int.Parse(status["players"]),
            MaxPlayers = block.MaxPlayers > 0 ? block.MaxPlayers : int.Parse(status["max_players"]),
            AppId = options.Browser.AppId,
            Tags = R(block.Tags, ""),
            Region = block.Region,
        };
        foreach (var (k, v) in block.Rules) info.Rules[k] = BrowserTemplate.Render(v, status);
        return info;
    }

    /// <summary>Renders and pushes when the entry changed; true when it pushed.</summary>
    public async Task<bool> PublishOnce(CancellationToken ct)
    {
        var info = await Build(ct);
        if (_last is not null && _last.Equals(info)) return false;
        await control.SetServerInfo(info, ct);
        _last = info;
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await PublishOnce(stoppingToken); }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning("browser entry not published: {Error}", e.Message); }
            try { await Task.Delay(options.Browser.PushInterval, clock, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
