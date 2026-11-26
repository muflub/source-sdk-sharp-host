using System.Numerics;
using Google.Protobuf;
using Grpc.Core;
using P = SourceSharp.Host.Proto;

namespace SourceSharp.Host.Sdk;

/// <summary>
/// The reserve cache of one leased character on this instance (§5.2a, D-H1): items the service
/// already minted and rolled, per tier. <see cref="NextForTier"/> is the kill path: a queue pop on
/// the main thread, no I/O, no await. Refills go out in the background when a tier falls below
/// its low-water mark and land in the queues inside Pump. Closed with the lease.
/// </summary>
public interface IReserve
{
    ICharacterLease Lease { get; }
    bool IsOpen { get; }
    int Count(int tier);
    int Size(int tier);
    bool Refilling(int tier);
    /// <summary>The next reserve item of <paramref name="tier"/>, or null when the tier is empty (then use <see cref="IItems.MintDrops"/>).</summary>
    HostItem? NextForTier(int tier);
    /// <summary>
    /// Reports the kill that revealed <paramref name="item"/>, after it has been spawned. Retried
    /// with the same request id while the host is unreachable and the lease is held.
    /// </summary>
    Task<HostResult<RevealOutcome>> Reveal(HostItem item, uint killSeq, string robotTemplate, IReadOnlyList<string>? nearbyMembers = null, Vector3 position = default);
}

