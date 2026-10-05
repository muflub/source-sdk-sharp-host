namespace SourceSharp.Host.Gateway.Admission;

/// <summary>
/// Repeated handshake failures become temp bans (plan_steam_auth S-15), per source IP and per SteamID:
/// <c>Failures</c> strikes within <c>Window</c> ban for <c>Base</c>, doubled for each earlier ban of the same key
/// within <c>RepeatWindow</c>, capped at <c>Cap</c>. Rules are read on every strike, so a live change applies
/// to the next one (S-21).
/// </summary>
public sealed class StrikeTable(Func<TempBanOptions> rules)
{
    sealed class Track
    {
        public readonly Queue<DateTimeOffset> Strikes = new();
        public DateTimeOffset? LastBan;
        public int Repeats;
    }

    readonly Dictionary<(BanKind, string), Track> _tracks = [];

    StrikeRule Rule(BanKind kind) => kind == BanKind.Address ? rules().Ip : rules().SteamId;

    /// <summary>Records a failure; returns the ban it triggers, or null.</summary>
    public BanEntry? Strike(BanKind kind, string key, DateTimeOffset now, string reason)
    {
        var rule = Rule(kind);
        if (!_tracks.TryGetValue((kind, key), out var t)) _tracks[(kind, key)] = t = new Track();
        while (t.Strikes.TryPeek(out var first) && now - first > rule.Window) t.Strikes.Dequeue();
        t.Strikes.Enqueue(now);
        if (t.Strikes.Count < rule.Failures) return null;

        t.Strikes.Clear();
        t.Repeats = t.LastBan is { } last && now - last <= rule.RepeatWindow ? t.Repeats + 1 : 0;
        t.LastBan = now;
        var ticks = Math.Min(rule.Cap.Ticks, rule.Base.Ticks * Math.Pow(2, Math.Min(t.Repeats, 40)));
        var duration = TimeSpan.FromTicks((long)ticks);
        return new BanEntry(kind, key, now + duration, $"temp ban: {rule.Failures} handshake failures ({reason})");
    }

    /// <summary>Strikes recorded for the key and still inside its window.</summary>
    public int Pending(BanKind kind, string key, DateTimeOffset now) =>
        _tracks.TryGetValue((kind, key), out var t) ? t.Strikes.Count(s => now - s <= Rule(kind).Window) : 0;

    /// <summary>Forgets keys with nothing left to remember.</summary>
    public void Prune(DateTimeOffset now)
    {
        foreach (var (k, t) in _tracks.ToList())
        {
            var rule = Rule(k.Item1);
            if (t.Strikes.All(s => now - s > rule.Window) && (t.LastBan is null || now - t.LastBan > rule.RepeatWindow))
                _tracks.Remove(k);
        }
    }
}
