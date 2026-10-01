using Microsoft.EntityFrameworkCore;
using SourceSharp.Host.Abstractions;

namespace SourceSharp.Host.Data;

/// <summary>
/// The ledger's primitives (R-H1, §5.1). A move checks the row, names the exact refusal,
/// then runs one conditional UPDATE on (id, version, owner_kind, state) that must affect
/// exactly one row; the event row is written in the same transaction.
/// </summary>
internal sealed class ItemLedger(Ctx c) : IItemLedger
{
    static string Owner(OwnerKind kind, string id) => $"{kind}:{id}";

    public async Task<ItemRecord> Mint(NewItem item, OwnerKind owner, string ownerId, string? instanceId, int slot, string mintedBy, string? requestId)
    {
        c.Writable();
        var now = c.Now;
        var row = new ItemRow
        {
            Id = c.NewId(), Seed = item.Seed, BaseType = item.BaseType, Rarity = item.Rarity, ItemLevel = item.ItemLevel,
            Count = item.Count, Identified = item.Identified, Instance = item.Instance, SchemaVersion = item.SchemaVersion,
            ForCharacter = item.ForCharacter, OwnerKind = owner, OwnerId = ownerId, Slot = slot, InstanceId = instanceId,
            Tier = item.Tier, RolledForLevel = item.RolledForLevel, MintedBy = mintedBy, MintedAt = now, Version = 1, State = ItemState.Live,
        };
        c.Db.Items.Add(row);
        c.Db.ItemEvents.Add(new ItemEventRow
        {
            ItemId = row.Id, At = now, Kind = "mint:" + mintedBy, ToOwner = Owner(owner, ownerId),
            InstanceId = instanceId, RequestId = requestId, Actor = c.Actor,
        });
        await c.Save();
        return row.ToRecord();
    }