/// <summary>§6.3: the ledger as typed calls. Every write carries its lease; no item is ever created here.</summary>
public interface IItems
{
    /// <summary>Opens the reserve for a lease (TakeReserve for every tier with a size); main thread.</summary>
    IReserve OpenReserve(ICharacterLease lease);
    /// <summary>
    /// The synchronous mint: <paramref name="tier"/> ≥ 0 is the empty-reserve fallback for one kill
    /// (counted in <see cref="IHostMetrics.ReserveFallbacks"/>), -1 a boss or champion table.
    /// </summary>
    Task<HostResult<IReadOnlyList<HostItem>>> MintDrops(ICharacterLease killer, uint killSeq, string robotTemplate, int tier,
        IReadOnlyList<string>? nearbyMembers = null, Vector3 position = default);
    Task<HostResult<HostItem>> Claim(ICharacterLease lease, string itemId);
    Task<HostResult<HostItem>> Drop(ICharacterLease lease, string itemId, Vector3 position = default);
    /// <summary>A backpack slot change (2.6).</summary>
    Task<HostResult<HostItem>> Move(ICharacterLease lease, string itemId, int slot);
    /// <summary>
    /// Into a slot of a container (2.7): equip, unequip, belt. Refused onto an occupied slot
    /// (<c>slot_taken</c>), outside the container (<c>bad_slot</c>) or by the rules (their reason, e.g. <c>potions_only</c>).
    /// </summary>
    Task<HostResult<HostItem>> Move(ICharacterLease lease, string itemId, HostContainer container, int slot);
    Task<HostResult<HostItem>> StashMove(ICharacterLease lease, string itemId, bool toStash, int slot = -1);
    Task<HostResult<IReadOnlyList<HostItem>>> GetBackpack(string characterId);
    Task<HostResult<IReadOnlyList<HostItem>>> GetStash(string account, bool hardcore);
    Task<HostResult<IReadOnlyList<HostItem>>> GetVendorStock(string vendor);
    Task<HostResult<HostItem>> Buy(ICharacterLease lease, string itemId, string vendor);
    /// <summary>Returns the character's Australium after the sale.</summary>
    Task<HostResult<long>> Sell(ICharacterLease lease, string itemId, string vendor);
    Task<HostResult<HostItem>> BuyBack(ICharacterLease lease, string itemId, string vendor);
    Task<HostResult<HostItem>> OpenCrate(ICharacterLease lease, string itemId);
    Task<HostResult<HostItem>> Craft(ICharacterLease lease, string recipe, IReadOnlyList<string> inputs);
    Task<HostResult<HostItem>> Identify(ICharacterLease lease, string itemId);
    /// <summary>A town repair (hub only). Same as <c>Repair(lease, itemId, HostRepairKind.Town)</c>.</summary>
    Task<HostResult<HostItem>> Repair(ICharacterLease lease, string itemId);
    /// <summary>
    /// SDK 2.7: a town repair (hub only, costs Australium) or a field repair with
    /// <paramref name="kitId"/> (anywhere; the kit is consumed in the same write). The item keeps its id.
    /// </summary>
    Task<HostResult<HostItem>> Repair(ICharacterLease lease, string itemId, HostRepairKind kind, string? kitId = null);
    /// <summary>
    /// SDK 2.7: wear on up to 64 items in one write, each item at most once. Batch it: on a timer,
    /// at a checkpoint and when an item reaches 0, not on every hit.
    /// </summary>
    Task<HostResult<IReadOnlyList<HostItem>>> Wear(ICharacterLease lease, IReadOnlyList<HostWear> entries);
    /// <summary>
    /// Uses up <paramref name="count"/> of a carried item (2.7; a potion from the belt, a tome): the item
    /// comes back as it is after, <see cref="HostItem.Count"/> lowered, or gone (the ledger marks it
    /// consumed). Any instance kind. The effect is the game's.
    /// </summary>
    Task<HostResult<HostItem>> Consume(ICharacterLease lease, string itemId, int count = 1);
    /// <summary>
    /// A world container (a supply crate, a locker) opened by the leased character (2.7): the host
    /// replays the rules' <c>ContainerDrops</c> and mints them to this floor for the opener (perhaps
    /// none). Once per container and character; a second open is refused <c>already_opened</c>.
    /// </summary>
    Task<HostResult<IReadOnlyList<HostItem>>> OpenContainer(ICharacterLease lease, uint containerSeq);
    /// <summary>
    /// Two carried items exchange their container and slot (2.8): an equip swap, a belt ↔ backpack
    /// swap, an equip through a full backpack. Both destinations are asked of the rules; a refusal
    /// refuses both. Returns [a, b] as they are after.
    /// </summary>
    Task<HostResult<IReadOnlyList<HostItem>>> Swap(ICharacterLease lease, string itemA, string itemB);
    /// <summary>
    /// Two stacks of one kind become one (2.8; two partial potion stacks on the belt): returns [into,
    /// from] as they are after; <c>from</c> is <see cref="HostItemState.Consumed"/> when it was used up,
    /// else holds what did not fit. The rules decide the kind and the limit; the host keeps the count.
    /// </summary>
    Task<HostResult<IReadOnlyList<HostItem>>> Merge(ICharacterLease lease, string fromItemId, string intoItemId);
    /// <summary>
    /// A breakable broken by the leased hero (2.8): the host replays the rules' <c>BreakableDrops</c>,
    /// credits its Australium to the hero and puts its items on this floor for them. Call it once per
    /// hero in the room, each with their own lease. Once per breakable and hero; a second break is
    /// <c>already_broken</c>.
    /// </summary>
    Task<HostResult<HostBreak>> Break(ICharacterLease lease, uint breakableSeq);
    /// <summary>
    /// A kill's Australium as one pile on this floor (2.8): the host replays the kill's roll. Call it
    /// for every kill whose roll has Australium, item or not (a 2.8 <c>Reveal</c> credits none). Once per
    /// kill; a kill with none is refused <c>no_australium</c>.
    /// </summary>
    Task<HostResult<HostItem>> KillAustralium(ICharacterLease killer, uint killSeq, string robotTemplate, int partySize, Vector3 position = default);
    /// <summary>
    /// A pile picked up by the leased hero (the killer or one of their party): every party member on
    /// this level is credited their share (the rules' <c>AustraliumShares</c>), and the pile ends.
    /// </summary>
    Task<HostResult<IReadOnlyList<HostAustraliumCredit>>> ClaimAustralium(ICharacterLease lease, string pileId);
    Task<HostResult<IReadOnlyList<HostItem>>> Salvage(ICharacterLease lease, string itemId);
    /// <summary>
    /// Everything carried (backpack, equipped, belt) becomes the corpse's; the result is those items.
    /// 2.9: the Australium stays with the character, and a softcore character is minted the class's
    /// starting kit in the same write (it is not in the result; it is on the character, as
    /// <see cref="GetBackpack"/> and the next join show). A hardcore character falls and gets none.
    /// </summary>
    Task<HostResult<IReadOnlyList<HostItem>>> RecordDeath(ICharacterLease lease, Vector3 position = default);
    Task<HostResult<IReadOnlyList<HostItem>>> LootCorpse(ICharacterLease lease);
    Task<HostResult<IReadOnlyList<HostItem>>> ListWorldItems();
}

