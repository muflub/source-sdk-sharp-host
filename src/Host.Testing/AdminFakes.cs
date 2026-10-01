using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Contracts;

namespace SourceSharp.Host.Testing;

// Fakes of the admin's seams onto the lead's code (Host.Abstractions/AdminServices.cs), for
// the unit tier. Each records its calls so a fact can require the stimulus reached it.

/// <summary>
/// IAdminLedger over the real stores with FakeGameRules' conventions: the stash owner is
/// "&lt;account&gt;:sc|hc" (as Descent's), capacity <see cref="Capacity"/>, the sheet is FakeGameRules' JSON.
/// </summary>
public sealed class AdminFakeLedger : IAdminLedger
{
    public int Capacity { get; set; } = 50;
    public List<string> Calls { get; } = [];
    public AdminReconcileReport? LastReconcile { get; private set; }

    public string StashOwner(string account, bool hardcore) => $"{account}:{(hardcore ? "hc" : "sc")}";

    public (string Account, bool Hardcore)? ParseStashOwner(string ownerId)
    {
        var i = ownerId.LastIndexOf(':');
        if (i <= 0) return null;
        return ownerId[(i + 1)..] switch { "hc" => (ownerId[..i], true), "sc" => (ownerId[..i], false), _ => null };
    }

    public Task<int> StashCapacity(IReadTx tx, string account, bool hardcore) => Task.FromResult(Capacity);

    public async Task<IReadOnlyList<AdminFinding>> Reconcile(IWriteTx tx, bool fixCachedSums)
    {
        Calls.Add($"reconcile:{fixCachedSums}");
        var findings = new List<AdminFinding>();
        foreach (var c in await tx.Characters.ListAll(0, 10_000))
        {
            var sum = await tx.Characters.AustraliumLedgerSum(c.Id);
            if (sum == c.Australium) continue;
            findings.Add(new("australium_cache", c.Id, $"cached {c.Australium}, ledger {sum}"));
            if (fixCachedSums) await tx.Characters.RecomputeAustralium(c.Id);
        }
        foreach (var l in await tx.Leases.All())
            if (await tx.Instances.Get(l.InstanceId) is not { Terminal: false })
                findings.Add(new("lease_orphan", l.CharacterId, $"instance {l.InstanceId} is not live"));
        LastReconcile = new AdminReconcileReport(tx.Now, findings, fixCachedSums);
        return findings;
    }

    public async Task<int> CancelTrade(IWriteTx tx, string tradeId)
    {
        Calls.Add($"cancel_trade:{tradeId}");
        var t = await tx.Trades.Get(tradeId) ?? throw HostRefusal.NotFound("no_trade", tradeId);
        var escrow = await tx.Items.Query(new ItemQuery(OwnerKind: OwnerKind.TradeEscrow, OwnerId: t.Id, Take: 1000));
        foreach (var i in escrow)
            await tx.Items.Move(new ItemMove(i.Id, OwnerKind.TradeEscrow, OwnerKind.Character, t.OfferA.Contains(i.Id) ? t.A : t.B, "trade_cancel", Slot: i.Slot));
        await tx.Trades.Save(t with { State = TradeStatus.Cancelled });
        return escrow.Count;
    }

    public async Task<ItemRecord> ReleaseToStash(IWriteTx tx, string entryId)
    {
        Calls.Add($"release_to_stash:{entryId}");
        var e = await tx.LostAndFound.Get(entryId) ?? throw HostRefusal.NotFound("no_entry", entryId);
        var c = await tx.Characters.Get(e.CharacterId) ?? throw HostRefusal.NotFound("no_character", e.CharacterId);
        var owner = StashOwner(c.Account, c.Hardcore);
        var used = (await tx.Items.Query(new ItemQuery(OwnerKind: OwnerKind.Stash, OwnerId: owner, Take: 10_000))).Select(i => i.Slot).ToHashSet();
        var slot = Enumerable.Range(0, Capacity).FirstOrDefault(s => !used.Contains(s), -1);
        if (slot < 0) throw HostRefusal.Exhausted("no_capacity", "the stash is full");
        await tx.LostAndFound.Remove(entryId);
        return await tx.Items.Move(new ItemMove(e.ItemId, OwnerKind.LostAndFound, OwnerKind.Stash, owner, "admin_release", Slot: slot));
    }

    public async Task<CharacterRecord> SetFallen(IWriteTx tx, string characterId, bool fallen)
    {
        Calls.Add($"set_fallen:{characterId}:{fallen}");
        var c = await tx.Characters.Get(characterId) ?? throw HostRefusal.NotFound("no_character", characterId);
        var sheet = FakeGameRules.SheetCodec.Read(new CharacterSheet(c.Sheet, c.SheetVersion)) with { Fallen = fallen };
        var bytes = FakeGameRules.SheetCodec.Write(sheet).Data;
        return await tx.Characters.Update(characterId, c.Version, x => x with { Fallen = fallen, Sheet = bytes });
    }
}

/// <summary>The gateway control, recorded. <see cref="Fail"/> makes the next call throw it.</summary>
public sealed class AdminFakeGateway : IAdminGateway
{
    public List<string> Calls { get; } = [];
    public Exception? Fail { get; set; }
    public AdminGatewaySnapshot Current { get; set; } = new(true, null, [], 0, "Descent hub (0/32)", [], 0, 0);

    Task Record(string call)
    {
        if (Fail is { } f) { Fail = null; throw f; }
        Calls.Add(call);
        return Task.CompletedTask;
    }

