using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SourceSharp.Host.Abstractions;

namespace SourceSharp.Host.Data;

internal sealed class LostAndFoundStore(Ctx c) : ILostAndFoundStore
{
    public async Task<LostAndFoundRecord> Add(string characterId, string itemId, long fee, string? originInstance)
    {
        c.Writable();
        var row = new LostAndFoundRow { Id = c.NewId(), CharacterId = characterId, ItemId = itemId, Fee = fee, Since = c.Now, OriginInstance = originInstance };
        c.Db.LostAndFound.Add(row);
        await c.Save();
        return row.ToRecord();
    }

    public async Task<IReadOnlyList<LostAndFoundRecord>> ForCharacter(string characterId) =>
        (await c.Db.LostAndFound.AsNoTracking().Where(x => x.CharacterId == characterId).OrderBy(x => x.Id).ToListAsync()).Select(x => x.ToRecord()).ToList();

    public async Task<IReadOnlyList<LostAndFoundRecord>> All(int skip = 0, int take = 200) =>
        (await c.Db.LostAndFound.AsNoTracking().OrderBy(x => x.Id).Skip(skip).Take(take).ToListAsync()).Select(x => x.ToRecord()).ToList();

    public async Task<LostAndFoundRecord?> Get(string id) => (await c.Db.LostAndFound.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id))?.ToRecord();
    public async Task<LostAndFoundRecord?> ForItem(string itemId) => (await c.Db.LostAndFound.AsNoTracking().FirstOrDefaultAsync(x => x.ItemId == itemId))?.ToRecord();

    public async Task Remove(string id)
    {
        c.Writable();
        await c.Db.LostAndFound.Where(x => x.Id == id).ExecuteDeleteAsync();
    }

    public async Task SetFee(string id, long fee)
    {
        c.Writable();
        if (await c.Db.LostAndFound.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Fee, fee)) != 1)
            throw HostRefusal.NotFound("no_item", id);
    }
}

internal sealed class PartyStore(Ctx c) : IPartyStore
{
    async Task<Party?> Load(string id)
    {
        var p = await c.Db.Parties.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return null;
        var members = await c.Db.PartyMembers.AsNoTracking().Where(m => m.PartyId == id).OrderBy(m => m.Since).ThenBy(m => m.CharacterId).ToListAsync();
        return new Party(p.Id, p.Leader, p.Created,
            members.Where(m => !m.Invited).Select(m => m.CharacterId).ToList(),
            members.Where(m => m.Invited).Select(m => m.CharacterId).ToList());
    }

    public async Task<Party> Create(string leader)
    {
        c.Writable();
        var id = c.NewId();
        c.Db.Parties.Add(new PartyRow { Id = id, Leader = leader, Created = c.Now });
        c.Db.PartyMembers.Add(new PartyMemberRow { PartyId = id, CharacterId = leader, Since = c.Now });
        await c.Save();
        return (await Load(id))!;
    }

    public Task<Party?> Get(string id) => Load(id);

    public async Task<Party?> ForCharacter(string characterId)
    {
        var m = await c.Db.PartyMembers.AsNoTracking().FirstOrDefaultAsync(x => x.CharacterId == characterId && !x.Invited);
        return m is null ? null : await Load(m.PartyId);
    }

    public async Task<IReadOnlyList<Party>> All()
    {
        var ids = await c.Db.Parties.AsNoTracking().OrderBy(p => p.Id).Select(p => p.Id).ToListAsync();
        var result = new List<Party>();
        foreach (var id in ids) result.Add((await Load(id))!);
        return result;
    }

    public async Task<Party> Invite(string partyId, string characterId)
    {
        c.Writable();
        if (!await c.Db.PartyMembers.AnyAsync(m => m.PartyId == partyId && m.CharacterId == characterId))
        {
            c.Db.PartyMembers.Add(new PartyMemberRow { PartyId = partyId, CharacterId = characterId, Invited = true, Since = c.Now });
            await c.Save();
        }
        return (await Load(partyId)) ?? throw HostRefusal.NotFound("no_party", partyId);
    }

