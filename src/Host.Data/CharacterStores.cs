using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using SourceSharp.Host.Abstractions;

namespace SourceSharp.Host.Data;

internal sealed class CharacterStore(Ctx c) : ICharacterStore
{
    public async Task<Account> EnsureAccount(string steamId)
    {
        var row = await c.Db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.SteamId == steamId);
        if (row is not null) return row.ToRecord();
        row = new AccountRow { SteamId = steamId, Created = c.Now };
        c.Db.Accounts.Add(row);
        await c.Save();
        return row.ToRecord();
    }

    public async Task<Account?> GetAccount(string steamId) =>
        (await c.Db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.SteamId == steamId))?.ToRecord();

    public async Task<IReadOnlyList<Account>> ListAccounts(int skip = 0, int take = 100) =>
        (await c.Db.Accounts.AsNoTracking().OrderBy(a => a.SteamId).Skip(skip).Take(take).ToListAsync()).Select(a => a.ToRecord()).ToList();

    public async Task SetBanned(string steamId, bool banned, string? note)
    {
        c.Writable();
        var row = await c.Db.Accounts.FirstOrDefaultAsync(a => a.SteamId == steamId)
                  ?? throw HostRefusal.NotFound("no_account", steamId);
        row.Banned = banned;
        row.Note = note ?? row.Note;
        await c.Save();
    }

    public async Task<IReadOnlyList<CharacterRecord>> List(string account, bool includeDeleted = false) =>
        (await c.Db.Characters.AsNoTracking().Where(x => x.Account == account && (includeDeleted || !x.Deleted)).OrderBy(x => x.Id).ToListAsync())
        .Select(x => x.ToRecord()).ToList();

    public async Task<IReadOnlyList<CharacterRecord>> ListAll(int skip = 0, int take = 100) =>
        (await c.Db.Characters.AsNoTracking().OrderBy(x => x.Id).Skip(skip).Take(take).ToListAsync()).Select(x => x.ToRecord()).ToList();

    public async Task<CharacterRecord?> Get(string id) =>
        (await c.Db.Characters.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id))?.ToRecord();

    public async Task<CharacterRecord> Create(string account, string className, string name, bool hardcore, byte[] sheet, int sheetVersion, int level, long xp)
    {
        c.Writable();
        await EnsureAccount(account);
        var row = new CharacterRow
        {
            Id = c.NewId(), Account = account, ClassName = className, Name = name, Hardcore = hardcore,
            Level = level, Xp = xp, Sheet = sheet, SheetVersion = sheetVersion, Version = 1,
        };
        c.Db.Characters.Add(row);
        await c.Save();
        return row.ToRecord();
    }

    public async Task<CharacterRecord> Update(string id, long expectedVersion, Func<CharacterRecord, CharacterRecord> change)
    {
        c.Writable();
        var row = await c.Db.Characters.FirstOrDefaultAsync(x => x.Id == id) ?? throw HostRefusal.NotFound("no_character", id);
        if (row.Version != expectedVersion)
            throw HostRefusal.Precondition("version_conflict", $"character {id} is at version {row.Version}, not {expectedVersion}");
        row.Apply(change(row.ToRecord()));
        row.Version++;
        await c.Save();
        return row.ToRecord();
    }

    public async Task MarkDeleted(string id)
    {
        c.Writable();
        var row = await c.Db.Characters.FirstOrDefaultAsync(x => x.Id == id) ?? throw HostRefusal.NotFound("no_character", id);
        row.Deleted = true;
        row.Version++;
        await c.Save();
    }

    public async Task<long> AddAustralium(string characterId, long delta, string reason, string? reference)
    {
        c.Writable();
        var row = await c.Db.Characters.FirstOrDefaultAsync(x => x.Id == characterId) ?? throw HostRefusal.NotFound("no_character", characterId);
        if (row.Australium + delta < 0)
            throw HostRefusal.Precondition("insufficient_funds", $"{characterId} holds {row.Australium}, needs {-delta}");
        row.Australium += delta;
        c.Db.AustraliumLedger.Add(new AustraliumRow { CharacterId = characterId, Delta = delta, Reason = reason, Ref = reference, At = c.Now });
        await c.Save();
        return row.Australium;
    }

    public async Task<IReadOnlyList<AustraliumEntry>> AustraliumLedger(string characterId) =>
        (await c.Db.AustraliumLedger.AsNoTracking().Where(x => x.CharacterId == characterId).OrderBy(x => x.Id).ToListAsync())
        .Select(x => x.ToRecord()).ToList();

    public async Task<long> RecomputeAustralium(string characterId)
    {
        c.Writable();
        var row = await c.Db.Characters.FirstOrDefaultAsync(x => x.Id == characterId) ?? throw HostRefusal.NotFound("no_character", characterId);
        var old = row.Australium;
        row.Australium = await AustraliumLedgerSum(characterId);
        await c.Save();
        return old;
    }

    public Task<bool> HasAustraliumRef(string characterId, string reference) =>
        c.Db.AustraliumLedger.AnyAsync(x => x.CharacterId == characterId && x.Ref == reference);

    public async Task<long> AustraliumLedgerSum(string characterId) =>
        await c.Db.AustraliumLedger.Where(x => x.CharacterId == characterId).SumAsync(x => (long?)x.Delta) ?? 0;
}

