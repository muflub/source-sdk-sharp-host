using System.Net;
using System.Net.Sockets;

namespace SourceSharp.Host.Gateway.Admission;

public enum BanKind { Address, SteamId }

/// <summary>One ban: an IPv4 address or CIDR, or a SteamID; <see cref="Until"/> null is permanent.</summary>
public sealed record BanEntry(BanKind Kind, string Value, DateTimeOffset? Until, string Reason);

/// <summary>
/// The bans the gateway enforces itself, never waiting on the service (plan_steam_auth S-14): the service's
/// list (SyncTable, SetBans) plus the temp bans this gateway has issued (S-15) until the service echoes them.
/// Address bans match an IPv4 address or a CIDR range; expired entries never match.
/// </summary>
public sealed class BanList
{
    readonly record struct Range(uint Network, uint Mask, BanEntry Entry);

    List<Range> _ranges = [];
    Dictionary<ulong, BanEntry> _steamIds = [];
    readonly List<BanEntry> _local = [];

    /// <summary>Replaces the service's list; local temp bans stay until they expire or the service carries them.</summary>
    public void Replace(IEnumerable<BanEntry> service, DateTimeOffset now)
    {
        var all = service.ToList();
        _local.RemoveAll(l => l.Until <= now || all.Any(s => s.Kind == l.Kind && s.Value == l.Value));
        Rebuild(all.Concat(_local));
    }

    /// <summary>A temp ban issued here: enforced at once.</summary>
    public void AddLocal(BanEntry entry)
    {
        _local.Add(entry);
        Rebuild(Entries().Concat([entry]));
    }

    IEnumerable<BanEntry> Entries() => _ranges.Select(r => r.Entry).Concat(_steamIds.Values).Distinct();

    void Rebuild(IEnumerable<BanEntry> entries)
    {
        var ranges = new List<Range>();
        var ids = new Dictionary<ulong, BanEntry>();
        foreach (var e in entries)
        {
            if (e.Kind == BanKind.SteamId && ulong.TryParse(e.Value, out var id)) ids[id] = Later(ids.GetValueOrDefault(id), e);
            else if (e.Kind == BanKind.Address && TryParseCidr(e.Value, out var net, out var mask)) ranges.Add(new Range(net, mask, e));
        }
        _ranges = ranges;
        _steamIds = ids;
    }

    static BanEntry Later(BanEntry? a, BanEntry b) =>
        a is null ? b : a.Until is null ? a : b.Until is null || b.Until > a.Until ? b : a;

    public static bool TryParseCidr(string value, out uint network, out uint mask)
    {
        network = mask = 0;
        var slash = value.IndexOf('/');
        var bits = 32;
        if (slash >= 0 && !(int.TryParse(value.AsSpan(slash + 1), out bits) && bits is >= 0 and <= 32)) return false;
        if (!IPAddress.TryParse(slash >= 0 ? value[..slash] : value, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) return false;
        mask = bits == 0 ? 0 : uint.MaxValue << (32 - bits);
        network = ToUInt(ip) & mask;
        return true;
    }

    static uint ToUInt(IPAddress ip)
    {
        var b = ip.MapToIPv4().GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    static bool Live(BanEntry e, DateTimeOffset now) => e.Until is null || e.Until > now;

    public BanEntry? Address(IPAddress ip, DateTimeOffset now)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork && !ip.IsIPv4MappedToIPv6) return null;
        var v = ToUInt(ip);
        foreach (var r in _ranges)
            if ((v & r.Mask) == r.Network && Live(r.Entry, now)) return r.Entry;
        return null;
    }

    public BanEntry? SteamId(ulong steamId, DateTimeOffset now) =>
        _steamIds.TryGetValue(steamId, out var e) && Live(e, now) ? e : null;

    public int Count => _ranges.Count + _steamIds.Count;
}