    public async Task<Party> Join(string partyId, string characterId)
    {
        c.Writable();
        var row = await c.Db.PartyMembers.FirstOrDefaultAsync(m => m.PartyId == partyId && m.CharacterId == characterId)
                  ?? throw HostRefusal.Precondition("not_in_party", $"{characterId} was not invited to {partyId}");
        row.Invited = false;
        row.Since = c.Now;
        await c.Save();
        return (await Load(partyId))!;
    }

    public async Task<Party?> Remove(string partyId, string characterId)
    {
        c.Writable();
        await c.Db.PartyMembers.Where(m => m.PartyId == partyId && m.CharacterId == characterId).ExecuteDeleteAsync();
        var p = await Load(partyId);
        if (p is null) return null;
        if (p.Members.Count == 0) { await Disband(partyId); return null; }
        if (p.Leader == characterId)
        {
            await c.Db.Parties.Where(x => x.Id == partyId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Leader, p.Members[0]));
            p = await Load(partyId);
        }
        return p;
    }

    public async Task Disband(string partyId)
    {
        c.Writable();
        await c.Db.PartyMembers.Where(m => m.PartyId == partyId).ExecuteDeleteAsync();
        await c.Db.Parties.Where(p => p.Id == partyId).ExecuteDeleteAsync();
    }
}

internal sealed class TradeStore(Ctx c) : ITradeStore
{
    sealed record Offer(List<string> A, List<string> B, long AustraliumA, long AustraliumB);

    static TradeRecord ToRecord(TradeRow r)
    {
        var o = JsonSerializer.Deserialize<Offer>(r.OfferJson) ?? new Offer([], [], 0, 0);
        return new TradeRecord(r.Id, r.HubInstance, r.A, r.B, r.State, o.A, o.B, o.AustraliumA, o.AustraliumB, r.LockedA, r.LockedB, r.ConfirmedA, r.ConfirmedB, r.Committed);
    }

    public async Task<TradeRecord> Open(string hubInstance, string a, string b)
    {
        c.Writable();
        var row = new TradeRow
        {
            Id = c.NewId(), HubInstance = hubInstance, A = a, B = b, State = TradeStatus.Open, Created = c.Now,
            OfferJson = JsonSerializer.Serialize(new Offer([], [], 0, 0)),
        };
        c.Db.Trades.Add(row);
        await c.Save();
        return ToRecord(row);
    }

    public async Task<TradeRecord?> Get(string id)
    {
        var r = await c.Db.Trades.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        return r is null ? null : ToRecord(r);
    }

    public async Task<TradeRecord> Save(TradeRecord t)
    {
        c.Writable();
        var row = await c.Db.Trades.FirstOrDefaultAsync(x => x.Id == t.Id) ?? throw HostRefusal.NotFound("no_trade", t.Id);
        row.State = t.State;
        row.OfferJson = JsonSerializer.Serialize(new Offer([.. t.OfferA], [.. t.OfferB], t.AustraliumA, t.AustraliumB));
        row.LockedA = t.LockedA; row.LockedB = t.LockedB; row.ConfirmedA = t.ConfirmedA; row.ConfirmedB = t.ConfirmedB; row.Committed = t.Committed;
        await c.Save();
        return ToRecord(row);
    }

    public async Task<IReadOnlyList<TradeRecord>> ForHub(string hubInstance, bool openOnly = true) =>
        (await c.Db.Trades.AsNoTracking()
            .Where(x => x.HubInstance == hubInstance && (!openOnly || x.State == TradeStatus.Open || x.State == TradeStatus.Locked))
            .OrderBy(x => x.Id).ToListAsync()).Select(ToRecord).ToList();