internal sealed class Reserve : IReserve
{
    readonly SdkCore _core;
    readonly CharacterLease _lease;
    readonly Dictionary<int, Queue<HostItem>> _queues = new();
    readonly Dictionary<int, int> _generation = new();
    readonly HashSet<int> _refilling = [];
    readonly HashSet<int> _heldTaken = []; // §5.2a: tiers whose held reserve this lease has taken back
    bool _closed;

    public Reserve(SdkCore core, CharacterLease lease)
    {
        _core = core;
        _lease = lease;
        for (var t = 0; t < core.Options.ReserveSizes.Length; t++)
        {
            _queues[t] = new Queue<HostItem>();
            _generation[t] = 0;
        }
    }

    public ICharacterLease Lease => _lease;
    public bool IsOpen => !_closed;
    public int Count(int tier) => _queues.TryGetValue(tier, out var q) ? q.Count : 0;
    public int Size(int tier) => tier >= 0 && tier < _core.Options.ReserveSizes.Length ? _core.Options.ReserveSizes[tier] : 0;
    public bool Refilling(int tier) => _refilling.Contains(tier);
    /// <summary>The ids queued for a tier (facts only).</summary>
    internal IReadOnlyList<string> Peek(int tier) => _queues.TryGetValue(tier, out var q) ? q.Select(i => i.Id).ToList() : [];

    public void FillAll()
    {
        foreach (var tier in _queues.Keys) StartRefill(tier);
    }

    public HostItem? NextForTier(int tier)
    {
        if (_closed || !_queues.TryGetValue(tier, out var q) || !q.TryDequeue(out var item))
        {
            _core.Metrics.Miss();
            if (!_closed) StartRefill(tier);
            return null;
        }
        _core.Metrics.Hit();
        if (q.Count < Size(tier) * _core.Options.ReserveLowWater) StartRefill(tier);
        return item;
    }

    /// <summary>A background TakeReserve for one tier; its items land in the queue inside Pump. Never blocks.</summary>
    void StartRefill(int tier)
    {
        if (_closed || !_queues.ContainsKey(tier) || _refilling.Contains(tier) || _lease.Refusal() is not null) return;
        var wanted = Size(tier) - Count(tier);
        if (wanted <= 0) return;
        _refilling.Add(tier);
        _core.Metrics.Refill();
        var generation = _generation[tier];
        // §5.2a: the reserve outlives a lease, so a tier's first take asks for what is already held.
        var includeHeld = !_heldTaken.Contains(tier);
        var request = new P.TakeReserveRequest
        {
            RequestId = Rpc.NewRequestId(), CharacterId = _lease.CharacterId, Wanted = { new P.TierCount { Tier = tier, Count = wanted } },
            IncludeHeld = includeHeld,
        };
        _ = _lease.Write(async token =>
        {
            request.LeaseToken = token;
            return await _core.Rpc.Call((m, d, ct) => _core.ItemClient.TakeReserveAsync(request, m, d, ct)).ConfigureAwait(false);
        }, r =>
        {
            _refilling.Remove(tier);
            if (!r.Ok || _closed || _generation[tier] != generation) return;
            if (includeHeld) _heldTaken.Add(tier);
            foreach (var i in r.Value.Items)
                if (_queues.TryGetValue(i.Tier, out var q)) q.Enqueue(i.ToSdk());
        });
    }

    /// <summary>The service swept these tiers for a level jump: drop the cached items and take fresh ones.</summary>
    public void ReplaceStale(IReadOnlyList<int> tiers)
    {
        foreach (var tier in tiers)
        {
            if (!_queues.TryGetValue(tier, out var q)) continue;
            q.Clear();
            _generation[tier]++;
            _refilling.Remove(tier);
            StartRefill(tier);
        }
    }

    public void Close()
    {
        _closed = true;
        foreach (var q in _queues.Values) q.Clear();
        _refilling.Clear();
    }