internal sealed class LeaseStore(Ctx c) : ILeaseStore
{
    public async Task<LeaseOutcome> Acquire(string characterId, string instanceId, TimeSpan ttl)
    {
        c.Writable();
        var now = c.Now;
        var row = await c.Db.Leases.FirstOrDefaultAsync(l => l.CharacterId == characterId);
        if (row is not null)
        {
            if (row.InstanceId != instanceId)
                return new LeaseOutcome.AlreadyLeased(row.InstanceId);
            row.Expires = now + ttl;
            row.Heartbeat = now;
            await c.Save();
            return new LeaseOutcome.Granted(row.ToRecord(), Reentrant: true);
        }
        row = new LeaseRow
        {
            CharacterId = characterId, InstanceId = instanceId, Token = RandomNumberGenerator.GetBytes(32),
            Issued = now, Expires = now + ttl, Heartbeat = now,
        };
        c.Db.Leases.Add(row);
        await c.Save();
        return new LeaseOutcome.Granted(row.ToRecord(), Reentrant: false);
    }

    public async Task<Lease?> Get(string characterId) =>
        (await c.Db.Leases.AsNoTracking().FirstOrDefaultAsync(l => l.CharacterId == characterId))?.ToRecord();

    public async Task<Lease> Verify(string characterId, byte[] token)
    {
        var row = await c.Db.Leases.AsNoTracking().FirstOrDefaultAsync(l => l.CharacterId == characterId);
        if (row is null || !CryptographicOperations.FixedTimeEquals(row.Token, token))
            throw HostRefusal.Precondition("stale_lease", $"no current lease on {characterId} with that token");
        return row.ToRecord();
    }

    public async Task<int> RenewForInstance(string instanceId, TimeSpan ttl)
    {
        c.Writable();
        var now = c.Now;
        var expires = now + ttl;
        return await c.Db.Leases.Where(l => l.InstanceId == instanceId)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.Expires, expires).SetProperty(l => l.Heartbeat, now));
    }

    public async Task<bool> Release(string characterId, byte[]? token)
    {
        c.Writable();
        var row = await c.Db.Leases.FirstOrDefaultAsync(l => l.CharacterId == characterId);
        if (row is null) return false;
        if (token is not null && !CryptographicOperations.FixedTimeEquals(row.Token, token))
            throw HostRefusal.Precondition("stale_lease", $"release of {characterId} with a token that is not current");
        c.Db.Leases.Remove(row);
        await c.Save();
        return true;
    }

    public async Task<IReadOnlyList<Lease>> Expired()
    {
        var now = c.Now;
        return (await c.Db.Leases.AsNoTracking().Where(l => l.Expires < now).ToListAsync()).Select(l => l.ToRecord()).ToList();
    }

    public async Task<IReadOnlyList<Lease>> ForInstance(string instanceId) =>
        (await c.Db.Leases.AsNoTracking().Where(l => l.InstanceId == instanceId).ToListAsync()).Select(l => l.ToRecord()).ToList();

    public async Task<IReadOnlyList<Lease>> All() =>
        (await c.Db.Leases.AsNoTracking().ToListAsync()).Select(l => l.ToRecord()).ToList();
}