    public async Task<IReadOnlyList<TradeRecord>> All(int skip = 0, int take = 200) =>
        (await c.Db.Trades.AsNoTracking().OrderByDescending(x => x.Id).Skip(skip).Take(take).ToListAsync()).Select(ToRecord).ToList();
}

internal sealed class InstanceStore(Ctx c) : IInstanceStore
{
    public async Task<InstanceRecord> Add(InstanceRecord instance)
    {
        c.Writable();
        c.Db.Instances.Add(InstanceRow.From(instance));
        await c.Save();
        return (await Get(instance.Id))!;
    }

    public async Task<InstanceRecord?> Get(string id) => (await c.Db.Instances.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id))?.ToRecord();

    public async Task<InstanceRecord?> ByPodName(string podName) =>
        (await c.Db.Instances.AsNoTracking().Where(x => x.PodName == podName).OrderByDescending(x => x.Created).FirstOrDefaultAsync())?.ToRecord();

    public async Task<InstanceRecord> Update(string id, Func<InstanceRecord, InstanceRecord> change)
    {
        c.Writable();
        var row = await c.Db.Instances.FirstOrDefaultAsync(x => x.Id == id) ?? throw HostRefusal.NotFound("unknown_instance", id);
        var next = change(row.ToRecord());
        if (next.Id != id) throw new InvalidOperationException("an instance's id never changes");
        row.Apply(next);
        await c.Save();
        return row.ToRecord();
    }

    public async Task<IReadOnlyList<InstanceRecord>> NonTerminal() =>
        (await c.Db.Instances.AsNoTracking()
            .Where(x => x.State != InstanceState.Reaped && x.State != InstanceState.Crashed && x.State != InstanceState.Failed)
            .OrderBy(x => x.Created).ToListAsync()).Select(x => x.ToRecord()).ToList();

    public async Task<IReadOnlyList<InstanceRecord>> List(InstanceKind? kind = null, InstanceState? state = null, int skip = 0, int take = 200)
    {
        var q = c.Db.Instances.AsNoTracking();
        if (kind is { } k) q = q.Where(x => x.Kind == k);
        if (state is { } s) q = q.Where(x => x.State == s);
        return (await q.OrderByDescending(x => x.Created).Skip(skip).Take(take).ToListAsync()).Select(x => x.ToRecord()).ToList();
    }

    public Task<int> CountLevels(params InstanceState[] states) =>
        c.Db.Instances.CountAsync(x => x.Kind == InstanceKind.Level && states.Contains(x.State));
}

internal sealed class SessionStore(Ctx c) : ISessionStore
{
    public async Task<SessionRecord> Upsert(SessionRecord s)
    {
        c.Writable();
        var row = await c.Db.Sessions.FirstOrDefaultAsync(x => x.Id == s.Id);
        if (row is null) { row = new SessionRow { Id = s.Id }; c.Db.Sessions.Add(row); }
        row.ClientAddr = s.ClientAddr; row.SteamId = s.SteamId; row.InstanceId = s.InstanceId; row.Backend = s.Backend; row.Peer = s.Peer;
        row.State = s.State; row.Opened = s.Opened; row.LastSeen = s.LastSeen; row.IdentifiedAt = s.IdentifiedAt;
        await c.Save();
        return row.ToRecord();
    }