    public Task<AdminGatewaySnapshot> Snapshot(CancellationToken ct = default) => Task.FromResult(Current);
    public Task CloseSession(string sessionId, string reason, CancellationToken ct = default) => Record($"close:{sessionId}");
    public Task ForceToHub(string sessionId, CancellationToken ct = default) => Record($"hub:{sessionId}");
    public Task BanAddress(string address, string? note, CancellationToken ct = default) => Record($"ban:{address}");
    public Task ReleasePin(string steamId, CancellationToken ct = default) => Record($"unpin:{steamId}");
}

/// <summary>The module registry over the real store: approve and quarantine set the state; delete is recorded.</summary>
public sealed class AdminFakeModules(IHostData data) : IAdminModules
{
    public List<string> Calls { get; } = [];

    public async Task Approve(string sha256, CancellationToken ct = default)
    {
        Calls.Add($"approve:{sha256}");
        await data.WriteAsync((tx, _) => tx.Modules.SetState(sha256, ModuleState.Approved), ct);
    }

    public async Task Quarantine(string sha256, CancellationToken ct = default)
    {
        Calls.Add($"quarantine:{sha256}");
        await data.WriteAsync((tx, _) => tx.Modules.SetState(sha256, ModuleState.Quarantined), ct);
    }

    public Task Delete(string sha256, CancellationToken ct = default)
    {
        Calls.Add($"delete:{sha256}");
        return Task.CompletedTask;
    }
}

/// <summary>Backups in memory: RunNow adds one of <see cref="Bytes"/> bytes.</summary>
public sealed class AdminFakeBackups(TimeProvider clock) : IAdminBackups
{
    readonly List<AdminBackup> _all = [];
    public byte[] Bytes { get; set; } = [0x53, 0x51, 0x4c];
    public long DatabaseBytes { get; set; } = 4096;
    public List<string> Restored { get; } = [];

    public Task<IReadOnlyList<AdminBackup>> List(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AdminBackup>>([.. _all]);

    public Task<AdminBackup> RunNow(CancellationToken ct = default)
    {
        var b = new AdminBackup($"host-{clock.GetUtcNow():yyyyMMdd-HHmmss}-{_all.Count}.db", Bytes.Length, clock.GetUtcNow(), "manual");
        _all.Add(b);
        return Task.FromResult(b);
    }

    public Stream? Open(string name) => _all.Any(b => b.Name == name) ? new MemoryStream(Bytes) : null;

    public Task<string> RestoreToFile(string name, CancellationToken ct = default)
    {
        if (_all.All(b => b.Name != name)) throw HostRefusal.NotFound("no_backup", name);
        Restored.Add(name);
        return Task.FromResult($"/data/restore-{name}");
    }
}

/// <summary>
/// The instance manager's admin edge, recorded: Drain moves the row to draining and audits as
/// the manager does; Kick and Exec audit and record; the game-server calls are not used by the admin.
/// </summary>
public sealed class AdminFakeLifecycle(IHostData data) : IInstanceLifecycle
{
    public List<string> Calls { get; } = [];
    /// <summary>When false, Kick and Exec refuse with no_stream (as the manager does for a pod with no stream).</summary>
    public bool HasStream { get; set; } = true;

    public async Task<InstanceRecord> Drain(string instanceId, string reason, string actor, CancellationToken ct = default)
    {
        Calls.Add($"drain:{instanceId}");
        return await data.WriteAsync(async (tx, _) =>
        {
            var row = await tx.Instances.Update(instanceId, r => r with { State = InstanceState.Draining, Reason = reason });
            await tx.Audit.Write("instance.draining", instanceId, null, new { reason }, actor);
            return row;
        }, ct);
    }

    public async Task Kick(string instanceId, string steamId, string reason, string actor, CancellationToken ct = default)
    {
        Calls.Add($"kick:{instanceId}:{steamId}");
        await data.WriteAsync(async (tx, _) => { await tx.Audit.Write("instance.kick", instanceId, null, new { steamId, reason }, actor); return true; }, ct);
        if (!HasStream) throw HostRefusal.Precondition("no_stream", $"instance {instanceId} has no open stream");
    }

    public async Task Exec(string instanceId, string command, string actor, CancellationToken ct = default)
    {
        Calls.Add($"exec:{instanceId}:{command}");
        await data.WriteAsync(async (tx, _) => { await tx.Audit.Write("instance.exec", instanceId, null, new { command }, actor); return true; }, ct);
        if (!HasStream) throw HostRefusal.Precondition("no_stream", $"instance {instanceId} has no open stream");
    }

    public Task ResumeHubs(string actor, CancellationToken ct = default)
    {
        Calls.Add("resume_hubs");
        return Task.CompletedTask;
    }

    public Task<LevelRequestResult> RequestLevel(int depth, string? partyId, string? levelHash, long? packVersion, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<InstanceRecord> AssignLevel(string instanceId, string levelHash, long packVersion, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<InstanceRecord?> Authenticate(string instanceId, string token, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<InstanceRecord> OnBooting(string instanceId, string rulesSha256, string? modImageDigest, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<MapReadyResult> OnMapReady(string instanceId, int port, CancellationToken ct = default) => throw new NotSupportedException();
    public Task OnStreamOpened(string instanceId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task OnHeartbeat(string instanceId, int players, CancellationToken ct = default) => throw new NotSupportedException();
    public Task OnStreamClosed(string instanceId, CancellationToken ct = default) => throw new NotSupportedException();
}