    public Task<HostResult<RevealOutcome>> Reveal(HostItem item, uint killSeq, string robotTemplate, IReadOnlyList<string>? nearbyMembers = null, Vector3 position = default)
    {
        var request = new P.RevealRequest
        {
            RequestId = Rpc.NewRequestId(), CharacterId = _lease.CharacterId, ItemId = item.Id, KillSeq = killSeq, RobotTemplate = robotTemplate,
            NearbyMembers = { nearbyMembers ?? [] }, Position = position.ToProto(),
            AustraliumPile = true, // 2.8: the kill's Australium is IItems.KillAustralium's pile, not credited here
        };
        return _lease.Write(async token =>
        {
            request.LeaseToken = token;
            var backoff = _core.Options.RetryBackoff;
            var auth = _core.Rpc.Auth(); // the kill's instance, for every retry (plan_sdk_2_7 §3a)
            while (true)
            {
                var r = await _core.Rpc.Call((m, d, ct) => _core.ItemClient.RevealAsync(request, m, d, ct), auth: auth).ConfigureAwait(false);
                if (r.Error != HostError.Unreachable || _lease.Refusal() is not null || _core.Stopping.IsCancellationRequested)
                    return r.Map(v => new RevealOutcome(v.Item.ToSdk(), v.AustraliumBooked));
                _core.Metrics.RevealRetry();
                try { await _core.Options.Clock.Delay(backoff, _core.Stopping).ConfigureAwait(false); }
                catch (OperationCanceledException) { return HostResult<RevealOutcome>.Refused(HostRefusal.Disposed()); }
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, _core.Options.ReconnectBackoffMax.Ticks));
            }
        });
    }
}

internal sealed class Items(SdkCore core) : IItems
{
    static CharacterLease L(ICharacterLease lease) =>
        lease as CharacterLease ?? throw new ArgumentException("a lease from ICharacters.Lease", nameof(lease));

    Task<HostResult<TOut>> Leased<TReq, TResp, TOut>(ICharacterLease lease, TReq request, Action<TReq, ByteString> setToken,
        Func<TReq, Metadata, DateTime?, CancellationToken, AsyncUnaryCall<TResp>> rpc, Func<TResp, TOut> map) =>
        L(lease).Write(async token =>
        {
            setToken(request, token);
            return (await core.Rpc.Call((m, d, ct) => rpc(request, m, d, ct)).ConfigureAwait(false)).Map(map);
        });

    Task<HostResult<TOut>> Read<TResp, TOut>(Func<Metadata, DateTime, CancellationToken, AsyncUnaryCall<TResp>> rpc, Func<TResp, TOut> map) =>
        core.Background(async () => (await core.Rpc.Call(rpc).ConfigureAwait(false)).Map(map));

    static IReadOnlyList<HostItem> List(P.ItemsResponse r) => r.ToSdk();

    P.ItemActionRequest Action(ICharacterLease lease, string itemId, string vendor = "") =>
        new() { RequestId = Rpc.NewRequestId(), CharacterId = lease.CharacterId, ItemId = itemId, Vendor = vendor };

    static void Tok(P.ItemActionRequest r, ByteString t) => r.LeaseToken = t;

    public IReserve OpenReserve(ICharacterLease lease)
    {
        var l = L(lease);
        if (l.Reserve is { IsOpen: true } open) return open;
        var reserve = new Reserve(core, l);
        l.Reserve = reserve;
        if (l.Refusal() is not null) reserve.Close();
        else reserve.FillAll();
        return reserve;
    }

    public Task<HostResult<IReadOnlyList<HostItem>>> MintDrops(ICharacterLease killer, uint killSeq, string robotTemplate, int tier,
        IReadOnlyList<string>? nearbyMembers = null, Vector3 position = default)
    {
        if (tier >= 0) core.Metrics.Fallback(); else core.Metrics.Boss();
        var request = new P.MintDropsRequest
        {
            RequestId = Rpc.NewRequestId(), KillerCharacter = killer.CharacterId, RobotTemplate = robotTemplate, KillSeq = killSeq, Tier = tier,
            NearbyMembers = { nearbyMembers ?? [] }, Position = position.ToProto(),
        };
        return Leased(killer, request, (r, t) => r.LeaseToken = t, core.ItemClient.MintDropsAsync, List);
    }

    public Task<HostResult<HostItem>> Claim(ICharacterLease lease, string itemId) =>
        Leased(lease, Action(lease, itemId), Tok, core.ItemClient.ClaimAsync, i => i.ToSdk());

    public Task<HostResult<HostItem>> Drop(ICharacterLease lease, string itemId, Vector3 position = default) =>
        Leased(lease, new P.DropRequest { RequestId = Rpc.NewRequestId(), CharacterId = lease.CharacterId, ItemId = itemId, Position = position.ToProto() },
            (r, t) => r.LeaseToken = t, core.ItemClient.DropAsync, i => i.ToSdk());

