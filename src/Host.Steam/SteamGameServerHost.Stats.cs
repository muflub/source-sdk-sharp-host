using System.Runtime.CompilerServices;
using SourceSharp.Host.Steam.Native;

namespace SourceSharp.Host.Steam;

/// <summary>
/// ISteamGameServerStats area: fetch/store per-user server-side stats and achievements.
/// </summary>
public sealed partial class SteamGameServerHost
{
    /// <summary>
    /// Fetches a user's server-side stats/achievements (GSStatsReceived_t), then reads the named values
    /// — all on the pump thread. Dictionaries stay empty unless Result is OK.
    /// </summary>
    public Task<UserStatsSnapshot> RequestUserStatsAsync(
        SteamId player,
        IReadOnlyList<string>? intStats = null,
        IReadOnlyList<string>? floatStats = null,
        IReadOnlyList<string>? achievements = null)
    {
        intStats ??= [];
        floatStats ??= [];
        achievements ??= [];
        return CallResultAsync("RequestUserStats",
            api => api.StatsRequestUserStats(_stats, player.Value), CallbackStatsReceived,
            p =>
            {
                var received = CallbackDecoder.ReadStatsReceived(p);
                var ints = new Dictionary<string, int>();
                var floats = new Dictionary<string, float>();
                var achs = new Dictionary<string, bool>();
                if (received.Result == SteamResult.OK)
                {
                    var api = RequireApi();
                    var st = (Api: api, Stats: _stats, Player: player);
                    foreach (var name in intStats)
                        if (TryReadStat(name, st, static (s, np, outPtr) => s.Api.StatsGetUserStatInt32(s.Stats, s.Player.Value, np, outPtr), out int iv))
                            ints[name] = iv;
                    foreach (var name in floatStats)
                        if (TryReadStat(name, st, static (s, np, outPtr) => s.Api.StatsGetUserStatFloat(s.Stats, s.Player.Value, np, outPtr), out float fv))
                            floats[name] = fv;
                    foreach (var name in achievements)
                        if (TryReadStat(name, st, static (s, np, outPtr) => s.Api.StatsGetUserAchievement(s.Stats, s.Player.Value, np, outPtr), out bool av))
                            achs[name] = av;
                }
                return new UserStatsSnapshot(player, received.Result) { IntStats = ints, FloatStats = floats, Achievements = achs };
            });
    }

    public Task<(SteamResult Result, SteamId Player)> StoreUserStatsAsync(SteamId player) =>
        CallResultAsync("StoreUserStats", api => api.StatsStoreUserStats(_stats, player.Value), CallbackStatsStored,
            p => CallbackDecoder.ReadStatsStored(p));

    /// <summary>Requests server gameplay stats; completes with GSGameplayStats_t (callback 207).</summary>
    public Task<GameplayStats> GetGameplayStatsAsync()
    {
        var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue("GetGameplayStats", () =>
        {
            RequireApi().GetGameplayStats(_gs);
            _pendingEventCalls[CallbackGameplayStats] = new PendingCall(0, CallbackGameplayStats,
                DeadlineOrDefault(), null, tcs);
        });
        return Unwrap<object, GameplayStats>(tcs.Task);
    }

    /// <summary>Writes a server-side stat; call StoreUserStatsAsync to flush to Steam.</summary>
    public Task SetUserStatAsync(SteamId player, string name, int value) =>
        Enqueue("SetUserStatInt32", () => Utf8.Eval(name, (Api: RequireApi(), Stats: _stats, Player: player, Value: value),
            static (s, n) => s.Api.StatsSetUserStatInt32(s.Stats, s.Player.Value, n, s.Value)));

    public Task SetUserStatAsync(SteamId player, string name, float value) =>
        Enqueue("SetUserStatFloat", () => Utf8.Eval(name, (Api: RequireApi(), Stats: _stats, Player: player, Value: value),
            static (s, n) => s.Api.StatsSetUserStatFloat(s.Stats, s.Player.Value, n, s.Value)));

    public Task SetUserAchievementAsync(SteamId player, string name) =>
        Enqueue("SetUserAchievement", () => Utf8.Eval(name, (Api: RequireApi(), Stats: _stats, Player: player),
            static (s, n) => s.Api.StatsSetUserAchievement(s.Stats, s.Player.Value, n)));

    public Task ClearUserAchievementAsync(SteamId player, string name) =>
        Enqueue("ClearUserAchievement", () => Utf8.Eval(name, (Api: RequireApi(), Stats: _stats, Player: player),
            static (s, n) => s.Api.StatsClearUserAchievement(s.Stats, s.Player.Value, n)));

    /// <summary>
    /// Reads one stat name on the pump thread: UTF-8 scratch for the name plus an 8-byte zeroed
    /// stack slot for the out param (int/float/bool outputs all fit; native writes exactly sizeof(T)).
    /// </summary>
    private static unsafe bool TryReadStat<T, TState>(string name, TState state, Utf8StatGet<TState> get, out T value)
        where T : unmanaged
    {
        byte* scratch = stackalloc byte[8] { 0, 0, 0, 0, 0, 0, 0, 0 };
        static bool Getter((TState State, Utf8StatGet<TState> Get, IntPtr Slot) s, IntPtr name) => s.Get(s.State, name, s.Slot);
        bool ok = Utf8.Eval(name, (state, get, (IntPtr)scratch), Getter);
        if (!ok)
        {
            value = default;
            return false;
        }
        value = Unsafe.ReadUnaligned<T>(scratch);
        return true;
    }

    /// <summary>Stat getter over an already-encoded UTF-8 name plus the 8-byte out slot.</summary>
    private delegate bool Utf8StatGet<TState>(TState state, IntPtr name, IntPtr outSlot);
}