    public async Task<SessionRecord?> Get(string id) => (await c.Db.Sessions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id))?.ToRecord();

    public async Task<SessionRecord?> ByClientAddr(string clientAddr) =>
        (await c.Db.Sessions.AsNoTracking().Where(x => x.ClientAddr == clientAddr && x.State != "closed").OrderByDescending(x => x.Opened).FirstOrDefaultAsync())?.ToRecord();

    public async Task<SessionRecord?> ByPeer(string peer) =>
        (await c.Db.Sessions.AsNoTracking().Where(x => x.Peer == peer).OrderByDescending(x => x.Opened).FirstOrDefaultAsync())?.ToRecord();

    public async Task<SessionRecord?> ByBackendPeer(string backend, string peer) =>
        (await c.Db.Sessions.AsNoTracking().Where(x => x.Backend == backend && x.Peer == peer).OrderByDescending(x => x.Opened).FirstOrDefaultAsync())?.ToRecord();

    public async Task<IReadOnlyList<SessionRecord>> ForBackend(string backend) =>
        (await c.Db.Sessions.AsNoTracking().Where(x => x.Backend == backend && x.State != "closed").OrderBy(x => x.Opened).ToListAsync()).Select(x => x.ToRecord()).ToList();

    public async Task<IReadOnlyList<SessionRecord>> ForSteamId(string steamId) =>
        (await c.Db.Sessions.AsNoTracking().Where(x => x.SteamId == steamId && x.State != "closed").OrderBy(x => x.Opened).ToListAsync()).Select(x => x.ToRecord()).ToList();

    public async Task<IReadOnlyList<SessionRecord>> Open() =>
        (await c.Db.Sessions.AsNoTracking().Where(x => x.State != "closed").OrderBy(x => x.Opened).ToListAsync()).Select(x => x.ToRecord()).ToList();

    public async Task Close(string id, string reason)
    {
        c.Writable();
        var now = c.Now;
        await c.Db.Sessions.Where(x => x.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "closed").SetProperty(x => x.CloseReason, reason).SetProperty(x => x.LastSeen, now));
    }

    static PlayerPin ToPin(PlayerRow r) =>
        new(r.SteamId, r.PinnedPeer, r.Allocator, r.Since, r.LastSeen, JsonSerializer.Deserialize<List<string>>(r.PreviousJson) ?? []);

    public async Task<PlayerPin?> GetPin(string steamId)
    {
        var r = await c.Db.Players.AsNoTracking().FirstOrDefaultAsync(x => x.SteamId == steamId);
        return r is null ? null : ToPin(r);
    }

    public async Task<PlayerPin?> PinByPeer(string peer)
    {
        var r = await c.Db.Players.AsNoTracking().FirstOrDefaultAsync(x => x.PinnedPeer == peer);
        return r is null ? null : ToPin(r);
    }

    public async Task<PlayerPin> SetPin(PlayerPin pin)
    {
        c.Writable();
        var row = await c.Db.Players.FirstOrDefaultAsync(x => x.SteamId == pin.SteamId);
        if (row is null) { row = new PlayerRow { SteamId = pin.SteamId }; c.Db.Players.Add(row); }
        var previous = JsonSerializer.Deserialize<List<string>>(row.PreviousJson) ?? [];
        if (row.PinnedPeer.Length > 0 && row.PinnedPeer != pin.PinnedPeer && !previous.Contains(row.PinnedPeer)) previous.Add(row.PinnedPeer);
        row.PinnedPeer = pin.PinnedPeer; row.Allocator = pin.Allocator; row.Since = pin.Since; row.LastSeen = pin.LastSeen;
        row.PreviousJson = JsonSerializer.Serialize(previous);
        await c.Save();
        return ToPin(row);
    }

    public async Task<IReadOnlyList<PlayerPin>> Pins() =>
        (await c.Db.Players.AsNoTracking().Where(x => x.PinnedPeer != "").OrderBy(x => x.SteamId).ToListAsync()).Select(ToPin).ToList();

    /// <summary>Returns the pin to the pool; the row stays so ResolvePeer on an old log line still answers (§8.1a).</summary>
    public async Task RemovePin(string steamId)
    {
        c.Writable();
        var row = await c.Db.Players.FirstOrDefaultAsync(x => x.SteamId == steamId);
        if (row is null) return;
        var previous = JsonSerializer.Deserialize<List<string>>(row.PreviousJson) ?? [];
        if (row.PinnedPeer.Length > 0 && !previous.Contains(row.PinnedPeer)) previous.Add(row.PinnedPeer);
        row.PreviousJson = JsonSerializer.Serialize(previous);
        row.PinnedPeer = "";
        await c.Save();
    }
}

