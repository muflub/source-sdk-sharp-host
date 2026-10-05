using SourceSharp.Host.Steam.Native;

namespace SourceSharp.Host.Steam;

/// <summary>
/// Server-browser area: the metadata Steam advertises for this server (name, map, tags,
/// key values, player caps, visibility). Applied once in Bootstrap and mutable live.
/// </summary>
public sealed partial class SteamGameServerHost
{
    /// <summary>Pump thread only: the pre-logon identity + browser metadata block from Bootstrap.</summary>
    private void ApplySettings(GameServerConfiguration cfg)
    {
        var api = _api!;
        CallString(api.SetProduct, cfg.Product);
        CallString(api.SetGameDescription, cfg.GameDescription);
        CallString(api.SetModDir, cfg.ModDir);
        api.SetDedicatedServer(_gs, cfg.DedicatedServer);
        CallString(api.SetServerName, cfg.ServerName);
        CallString(api.SetMapName, cfg.MapName);
        api.SetPasswordProtected(_gs, cfg.PasswordProtected);
        api.SetMaxPlayerCount(_gs, cfg.MaxPlayerCount);
        api.SetBotPlayerCount(_gs, cfg.BotPlayerCount);
        api.SetSpectatorPort(_gs, cfg.SpectatorPort);
        CallString(api.SetSpectatorServerName, cfg.SpectatorServerName);
        api.ClearAllKeyValues(_gs);
        foreach (var (k, v) in cfg.KeyValues)
            SetKeyValueNative(api, k, v);
        CallString(api.SetGameTags, cfg.GameTags);
        CallString(api.SetGameData, cfg.GameData);
        CallString(api.SetRegion, cfg.Region);
        api.SetAdvertiseServerActive(_gs, false); // advertise only once logged on (StartAsync flips it)
    }

    private void CallString(SteamNativeApi.SelfStringDelegate fn, string value) =>
        Utf8.Use(value, (Fn: fn, Gs: _gs), static (s, p) => s.Fn(s.Gs, p));

    private void SetKeyValueNative(SteamNativeApi api, string key, string value) =>
        Utf8.Use2(key, value, (Api: api, Gs: _gs), static (s, k, v) => s.Api.SetKeyValue(s.Gs, k, v));

    public Task SetServerNameAsync(string name) => Enqueue("SetServerName", () => CallString(RequireApi().SetServerName, name));
    public Task SetMapNameAsync(string map) => Enqueue("SetMapName", () => CallString(RequireApi().SetMapName, map));
    public Task SetGameTagsAsync(string tags) => Enqueue("SetGameTags", () => CallString(RequireApi().SetGameTags, tags));
    public Task SetGameDataAsync(string data) => Enqueue("SetGameData", () => CallString(RequireApi().SetGameData, data));
    public Task SetRegionAsync(string region) => Enqueue("SetRegion", () => CallString(RequireApi().SetRegion, region));
    public Task SetMaxPlayerCountAsync(int max) => Enqueue("SetMaxPlayerCount", () => RequireApi().SetMaxPlayerCount(_gs, max));
    public Task SetBotPlayerCountAsync(int bots) => Enqueue("SetBotPlayerCount", () => RequireApi().SetBotPlayerCount(_gs, bots));
    public Task SetPasswordProtectedAsync(bool value) => Enqueue("SetPasswordProtected", () => RequireApi().SetPasswordProtected(_gs, value));
    public Task SetSpectatorPortAsync(ushort port) => Enqueue("SetSpectatorPort", () => RequireApi().SetSpectatorPort(_gs, port));
    public Task SetSpectatorServerNameAsync(string name) => Enqueue("SetSpectatorServerName", () => CallString(RequireApi().SetSpectatorServerName, name));
    public Task SetKeyValueAsync(string key, string value) => Enqueue("SetKeyValue", () => SetKeyValueNative(RequireApi(), key, value));
    public Task ClearKeyValuesAsync() => Enqueue("ClearAllKeyValues", () => RequireApi().ClearAllKeyValues(_gs));
    public Task SetAdvertiseActiveAsync(bool active) => Enqueue("SetAdvertiseServerActive", () => RequireApi().SetAdvertiseServerActive(_gs, active));
}