    public async Task<ItemRecord?> Get(string id) =>
        (await c.Db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id))?.ToRecord();

    async Task<ItemRow> Current(string id, OwnerKind from, long? expectedVersion, string? expectedInstance)
    {
        var row = await c.Db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id)
                  ?? throw HostRefusal.NotFound("no_item", id);
        if (row.State != ItemState.Live)
            throw HostRefusal.Precondition("wrong_owner", $"item {id} is {row.State}");
        if (row.OwnerKind != from)
            throw HostRefusal.Precondition("wrong_owner", $"item {id} is owned by {row.OwnerKind}, not {from}");
        if (expectedVersion is long v && row.Version != v)
            throw HostRefusal.Precondition("version_conflict", $"item {id} is at version {row.Version}, not {v}");
        if (expectedInstance is not null && row.InstanceId != expectedInstance)
            throw HostRefusal.Denied("wrong_instance", $"item {id} is in instance {row.InstanceId ?? "none"}");
        return row;
    }

    public async Task<ItemRecord> Move(ItemMove m)
    {
        c.Writable();
        var row = await Current(m.ItemId, m.From, m.ExpectedVersion, m.ExpectedInstanceId);
        var version = row.Version;
        var forCharacter = m.ClearForCharacter ? null : row.ForCharacter;
        var n = await c.Db.Items
            .Where(i => i.Id == m.ItemId && i.Version == version && i.OwnerKind == m.From && i.State == ItemState.Live)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.OwnerKind, m.To)
                .SetProperty(i => i.OwnerId, m.ToOwnerId)
                .SetProperty(i => i.Slot, m.Slot)
                .SetProperty(i => i.InstanceId, m.InstanceId)
                .SetProperty(i => i.ForCharacter, forCharacter)
                .SetProperty(i => i.Version, version + 1));
        if (n != 1)
            throw HostRefusal.Precondition("version_conflict", $"item {m.ItemId} changed under the move");
        c.Db.ItemEvents.Add(new ItemEventRow
        {
            ItemId = m.ItemId, At = c.Now, Kind = m.EventKind, FromOwner = Owner(row.OwnerKind, row.OwnerId),
            ToOwner = Owner(m.To, m.ToOwnerId), InstanceId = m.InstanceId ?? row.InstanceId, RequestId = m.RequestId, Actor = c.Actor,
        });
        await c.Save();
        return (await Get(m.ItemId))!;
    }

    public async Task<ItemRecord> Terminate(string itemId, OwnerKind from, ItemState terminal, string reason, string? requestId)
    {
        c.Writable();
        if (terminal == ItemState.Live) throw new ArgumentException("Live is not terminal", nameof(terminal));
        var row = await Current(itemId, from, null, null);
        var now = c.Now;
        var n = await c.Db.Items
            .Where(i => i.Id == itemId && i.Version == row.Version && i.OwnerKind == from && i.State == ItemState.Live)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.State, terminal)
                .SetProperty(i => i.TerminalReason, reason)
                .SetProperty(i => i.TerminalAt, now)
                .SetProperty(i => i.Version, row.Version + 1));
        if (n != 1)
            throw HostRefusal.Precondition("version_conflict", $"item {itemId} changed under the terminal move");
        c.Db.ItemEvents.Add(new ItemEventRow
        {
            ItemId = itemId, At = now, Kind = $"{terminal.ToString().ToLowerInvariant()}:{reason}",
            FromOwner = Owner(row.OwnerKind, row.OwnerId), InstanceId = row.InstanceId, RequestId = requestId, Actor = c.Actor,
        });
        await c.Save();
        return (await Get(itemId))!;
    }

    public async Task<ItemRecord> Mutate(string itemId, long expectedVersion, byte[] instance, int schemaVersion, bool identified, bool adminEdited, string eventKind, string? requestId)
    {
        c.Writable();
        var row = await c.Db.Items.FirstOrDefaultAsync(i => i.Id == itemId) ?? throw HostRefusal.NotFound("no_item", itemId);
        if (row.State != ItemState.Live) throw HostRefusal.Precondition("wrong_owner", $"item {itemId} is {row.State}");
        if (row.Version != expectedVersion)
            throw HostRefusal.Precondition("version_conflict", $"item {itemId} is at version {row.Version}, not {expectedVersion}");
        row.Instance = instance;
        row.SchemaVersion = schemaVersion;
        row.Identified = identified;
        row.AdminEdited |= adminEdited;
        row.Version++;
        c.Db.ItemEvents.Add(new ItemEventRow
        {
            ItemId = itemId, At = c.Now, Kind = eventKind, FromOwner = Owner(row.OwnerKind, row.OwnerId),
            ToOwner = Owner(row.OwnerKind, row.OwnerId), InstanceId = row.InstanceId, RequestId = requestId, Actor = c.Actor,
        });
        await c.Save();
        return (await Get(itemId))!;
    }

    IQueryable<ItemRow> Filter(ItemQuery q)
    {
        var items = c.Db.Items.AsNoTracking();
        if (q.OwnerKind is { } ok) items = items.Where(i => i.OwnerKind == ok);
        if (q.OwnerId is { } oid) items = items.Where(i => i.OwnerId == oid);
        if (q.InstanceId is { } iid) items = items.Where(i => i.InstanceId == iid);
        if (q.State is { } st) items = items.Where(i => i.State == st);
        if (q.BaseType is { } bt) items = items.Where(i => i.BaseType == bt);
        if (q.Rarity is { } r) items = items.Where(i => i.Rarity == r);
        if (q.MinItemLevel is { } min) items = items.Where(i => i.ItemLevel >= min);
        if (q.MaxItemLevel is { } max) items = items.Where(i => i.ItemLevel <= max);
        if (q.IdPrefix is { } p) items = items.Where(i => i.Id.StartsWith(p));
        return items;
    }

    public async Task<IReadOnlyList<ItemRecord>> Query(ItemQuery query) =>
        (await Filter(query).OrderBy(i => i.Slot).ThenBy(i => i.Id).Skip(query.Skip).Take(query.Take).ToListAsync()).Select(i => i.ToRecord()).ToList();

    public Task<int> Count(ItemQuery query) => Filter(query).CountAsync();

    public async Task<IReadOnlyList<ItemEvent>> Events(string itemId) =>
        (await c.Db.ItemEvents.AsNoTracking().Where(e => e.ItemId == itemId).OrderBy(e => e.Id).ToListAsync()).Select(e => e.ToRecord()).ToList();

    public Task<bool> HasEvent(string instanceId, string kind) =>
        c.Db.ItemEvents.AnyAsync(e => e.InstanceId == instanceId && e.Kind == kind);

    public async Task<(long Minted, long Terminal, long Live)> Totals()
    {
        // Both sides from the append-only event log (terminal rows are purged after 30 days,
        // their events are not); Live from the rows. Reconcile checks minted − terminal = live.
        var minted = await c.Db.ItemEvents.LongCountAsync(e => e.Kind.StartsWith("mint:"));
        var terminal = await c.Db.ItemEvents.LongCountAsync(e =>
            e.Kind.StartsWith("swept:") || e.Kind.StartsWith("destroyed:") || e.Kind.StartsWith("consumed:"));
        var live = await c.Db.Items.LongCountAsync(i => i.State == ItemState.Live);
        return (minted, terminal, live);
    }

    public async Task<int> PurgeTerminalOlderThan(DateTimeOffset cutoff)
    {
        c.Writable();
        return await c.Db.Items.Where(i => i.State != ItemState.Live && i.TerminalAt < cutoff).ExecuteDeleteAsync();
    }
}