internal sealed class LibraryStore(Ctx c) : ILibraryStore
{
    public async Task<PackRecord> AddPack(PackRecord pack)
    {
        c.Writable();
        var row = new PackRow
        {
            Mod = pack.Mod, Library = pack.Library, Version = pack.Version, PackId = pack.PackId, LibraryVersion = pack.LibraryVersion,
            Source = pack.Source, UploadedBy = pack.UploadedBy, UploadedAt = pack.UploadedAt, Bytes = pack.Bytes, Sha256 = pack.Sha256,
            LintJson = pack.LintJson, Notes = pack.Notes, State = pack.State,
        };
        c.Db.Packs.Add(row);
        await c.Save();
        return row.ToRecord();
    }

    public async Task<PackRecord?> GetPack(long id) => (await c.Db.Packs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id))?.ToRecord();

    public async Task<IReadOnlyList<PackRecord>> Packs(string mod, string? library = null) =>
        (await c.Db.Packs.AsNoTracking().Where(x => x.Mod == mod && (library == null || x.Library == library))
            .OrderBy(x => x.Library).ThenBy(x => x.Version).ToListAsync()).Select(x => x.ToRecord()).ToList();

    public async Task<int> NextVersion(string mod, string library) =>
        (await c.Db.Packs.Where(x => x.Mod == mod && x.Library == library).MaxAsync(x => (int?)x.Version) ?? 0) + 1;

    public async Task<PackRecord> SetPackState(long id, PackState state)
    {
        c.Writable();
        if (await c.Db.Packs.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.State, state)) != 1)
            throw HostRefusal.NotFound("no_pack", id.ToString());
        return (await GetPack(id))!;
    }

    public async Task<PackAssignment> Assign(PackAssignment a)
    {
        c.Writable();
        var row = await c.Db.PackAssignments.FirstOrDefaultAsync(x => x.Mod == a.Mod && x.Depth == a.Depth);
        if (row is null) { row = new PackAssignmentRow { Mod = a.Mod, Depth = a.Depth }; c.Db.PackAssignments.Add(row); }
        row.PackId = a.PackId; row.Pinned = a.Pinned; row.AssignedBy = a.AssignedBy; row.AssignedAt = a.AssignedAt; row.OldLevels = a.OldLevels;
        await c.Save();
        return row.ToRecord();
    }

    public async Task<PackAssignment?> Assignment(string mod, int depth) =>
        (await c.Db.PackAssignments.AsNoTracking().FirstOrDefaultAsync(x => x.Mod == mod && x.Depth == depth))?.ToRecord();

    public async Task<IReadOnlyList<PackAssignment>> Assignments(string mod) =>
        (await c.Db.PackAssignments.AsNoTracking().Where(x => x.Mod == mod).OrderBy(x => x.Depth).ToListAsync()).Select(x => x.ToRecord()).ToList();
}

internal sealed class LevelStore(Ctx c) : ILevelStore
{
    public async Task<LevelRecord> Add(LevelRecord l)
    {
        c.Writable();
        c.Db.Levels.Add(new LevelRow
        {
            Hash = l.Hash, PackId = l.PackId, Depth = l.Depth, Tileset = l.Tileset, Seed = l.Seed, Difficulty = l.Difficulty,
            State = l.State, Created = l.Created, HandedTo = l.HandedTo, Bytes = l.Bytes, Error = l.Error, RulesSha256 = l.RulesSha256,
        });
        await c.Save();
        return (await Get(l.Hash))!;
    }

    public async Task<LevelRecord?> Get(string hash) => (await c.Db.Levels.AsNoTracking().FirstOrDefaultAsync(x => x.Hash == hash))?.ToRecord();

