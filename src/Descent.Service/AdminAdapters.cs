using System.Text.Json;
using Descent.Service.Gateway;
using Descent.Service.Ledger;
using Descent.Service.Workers;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Contracts;
using SourceSharp.Host.Data;
using SourceSharp.Host.Modules;
using G = SourceSharp.Host.Proto.Gateway;

namespace Descent.Service;

/// <summary>The admin UI's ledger operations (lane A's IAdminLedger), over DescentLedger. The admin writes the audit rows.</summary>
public sealed class AdminLedger(IRulesProvider rules, MaintenanceState maintenance) : IAdminLedger
{
    AdminReconcileReport? _last;

    public string StashOwner(string account, bool hardcore) => DescentLedger.StashOwner(account, hardcore);

    public (string Account, bool Hardcore)? ParseStashOwner(string ownerId)
    {
        var i = ownerId.LastIndexOf(':');
        if (i <= 0) return null;
        return ownerId[(i + 1)..] switch { "hc" => (ownerId[..i], true), "sc" => (ownerId[..i], false), _ => null };
    }

    public async Task<int> StashCapacity(IReadTx tx, string account, bool hardcore)
    {
        if (rules.Current is not { } r) return 0;
        var living = (await tx.Characters.List(account)).Where(c => c.Hardcore == hardcore && !c.Fallen)
            .Select(c => new CharacterSheet(c.Sheet, c.SheetVersion)).ToList();
        return r.StashSlots(living);
    }

    public async Task<IReadOnlyList<AdminFinding>> Reconcile(IWriteTx tx, bool fixCachedSums)
    {
        var findings = (await DescentLedger.Reconcile(tx, fixCachedSums)).Select(f => new AdminFinding(f.Kind, f.Target, f.Detail)).ToList();
        _last = new AdminReconcileReport(tx.Now, findings, fixCachedSums);
        return findings;
    }

    public AdminReconcileReport? LastReconcile => _last ?? (maintenance.LastReconcile is { } at
        ? new AdminReconcileReport(at, maintenance.Findings.Select(f => new AdminFinding(f.Kind, f.Target, f.Detail)).ToList(), true)
        : null);

    public async Task<int> CancelTrade(IWriteTx tx, string tradeId)
    {
        var t = await tx.Trades.Get(tradeId) ?? throw HostRefusal.NotFound("no_trade", tradeId);
        if (t.State is TradeStatus.Committed or TradeStatus.Cancelled) throw HostRefusal.Precondition("trade_state", $"trade {tradeId} is {t.State}");
        var n = await DescentLedger.ReturnEscrow(tx, t);
        await tx.Trades.Save(t with { State = TradeStatus.Cancelled });
        return n;
    }

    /// <summary>A Lost &amp; Found entry into the owner's stash with no fee (an admin courtesy); the stash must have room.</summary>
    public async Task<ItemRecord> ReleaseToStash(IWriteTx tx, string entryId)
    {
        var e = await tx.LostAndFound.Get(entryId) ?? throw HostRefusal.NotFound("no_item", entryId);
        var ch = await tx.Characters.Get(e.CharacterId) ?? throw HostRefusal.NotFound("no_character", e.CharacterId);
        var owner = StashOwner(ch.Account, ch.Hardcore);
        var cap = await StashCapacity(tx, ch.Account, ch.Hardcore);
        var used = (await tx.Items.Query(new ItemQuery(OwnerKind: OwnerKind.Stash, OwnerId: owner, Take: 10_000))).Select(i => i.Slot).ToHashSet();
        var slot = Enumerable.Range(0, cap).FirstOrDefault(s => !used.Contains(s), -1);
        if (slot < 0) throw HostRefusal.Precondition("no_capacity", $"stash {owner} is full ({cap})");
        await tx.LostAndFound.Remove(e.Id);
        return await tx.Items.Move(new ItemMove(e.ItemId, OwnerKind.LostAndFound, OwnerKind.Stash, owner, "admin_release", Slot: slot));
    }

    /// <summary>The row's flag. The sheet is the rules module's; the next checkpoint carries the game's own view.</summary>
    public async Task<CharacterRecord> SetFallen(IWriteTx tx, string characterId, bool fallen)
    {
        var ch = await tx.Characters.Get(characterId) ?? throw HostRefusal.NotFound("no_character", characterId);
        return await tx.Characters.Update(characterId, ch.Version, c => c with { Fallen = fallen });
    }
}

/// <summary>The admin's gateway controls (§11 Gateway page) over the control client and the registry.</summary>
public sealed class AdminGateway(IGatewayControl control, IHostData data, ServiceOptions options, GatewayState state, TimeProvider clock) : IAdminGateway
{
    public const string BansKey = "gateway.bans";