    public Task<HostResult<HostItem>> Move(ICharacterLease lease, string itemId, int slot) =>
        Move(lease, itemId, HostContainer.Backpack, slot);

    public Task<HostResult<HostItem>> Move(ICharacterLease lease, string itemId, HostContainer container, int slot) =>
        Leased(lease, new P.MoveRequest
            {
                RequestId = Rpc.NewRequestId(), CharacterId = lease.CharacterId, ItemId = itemId, Slot = slot,
                Container = (P.ItemContainer)(int)container,
            },
            (r, t) => r.LeaseToken = t, core.ItemClient.MoveAsync, i => i.ToSdk());

    public Task<HostResult<HostItem>> StashMove(ICharacterLease lease, string itemId, bool toStash, int slot = -1) =>
        Leased(lease, new P.StashMoveRequest { RequestId = Rpc.NewRequestId(), CharacterId = lease.CharacterId, ItemId = itemId, ToStash = toStash, Slot = slot },
            (r, t) => r.LeaseToken = t, core.ItemClient.StashMoveAsync, i => i.ToSdk());

    public Task<HostResult<IReadOnlyList<HostItem>>> GetBackpack(string characterId) =>
        Read((m, d, ct) => core.ItemClient.GetBackpackAsync(new P.CharacterRef { CharacterId = characterId }, m, d, ct), List);

    public Task<HostResult<IReadOnlyList<HostItem>>> GetStash(string account, bool hardcore) =>
        Read((m, d, ct) => core.ItemClient.GetStashAsync(new P.StashRef { Account = account, Hardcore = hardcore }, m, d, ct), List);

    public Task<HostResult<IReadOnlyList<HostItem>>> GetVendorStock(string vendor) =>
        Read((m, d, ct) => core.ItemClient.GetVendorStockAsync(new P.VendorRef { Vendor = vendor }, m, d, ct), List);

    public Task<HostResult<HostItem>> Buy(ICharacterLease lease, string itemId, string vendor) =>
        Leased(lease, Action(lease, itemId, vendor), Tok, core.ItemClient.BuyAsync, i => i.ToSdk());

    public Task<HostResult<long>> Sell(ICharacterLease lease, string itemId, string vendor) =>
        Leased(lease, Action(lease, itemId, vendor), Tok, core.ItemClient.SellAsync, w => w.Australium);

    public Task<HostResult<HostItem>> BuyBack(ICharacterLease lease, string itemId, string vendor) =>
        Leased(lease, Action(lease, itemId, vendor), Tok, core.ItemClient.BuyBackAsync, i => i.ToSdk());

    public Task<HostResult<HostItem>> OpenCrate(ICharacterLease lease, string itemId) =>
        Leased(lease, Action(lease, itemId), Tok, core.ItemClient.OpenCrateAsync, i => i.ToSdk());

    public Task<HostResult<HostItem>> Craft(ICharacterLease lease, string recipe, IReadOnlyList<string> inputs) =>
        Leased(lease, new P.CraftRequest { RequestId = Rpc.NewRequestId(), CharacterId = lease.CharacterId, Recipe = recipe, Inputs = { inputs } },
            (r, t) => r.LeaseToken = t, core.ItemClient.CraftAsync, i => i.ToSdk());

    public Task<HostResult<HostItem>> Identify(ICharacterLease lease, string itemId) =>
        Leased(lease, Action(lease, itemId), Tok, core.ItemClient.IdentifyAsync, i => i.ToSdk());

    public Task<HostResult<HostItem>> Repair(ICharacterLease lease, string itemId) => Repair(lease, itemId, HostRepairKind.Town);

    public Task<HostResult<HostItem>> Repair(ICharacterLease lease, string itemId, HostRepairKind kind, string? kitId = null)
    {
        var request = Action(lease, itemId);
        request.RepairKind = kind == HostRepairKind.Field ? P.RepairKind.Field : P.RepairKind.Town;
        request.KitId = kitId ?? "";
        return Leased(lease, request, Tok, core.ItemClient.RepairAsync, i => i.ToSdk());
    }

    public Task<HostResult<IReadOnlyList<HostItem>>> Wear(ICharacterLease lease, IReadOnlyList<HostWear> entries)
    {
        var request = new P.WearRequest { RequestId = Rpc.NewRequestId(), CharacterId = lease.CharacterId };
        request.Entries.AddRange(entries.Select(e => new P.WearEntry { ItemId = e.ItemId, Amount = e.Amount }));
        return Leased(lease, request, (r, t) => r.LeaseToken = t, core.ItemClient.WearAsync, List);
    }