    public async Task<LevelRecord> SetState(string hash, LevelState state, string? error = null)
    {
        c.Writable();
        var row = await c.Db.Levels.FirstOrDefaultAsync(x => x.Hash == hash) ?? throw HostRefusal.NotFound("no_level", hash);
        row.State = state;
        if (error is not null) row.Error = error;
        await c.Save();
        return row.ToRecord();
    }

    public async Task<LevelRecord?> HandOut(int depth, long packId, string instanceId)
    {
        c.Writable();
        var row = await c.Db.Levels.Where(x => x.Depth == depth && x.PackId == packId && x.State == LevelState.Ready)
            .OrderBy(x => x.Created).ThenBy(x => x.Hash).FirstOrDefaultAsync();
        if (row is null) return null;
        var n = await c.Db.Levels.Where(x => x.Hash == row.Hash && x.State == LevelState.Ready)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, LevelState.HandedOut).SetProperty(x => x.HandedTo, instanceId));
        if (n != 1) return null;
        c.Db.ChangeTracker.Clear();
        return await Get(row.Hash);
    }

    public Task<int> Count(int depth, long? packId, LevelState state) =>
        c.Db.Levels.CountAsync(x => x.Depth == depth && (packId == null || x.PackId == packId) && x.State == state);

    public async Task<IReadOnlyList<LevelRecord>> List(int? depth = null, LevelState? state = null, long? packId = null, int skip = 0, int take = 500)
    {
        var q = c.Db.Levels.AsNoTracking();
        if (depth is { } d) q = q.Where(x => x.Depth == d);
        if (state is { } s) q = q.Where(x => x.State == s);
        if (packId is { } p) q = q.Where(x => x.PackId == p);
        return (await q.OrderBy(x => x.Depth).ThenBy(x => x.Created).Skip(skip).Take(take).ToListAsync()).Select(x => x.ToRecord()).ToList();
    }
}

internal sealed class MapJobStore(Ctx c) : IMapJobStore
{
    public async Task<MapJob> Enqueue(MapJobKind kind, string key, int priority)
    {
        c.Writable();
        var live = await c.Db.MapJobs.FirstOrDefaultAsync(x => x.Key == key && x.Kind == kind && (x.State == MapJobState.Queued || x.State == MapJobState.Running));
        if (live is not null)
        {
            if (live.State == MapJobState.Queued && priority < live.Priority) { live.Priority = priority; await c.Save(); }
            return live.ToRecord();
        }
        var row = new MapJobRow { Kind = kind, Key = key, Priority = priority, State = MapJobState.Queued, Created = c.Now };
        c.Db.MapJobs.Add(row);
        await c.Save();
        return row.ToRecord();
    }

    public async Task<MapJob?> TakeNext()
    {
        c.Writable();
        // A bake outranks every link (§9.3); then priority (0 first), then age.
        var row = await c.Db.MapJobs.Where(x => x.State == MapJobState.Queued)
            .OrderBy(x => x.Kind == MapJobKind.Bake ? 0 : 1).ThenBy(x => x.Priority).ThenBy(x => x.Created).ThenBy(x => x.Id)
            .FirstOrDefaultAsync();
        if (row is null) return null;
        row.State = MapJobState.Running;
        row.Started = c.Now;
        row.Attempts++;
        await c.Save();
        return row.ToRecord();
    }

    public async Task<MapJob> Complete(long id, string? log)
    {
        c.Writable();
        var row = await c.Db.MapJobs.FirstOrDefaultAsync(x => x.Id == id) ?? throw HostRefusal.NotFound("no_job", id.ToString());
        row.State = MapJobState.Done; row.Finished = c.Now; row.Log = log;
        await c.Save();
        return row.ToRecord();
    }