    public async Task<AdminGatewaySnapshot> Snapshot(CancellationToken ct = default)
    {
        var bans = await Bans(data, ct);
        var routes = await data.ReadAsync(async (tx, _) => (await tx.Sessions.Open())
            .Select(s => new AdminRoute(s.Id, s.ClientAddr, s.SteamId, s.Backend, 0, 0, s.Opened)).ToList(), ct);
        if (control is not GatewayControlClient client || string.IsNullOrEmpty(options.Listen.GatewayControl))
            return new AdminGatewaySnapshot(false, "no gateway configured", routes, 0, null, bans, 0, 0);
        try
        {
            var snap = await client.Snapshot(ct);
            return new AdminGatewaySnapshot(state.Reachable(clock.GetUtcNow()), null,
                snap.Sessions.Select(s => new AdminRoute(s.SessionId, s.ClientAddr, s.Steamid.Length > 0 ? s.Steamid : null, s.Backend,
                    s.BytesIn, s.BytesOut, DateTimeOffset.FromUnixTimeMilliseconds(s.OpenedUnixMs))).ToList(),
                snap.AdmissionQueue, snap.ServerInfo?.Name, bans, 0, 0);
        }
        catch (Grpc.Core.RpcException e) { return new AdminGatewaySnapshot(false, e.Status.Detail, routes, 0, null, bans, 0, 0); }
    }

    async Task<SessionRecord> Session(string id, CancellationToken ct) =>
        await data.ReadAsync((tx, _) => tx.Sessions.Get(id), ct) ?? throw HostRefusal.NotFound("no_session", id);

    public async Task CloseSession(string sessionId, string reason, CancellationToken ct = default) =>
        await control.CloseSession((await Session(sessionId, ct)).ClientAddr, reason, ct);

    public async Task ForceToHub(string sessionId, CancellationToken ct = default)
    {
        var s = await Session(sessionId, ct);
        var hub = await data.ReadAsync(async (tx, _) =>
            (await tx.Instances.NonTerminal()).FirstOrDefault(i => i.Kind == InstanceKind.Hub && i.State == InstanceState.Live && i.PodIp is not null), ct)
            ?? throw HostRefusal.Exhausted("no_hub", "no live hub");
        await control.SetRoute(s.ClientAddr, GatewayControlClient.Backend(hub, options), s.SteamId, holdUntilReady: false, ct);
    }

    /// <summary>§6.1: bans are stored in the service and enforced by the gateway; the next SyncTable carries them.</summary>
    public async Task BanAddress(string address, string? note, CancellationToken ct = default)
    {
        var bans = (await Bans(data, ct)).Append(address).Distinct().ToList();
        await data.WriteAsync(async (tx, _) => { await tx.Settings.Set(BansKey, JsonSerializer.Serialize(bans), "admin@localhost"); return true; }, ct);
        await control.SyncTable(ct);
    }

    public static async Task<IReadOnlyList<string>> Bans(IHostData data, CancellationToken ct) =>
        await data.ReadAsync(async (tx, _) => (await tx.Settings.Get(BansKey)) is { } s ? JsonSerializer.Deserialize<List<string>>(s.ValueJson) ?? [] : [], ct);

    public Task ReleasePin(string steamId, CancellationToken ct = default) =>
        throw new HostRefusal(RefusalCode.Unimplemented, "pins_in_pods", "D-H10: a player's loopback address lives in the pod's launcher and ends with the instance");
}

/// <summary>The admin's module actions (§11 Modules) over Host.Modules' registry.</summary>
public sealed class AdminModules(IRulesModuleRegistry registry, ServiceOptions options) : IAdminModules
{
    public Task Approve(string sha256, CancellationToken ct = default) => registry.Approve(sha256, options.Admin.Actor, ct);
    public Task Quarantine(string sha256, CancellationToken ct = default) => registry.Quarantine(sha256, options.Admin.Actor, ct);
    public Task Delete(string sha256, CancellationToken ct = default) => registry.Delete(sha256, options.Admin.Actor, ct);
}

/// <summary>The admin's backups (§11 Backups) over the maintenance worker's VACUUM INTO files.</summary>
public sealed class AdminBackups(Maintenance maintenance, HostData data, ServiceOptions options) : IAdminBackups
{
    long _bytes;
    string Dir => options.Data.BackupPath;

    public async Task<IReadOnlyList<AdminBackup>> List(CancellationToken ct = default)
    {
        _bytes = await data.SizeAsync(ct);
        if (!Directory.Exists(Dir)) return [];
        return Directory.GetFiles(Dir, "*.db").Select(f => new FileInfo(f))
            .Select(f => new AdminBackup(f.Name, f.Length, f.LastWriteTimeUtc, f.Name.StartsWith("daily-") ? "daily" : f.Name.StartsWith("restore-") ? "restore" : "hourly"))
            .OrderByDescending(b => b.At).ToList();
    }

    public async Task<AdminBackup> RunNow(CancellationToken ct = default)
    {
        var file = new FileInfo(await maintenance.Backup(ct));
        return new AdminBackup(file.Name, file.Length, file.LastWriteTimeUtc, "hourly");
    }

    string? PathOf(string name) =>
        name.Length > 0 && Path.GetFileName(name) == name && name.EndsWith(".db") && File.Exists(Path.Combine(Dir, name)) ? Path.Combine(Dir, name) : null;

    public Stream? Open(string name) => PathOf(name) is { } p ? File.OpenRead(p) : null;

    public async Task<string> RestoreToFile(string name, CancellationToken ct = default)
    {
        var source = PathOf(name) ?? throw HostRefusal.NotFound("no_backup", name);
        var target = Path.Combine(Path.GetDirectoryName(options.Data.Path) ?? Dir, $"restore-{name}");
        await using (var from = File.OpenRead(source))
        await using (var to = File.Create(target))
            await from.CopyToAsync(to, ct);
        return target;
    }

    public long DatabaseBytes => _bytes;
}