    public Task<HostResult<HostItem>> KillAustralium(ICharacterLease killer, uint killSeq, string robotTemplate, int partySize, Vector3 position = default) =>
        Leased(killer, new P.KillAustraliumRequest
            {
                RequestId = Rpc.NewRequestId(), CharacterId = killer.CharacterId, KillSeq = killSeq, RobotTemplate = robotTemplate,
                PartySize = partySize, Position = position.ToProto(),
            },
            (r, t) => r.LeaseToken = t, core.ItemClient.KillAustraliumAsync, i => i.ToSdk());

    public Task<HostResult<IReadOnlyList<HostAustraliumCredit>>> ClaimAustralium(ICharacterLease lease, string pileId) =>
        Leased(lease, Action(lease, pileId), Tok, core.ItemClient.ClaimAustraliumAsync,
            p => (IReadOnlyList<HostAustraliumCredit>)p.Credits.Select(c => new HostAustraliumCredit(c.CharacterId, c.Amount)).ToList());

    public Task<HostResult<HostBreak>> Break(ICharacterLease lease, uint breakableSeq) =>
        Leased(lease, new P.BreakRequest { RequestId = Rpc.NewRequestId(), CharacterId = lease.CharacterId, BreakableSeq = breakableSeq },
            (r, t) => r.LeaseToken = t, core.ItemClient.BreakAsync, b => new HostBreak(b.Australium, b.Items.Select(i => i.ToSdk()).ToList()));

    public Task<HostResult<IReadOnlyList<HostItem>>> Merge(ICharacterLease lease, string fromItemId, string intoItemId) =>
        Leased(lease, new P.MergeRequest { RequestId = Rpc.NewRequestId(), CharacterId = lease.CharacterId, FromItemId = fromItemId, IntoItemId = intoItemId },
            (r, t) => r.LeaseToken = t, core.ItemClient.MergeAsync, List);

    public Task<HostResult<IReadOnlyList<HostItem>>> Swap(ICharacterLease lease, string itemA, string itemB) =>
        Leased(lease, new P.SwapRequest { RequestId = Rpc.NewRequestId(), CharacterId = lease.CharacterId, ItemA = itemA, ItemB = itemB },
            (r, t) => r.LeaseToken = t, core.ItemClient.SwapAsync, List);

    public Task<HostResult<IReadOnlyList<HostItem>>> OpenContainer(ICharacterLease lease, uint containerSeq) =>
        Leased(lease, new P.OpenContainerRequest { RequestId = Rpc.NewRequestId(), CharacterId = lease.CharacterId, ContainerSeq = containerSeq },
            (r, t) => r.LeaseToken = t, core.ItemClient.OpenContainerAsync, List);

    public Task<HostResult<HostItem>> Consume(ICharacterLease lease, string itemId, int count = 1) =>
        Leased(lease, new P.ConsumeRequest { RequestId = Rpc.NewRequestId(), CharacterId = lease.CharacterId, ItemId = itemId, Count = count },
            (r, t) => r.LeaseToken = t, core.ItemClient.ConsumeAsync, i => i.ToSdk());

    public Task<HostResult<IReadOnlyList<HostItem>>> Salvage(ICharacterLease lease, string itemId) =>
        Leased(lease, Action(lease, itemId), Tok, core.ItemClient.SalvageAsync, List);

    public Task<HostResult<IReadOnlyList<HostItem>>> RecordDeath(ICharacterLease lease, Vector3 position = default) =>
        Leased(lease, new P.RecordDeathRequest { RequestId = Rpc.NewRequestId(), CharacterId = lease.CharacterId, Position = position.ToProto() },
            (r, t) => r.LeaseToken = t, core.ItemClient.RecordDeathAsync, List);

    public Task<HostResult<IReadOnlyList<HostItem>>> LootCorpse(ICharacterLease lease) =>
        Leased(lease, new P.LootCorpseRequest { RequestId = Rpc.NewRequestId(), CharacterId = lease.CharacterId },
            (r, t) => r.LeaseToken = t, core.ItemClient.LootCorpseAsync, List);

    public Task<HostResult<IReadOnlyList<HostItem>>> ListWorldItems() =>
        Read((m, d, ct) => core.ItemClient.ListWorldItemsAsync(new P.ListWorldItemsRequest(), m, d, ct), List);
}