    public async Task<MapJob> Fail(long id, string log, bool requeue)
    {
        c.Writable();
        var row = await c.Db.MapJobs.FirstOrDefaultAsync(x => x.Id == id) ?? throw HostRefusal.NotFound("no_job", id.ToString());
        row.State = requeue ? MapJobState.Queued : MapJobState.Failed;
        row.Finished = requeue ? null : c.Now;
        row.Log = log;
        await c.Save();
        return row.ToRecord();
    }

    public async Task<IReadOnlyList<MapJob>> List(MapJobState? state = null, int take = 200) =>
        (await c.Db.MapJobs.AsNoTracking().Where(x => state == null || x.State == state).OrderByDescending(x => x.Id).Take(take).ToListAsync())
        .Select(x => x.ToRecord()).ToList();
}

internal sealed class VendorStore(Ctx c) : IVendorStore
{
    public async Task Add(string hubInstance, string vendor, string itemId)
    {
        c.Writable();
        c.Db.VendorStock.Add(new VendorStockRow { HubInstance = hubInstance, Vendor = vendor, ItemId = itemId, RolledAt = c.Now });
        await c.Save();
    }

    public async Task<IReadOnlyList<VendorStockRecord>> Stock(string hubInstance, string? vendor = null) =>
        (await c.Db.VendorStock.AsNoTracking().Where(x => x.HubInstance == hubInstance && (vendor == null || x.Vendor == vendor)).ToListAsync())
        .Select(x => new VendorStockRecord(x.HubInstance, x.Vendor, x.RolledAt, x.ItemId)).ToList();

    public async Task Clear(string hubInstance, string? vendor = null)
    {
        c.Writable();
        await c.Db.VendorStock.Where(x => x.HubInstance == hubInstance && (vendor == null || x.Vendor == vendor)).ExecuteDeleteAsync();
    }
}

internal sealed class AuditLog(Ctx c) : IAuditLog
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public async Task Write(string action, string target, object? before = null, object? after = null, string? actor = null)
    {
        c.Writable();
        c.Db.AuditLog.Add(new AuditRow
        {
            At = c.Now, Actor = actor ?? c.Actor, Action = action, Target = target,
            BeforeJson = before is null ? null : before as string ?? JsonSerializer.Serialize(before, Json),
            AfterJson = after is null ? null : after as string ?? JsonSerializer.Serialize(after, Json),
        });
        await c.Save();
    }

    IQueryable<AuditRow> Filter(AuditQuery q)
    {
        var rows = c.Db.AuditLog.AsNoTracking();
        if (q.Actor is { } a) rows = rows.Where(x => x.Actor == a);
        if (q.Action is { } ac) rows = rows.Where(x => x.Action == ac);
        if (q.Target is { } t) rows = rows.Where(x => x.Target == t);
        return rows;
    }

    public async Task<IReadOnlyList<AuditEntry>> List(AuditQuery query) =>
        (await Filter(query).OrderByDescending(x => x.Id).Skip(query.Skip).Take(query.Take).ToListAsync()).Select(x => x.ToRecord()).ToList();

    public Task<int> Count(AuditQuery query) => Filter(query).CountAsync();
}

internal sealed class SettingsStore(Ctx c) : ISettingsStore
{
    public async Task<Setting?> Get(string key)
    {
        var r = await c.Db.Settings.AsNoTracking().FirstOrDefaultAsync(x => x.Key == key);
        return r is null ? null : new Setting(r.Key, r.ValueJson, r.UpdatedBy, r.UpdatedAt);
    }

    public async Task Set(string key, string valueJson, string updatedBy)
    {
        c.Writable();
        var row = await c.Db.Settings.FirstOrDefaultAsync(x => x.Key == key);
        if (row is null) { row = new SettingRow { Key = key }; c.Db.Settings.Add(row); }
        row.ValueJson = valueJson; row.UpdatedBy = updatedBy; row.UpdatedAt = c.Now;
        await c.Save();
    }

    public async Task<IReadOnlyList<Setting>> All() =>
        (await c.Db.Settings.AsNoTracking().OrderBy(x => x.Key).ToListAsync()).Select(r => new Setting(r.Key, r.ValueJson, r.UpdatedBy, r.UpdatedAt)).ToList();
}

