using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;
using SourceSharp.Host.Abstractions;

namespace SourceSharp.Host.Admin;

/// <summary><c>/admin/export/&lt;table&gt;.csv</c>: the list pages' CSV export (§11), with the page's filters as query parameters.</summary>
public static class AdminCsv
{
    public const int MaxRows = 100_000;

    public static readonly IReadOnlyList<string> Tables =
        ["accounts", "characters", "items", "lost-and-found", "parties", "trades", "instances", "sessions", "levels", "jobs", "audit", "modules"];

    public static async Task<IResult> Handle(string table, HttpContext http, IHostData data)
    {
        var q = http.Request.Query;
        string? S(string k) => q.TryGetValue(k, out var v) && v.Count > 0 && v[0] is { Length: > 0 } s ? s : null;
        int? I(string k) => int.TryParse(S(k), out var n) ? n : null;

        var rows = await data.ReadAsync<IReadOnlyList<string?[]>?>(async (tx, _) => table switch
        {
            "accounts" => [["steamid", "created", "banned", "note"],
                .. (await tx.Characters.ListAccounts(0, MaxRows)).Select(a => new[] { a.SteamId, T(a.Created), B(a.Banned), a.Note })],
            "characters" => [["id", "account", "class", "name", "hardcore", "fallen", "level", "xp", "reached_depth", "australium", "deleted"],
                .. (await tx.Characters.ListAll(0, MaxRows)).Select(c => new[] { c.Id, c.Account, c.ClassName, c.Name, B(c.Hardcore), B(c.Fallen),
                    N(c.Level), N(c.Xp), N(c.ReachedDepth), N(c.Australium), B(c.Deleted) })],
            "items" => [["id", "base_type", "rarity", "ilvl", "count", "owner_kind", "owner_id", "slot", "instance_id", "state", "minted_by", "minted_at", "admin_edited"],
                .. (await tx.Items.Query(ItemQueryFrom(S, I, MaxRows))).Select(i => new[] { i.Id, i.BaseType, N(i.Rarity), N(i.ItemLevel), N(i.Count),
                    i.OwnerKind.ToString(), i.OwnerId, N(i.Slot), i.InstanceId, i.State.ToString(), i.MintedBy, T(i.MintedAt), B(i.AdminEdited) })],
            "lost-and-found" => [["id", "character_id", "item_id", "fee", "since", "origin_instance"],
                .. (await tx.LostAndFound.All(0, MaxRows)).Select(e => new[] { e.Id, e.CharacterId, e.ItemId, N(e.Fee), T(e.Since), e.OriginInstance })],
            "parties" => [["id", "leader", "created", "members"],
                .. (await tx.Parties.All()).Select(p => new[] { p.Id, p.Leader, T(p.Created), string.Join(' ', p.Members) })],
            "trades" => [["id", "hub", "a", "b", "state", "offer_a", "offer_b", "committed"],
                .. (await tx.Trades.All(0, MaxRows)).Select(t => new[] { t.Id, t.HubInstance, t.A, t.B, t.State.ToString(),
                    string.Join(' ', t.OfferA), string.Join(' ', t.OfferB), t.Committed is { } c ? T(c) : null })],
            "instances" => [["id", "kind", "state", "depth", "party", "level_hash", "pod", "pod_ip", "port", "rules_sha256", "created", "players"],
                .. (await tx.Instances.List(take: MaxRows)).Select(i => new[] { i.Id, i.Kind.ToString(), i.State.ToString(), N(i.Depth), i.PartyId,
                    i.LevelHash, i.PodName, i.PodIp, N(i.Port), i.RulesSha256, T(i.Created), N(i.Players) })],
            "sessions" => [["id", "client_addr", "steamid", "instance_id", "backend", "peer", "state", "opened", "last_seen"],
                .. (await tx.Sessions.Open()).Select(s => new[] { s.Id, s.ClientAddr, s.SteamId, s.InstanceId, s.Backend, s.Peer, s.State, T(s.Opened), T(s.LastSeen) })],
            "levels" => [["hash", "pack_id", "depth", "tileset", "seed", "difficulty", "state", "created", "handed_to", "bytes", "error"],
                .. (await tx.Levels.List(I("depth"), take: MaxRows)).Select(l => new[] { l.Hash, N(l.PackId), N(l.Depth), l.Tileset, l.Seed.ToString(CultureInfo.InvariantCulture),
                    N(l.Difficulty), l.State.ToString(), T(l.Created), l.HandedTo, N(l.Bytes), l.Error })],
            "jobs" => [["id", "kind", "key", "priority", "state", "attempts", "created", "started", "finished"],
                .. (await tx.MapJobs.List(take: MaxRows)).Select(j => new[] { N(j.Id), j.Kind.ToString(), j.Key, N(j.Priority), j.State.ToString(), N(j.Attempts),
                    T(j.Created), j.Started is { } s ? T(s) : null, j.Finished is { } f ? T(f) : null })],
            "audit" => [["id", "at", "actor", "action", "target", "before", "after"],
                .. (await tx.Audit.List(new AuditQuery(S("actor"), S("action"), S("target"), 0, MaxRows))).Select(a => new[] { N(a.Id), T(a.At), a.Actor, a.Action, a.Target, a.BeforeJson, a.AfterJson })],
            "modules" => [["sha256", "assembly", "version", "contract_version", "first_seen", "first_instance", "state", "bytes"],
                .. (await tx.Modules.List()).Select(m => new[] { m.Sha256, m.Assembly, m.Version, m.ContractVersion, T(m.FirstSeen), m.FirstInstance, m.State.ToString(), N(m.Bytes) })],
            _ => null,
        });
        if (rows is null) return Results.NotFound();
        return Results.Text(Write(rows), "text/csv; charset=utf-8");
    }

    public static ItemQuery ItemQueryFrom(Func<string, string?> s, Func<string, int?> i, int take, int skip = 0) => new(
        OwnerKind: Enum.TryParse<OwnerKind>(s("ownerKind"), out var k) ? k : null, OwnerId: s("ownerId"), State: ItemState.Live,
        BaseType: s("baseType"), Rarity: i("rarity"), MinItemLevel: i("minIlvl"), MaxItemLevel: i("maxIlvl"), IdPrefix: s("id"), Skip: skip, Take: take);

    static string T(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    static string N(long n) => n.ToString(CultureInfo.InvariantCulture);
    static string B(bool b) => b ? "true" : "false";

    public static string Write(IEnumerable<string?[]> rows)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            for (var i = 0; i < row.Length; i++)
            {
                if (i > 0) sb.Append(',');
                var v = row[i] ?? "";
                if (v.AsSpan().IndexOfAny(",\"\r\n") >= 0) sb.Append('"').Append(v.Replace("\"", "\"\"")).Append('"');
                else sb.Append(v);
            }
            sb.Append("\r\n");
        }
        return sb.ToString();
    }
}