internal sealed class RulesModuleStore(Ctx c) : IRulesModuleStore
{
    public async Task<RulesModuleRecord?> Get(string sha256) => (await c.Db.RulesModules.AsNoTracking().FirstOrDefaultAsync(x => x.Sha256 == sha256))?.ToRecord();

    public async Task<RulesModuleRecord> Add(RulesModuleRecord m)
    {
        c.Writable();
        c.Db.RulesModules.Add(new RulesModuleRow
        {
            Sha256 = m.Sha256, Assembly = m.Assembly, Version = m.Version, ContractVersion = m.ContractVersion, DepsJson = m.DepsJson,
            FirstSeen = m.FirstSeen, FirstInstance = m.FirstInstance, State = m.State, Bytes = m.Bytes,
        });
        await c.Save();
        return (await Get(m.Sha256))!;
    }

    public async Task<RulesModuleRecord> SetState(string sha256, ModuleState state)
    {
        c.Writable();
        if (await c.Db.RulesModules.Where(x => x.Sha256 == sha256).ExecuteUpdateAsync(s => s.SetProperty(x => x.State, state)) != 1)
            throw HostRefusal.NotFound("no_module", sha256);
        return (await Get(sha256))!;
    }

    public async Task<IReadOnlyList<RulesModuleRecord>> List() =>
        (await c.Db.RulesModules.AsNoTracking().OrderBy(x => x.FirstSeen).ToListAsync()).Select(x => x.ToRecord()).ToList();
}

internal sealed class LevelClaimStore(Ctx c) : ILevelClaimStore
{
    public async Task<LevelClaim?> Get(string characterId, int depth) =>
        (await c.Db.LevelClaims.AsNoTracking().FirstOrDefaultAsync(x => x.CharacterId == characterId && x.Depth == depth))?.ToRecord();

    public async Task<LevelClaim> Set(string characterId, int depth, string levelHash)
    {
        c.Writable();
        var row = await c.Db.LevelClaims.FirstOrDefaultAsync(x => x.CharacterId == characterId && x.Depth == depth);
        if (row is null) { row = new LevelClaimRow { CharacterId = characterId, Depth = depth, Since = c.Now }; c.Db.LevelClaims.Add(row); }
        if (row.LevelHash != levelHash) { row.LevelHash = levelHash; row.Since = c.Now; }
        row.LastUsed = c.Now;
        await c.Save();
        return row.ToRecord();
    }

    public async Task<IReadOnlyList<LevelClaim>> ForCharacter(string characterId) =>
        (await c.Db.LevelClaims.AsNoTracking().Where(x => x.CharacterId == characterId).OrderBy(x => x.Depth).ToListAsync()).Select(x => x.ToRecord()).ToList();

    public async Task<IReadOnlyList<string>> CharactersWithClaims() =>
        await c.Db.LevelClaims.AsNoTracking().Select(x => x.CharacterId).Distinct().ToListAsync();

    public async Task Touch(string characterId)
    {
        c.Writable();
        var now = c.Now;
        await c.Db.LevelClaims.Where(x => x.CharacterId == characterId).ExecuteUpdateAsync(s => s.SetProperty(x => x.LastUsed, now));
    }

    public async Task<IReadOnlyList<string>> Drop(string characterId)
    {
        c.Writable();
        var hashes = await c.Db.LevelClaims.AsNoTracking().Where(x => x.CharacterId == characterId).Select(x => x.LevelHash).ToListAsync();
        await c.Db.LevelClaims.Where(x => x.CharacterId == characterId).ExecuteDeleteAsync();
        return hashes.Distinct().ToList();
    }

    public Task<int> ClaimsOn(string levelHash) => c.Db.LevelClaims.CountAsync(x => x.LevelHash == levelHash);
}
