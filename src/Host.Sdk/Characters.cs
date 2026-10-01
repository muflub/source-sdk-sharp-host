using System.Collections.Concurrent;
using Google.Protobuf;
using P = SourceSharp.Host.Proto;

namespace SourceSharp.Host.Sdk;

public enum LeaseLostReason
{
    /// <summary>The service revoked it (LeaseRevoked on the stream: expiry, admin, kick-and-edit).</summary>
    Revoked,
    /// <summary>A write was refused with stale_lease: someone else holds the character now.</summary>
    Stale,
    /// <summary>Fail closed: heartbeats could not reach the host for longer than the lease TTL.</summary>
    Unreachable,
}

/// <summary>
/// A character this instance leases (§4.3). The fencing token rides inside every write; the
/// heartbeat renews it server-side; a released or lost lease refuses every call locally, and
/// <see cref="LeaseLost"/> (raised in Pump) tells the game to stop writing under it.
/// </summary>
public interface ICharacterLease
{
    string CharacterId { get; }
    /// <summary>The character as of the lease (and the last checkpoint's version).</summary>
    HostCharacter Character { get; }
    /// <summary>The version the next Checkpoint expects.</summary>
    long Version { get; }
    bool IsHeld { get; }
    bool IsReleased { get; }
    bool IsLost { get; }
    /// <summary>Until when the SDK can vouch that the service still holds this lease.</summary>
    DateTimeOffset ConfirmedUntil { get; }
    event Action<ICharacterLease, LeaseLostReason, string>? LeaseLost;

    /// <summary>
    /// Saves the sheet (opaque) at the tracked version. Stale lease, version conflict and a failed
    /// rule come back typed; unreachable is <see cref="HostError.Unreachable"/>, never an acknowledgement.
    /// Tiers the service swept for a level jump are refilled in the reserve.
    /// </summary>
    Task<HostResult<CheckpointOutcome>> Checkpoint(OpaquePayload sheet, OpaquePayload? evidence = null);
    Task<HostResult<HostCharacter>> SetReachedDepth(int depth);
    /// <summary>The final call on hop or logout, after the last Checkpoint. The lease refuses use from this moment.</summary>
    Task<HostResult> Release();
}

/// <summary>§6.2. List, Create and Delete are the hub's; Lease is anyone's.</summary>
public interface ICharacters
{
    Task<HostResult<IReadOnlyList<HostCharacter>>> List(string account);
    Task<HostResult<HostCharacter>> Create(string account, string className, string name, bool hardcore = false);
    Task<HostResult> Delete(string characterId);
    Task<HostResult<ICharacterLease>> Lease(string characterId);
    Task<HostResult<HostCharacter>> GetSheet(string characterId);
    /// <summary>The lease this instance holds on a character, if any.</summary>
    ICharacterLease? Held(string characterId);
}

internal sealed class CharacterLease : ICharacterLease
{
    const int Held = 0, Released = 1, Lost = 2;
    readonly SdkCore _core;
    int _state;
    long _confirmedUntil;

    public CharacterLease(SdkCore core, string characterId, ByteString token, HostCharacter character, DateTimeOffset confirmedUntil)
    {
        _core = core;
        CharacterId = characterId;
        Token = token;
        Character = character;
        Version = character.Version;
        _confirmedUntil = confirmedUntil.UtcTicks;
    }

    public string CharacterId { get; }
    public ByteString Token { get; }
    public HostCharacter Character { get; private set; }
    public long Version { get; private set; }
    public bool IsHeld => Volatile.Read(ref _state) == Held;
    public bool IsReleased => Volatile.Read(ref _state) == Released;
    public bool IsLost => Volatile.Read(ref _state) == Lost;
    public DateTimeOffset ConfirmedUntil => new(Interlocked.Read(ref _confirmedUntil), TimeSpan.Zero);
    public event Action<ICharacterLease, LeaseLostReason, string>? LeaseLost;
    /// <summary>The reserve cache bound to this lease (§5.2a), closed with it. Main thread only.</summary>
    internal Reserve? Reserve { get; set; }

    public void Confirm(DateTimeOffset until)
    {
        var t = until.UtcTicks;
        long cur;
        while ((cur = Interlocked.Read(ref _confirmedUntil)) < t)
            if (Interlocked.CompareExchange(ref _confirmedUntil, t, cur) == cur) break;
    }

    public void Refresh(HostCharacter c) { Character = c; Version = c.Version; }

    /// <summary>Null when the lease may be used; the local refusal otherwise.</summary>
    public HostRefusal? Refusal() => Volatile.Read(ref _state) switch
    {
        Held => null,
        Released => new HostRefusal(HostError.LeaseReleased, "lease_released", $"{CharacterId}'s lease was released", Local: true),
        _ => new HostRefusal(HostError.LeaseLost, "lease_lost", $"{CharacterId}'s lease was lost", Local: true),
    };

    /// <summary>Held → Lost once; the reserve closes and LeaseLost is raised inside Pump.</summary>
    public bool Lose(LeaseLostReason reason, string detail)
    {
        if (Interlocked.CompareExchange(ref _state, Lost, Held) != Held) return false;
        _core.Leases.Forget(this);
        _core.Main.Post(() =>
        {
            Reserve?.Close();
            LeaseLost?.Invoke(this, reason, detail);
        });
        return true;
    }

    bool MarkReleased()
    {
        if (Interlocked.CompareExchange(ref _state, Released, Held) != Held) return false;
        _core.Leases.Forget(this);
        Reserve?.Close(); // Release() is called on the main thread
        return true;
    }

    /// <summary>A write under this lease: refused locally unless held; stale_lease from the service loses the lease.</summary>
    public Task<HostResult<T>> Write<T>(Func<ByteString, Task<HostResult<T>>> call, Action<HostResult<T>>? onMain = null)
    {
        if (Refusal() is { } local) return Task.FromResult(HostResult<T>.Refused(local));
        return _core.Background(async () =>
        {
            var r = await call(Token).ConfigureAwait(false);
            if (r.Error == HostError.StaleLease) Lose(LeaseLostReason.Stale, r.Refusal!.Detail);
            return r;
        }, onMain);
    }

    public Task<HostResult<CheckpointOutcome>> Checkpoint(OpaquePayload sheet, OpaquePayload? evidence = null)
    {
        var expected = Version;
        var request = new P.CheckpointRequest
        {
            RequestId = Rpc.NewRequestId(), CharacterId = CharacterId, Sheet = sheet.ToProto(), Evidence = (evidence ?? OpaquePayload.Empty).ToProto(),
            ExpectedVersion = expected,
        };
        return Write(async token =>
        {
            request.LeaseToken = token;
            var r = await _core.Rpc.Call((m, d, ct) => _core.CharacterClient.CheckpointAsync(request, m, d, ct)).ConfigureAwait(false);
            return r.Map(v => new CheckpointOutcome(v.Version, v.StaleReserveTiers.ToList()));
        }, r =>
        {
            if (!r.Ok) return;
            Version = r.Value.Version;
            Character = Character with { Sheet = sheet, Version = r.Value.Version };
            if (r.Value.StaleReserveTiers.Count > 0) Reserve?.ReplaceStale(r.Value.StaleReserveTiers);
        });
    }

    public Task<HostResult<HostCharacter>> SetReachedDepth(int depth)
    {
        var request = new P.SetReachedDepthRequest { RequestId = Rpc.NewRequestId(), CharacterId = CharacterId, Depth = depth };
        return Write(async token =>
        {
            request.LeaseToken = token;
            return (await _core.Rpc.Call((m, d, ct) => _core.CharacterClient.SetReachedDepthAsync(request, m, d, ct)).ConfigureAwait(false)).Map(c => c.ToSdk());
        }, r => { if (r.Ok) Refresh(r.Value); });
    }

    public Task<HostResult> Release()
    {
        if (Refusal() is { } local) return Task.FromResult(HostResult.Refused(local));
        MarkReleased();
        var request = new P.ReleaseRequest { RequestId = Rpc.NewRequestId(), CharacterId = CharacterId, LeaseToken = Token };
        return _core.Background(async () =>
            (await _core.Rpc.Call((m, d, ct) => _core.CharacterClient.ReleaseAsync(request, m, d, ct)).ConfigureAwait(false)).Untyped());
    }
}

/// <summary>The leases this instance holds, for the heartbeat's confirmations and the fail-closed watchdog.</summary>
internal sealed class LeaseRegistry(SdkCore core)
{
    readonly ConcurrentDictionary<string, CharacterLease> _held = new();

    public CharacterLease? Get(string characterId) => _held.TryGetValue(characterId, out var l) ? l : null;
    public void Add(CharacterLease lease) => _held[lease.CharacterId] = lease;
    public void Forget(CharacterLease lease) => _held.TryRemove(new(lease.CharacterId, lease));
    public int Count => _held.Count;

    public void Confirm(DateTimeOffset until)
    {
        foreach (var l in _held.Values) l.Confirm(until);
    }

    public void ExpireUnconfirmed(DateTimeOffset now)
    {
        foreach (var l in _held.Values)
            if (now > l.ConfirmedUntil)
                l.Lose(LeaseLostReason.Unreachable, $"no heartbeat acknowledged since {l.ConfirmedUntil - core.Options.LeaseTtl:O}");
    }

    public void Revoked(string characterId, string reason) => Get(characterId)?.Lose(LeaseLostReason.Revoked, reason);
}

internal sealed class Characters(SdkCore core) : ICharacters
{
    public Task<HostResult<IReadOnlyList<HostCharacter>>> List(string account) => core.Background(async () =>
    {
        var request = new P.ListCharactersRequest { Account = account };
        var r = await core.Rpc.Call((m, d, ct) => core.CharacterClient.ListAsync(request, m, d, ct)).ConfigureAwait(false);
        return r.Map(v => (IReadOnlyList<HostCharacter>)v.Characters.Select(c => c.ToSdk()).ToList());
    });

    public Task<HostResult<HostCharacter>> Create(string account, string className, string name, bool hardcore = false) => core.Background(async () =>
    {
        var request = new P.CreateCharacterRequest { RequestId = Rpc.NewRequestId(), Account = account, ClassName = className, Name = name, Hardcore = hardcore };
        return (await core.Rpc.Call((m, d, ct) => core.CharacterClient.CreateAsync(request, m, d, ct)).ConfigureAwait(false)).Map(c => c.ToSdk());
    });

    public Task<HostResult> Delete(string characterId) => core.Background(async () =>
    {
        var request = new P.DeleteCharacterRequest { RequestId = Rpc.NewRequestId(), CharacterId = characterId };
        return (await core.Rpc.Call((m, d, ct) => core.CharacterClient.DeleteAsync(request, m, d, ct)).ConfigureAwait(false)).Untyped();
    });

    public Task<HostResult<HostCharacter>> GetSheet(string characterId) => core.Background(async () =>
    {
        var request = new P.GetSheetRequest { CharacterId = characterId };
        return (await core.Rpc.Call((m, d, ct) => core.CharacterClient.GetSheetAsync(request, m, d, ct)).ConfigureAwait(false)).Map(c => c.ToSdk());
    });

    public ICharacterLease? Held(string characterId) => core.Leases.Get(characterId);

    public Task<HostResult<ICharacterLease>> Lease(string characterId) => core.Background(async () =>
    {
        var request = new P.LeaseRequest { RequestId = Rpc.NewRequestId(), CharacterId = characterId };
        var sent = core.Options.Clock.UtcNow;
        var r = await core.Rpc.Call((m, d, ct) => core.CharacterClient.LeaseAsync(request, m, d, ct)).ConfigureAwait(false);
        if (!r.Ok) return HostResult<ICharacterLease>.Refused(r.Refusal!);
        // Re-entrant: the same instance gets the same token, and the same object while it is held.
        if (core.Leases.Get(characterId) is { } held && held.Token.Equals(r.Value.Token))
        {
            held.Confirm(sent + core.Options.LeaseTtl);
            return HostResult<ICharacterLease>.Success(held);
        }
        var lease = new CharacterLease(core, characterId, r.Value.Token, r.Value.Character.ToSdk(), sent + core.Options.LeaseTtl);
        core.Leases.Add(lease);
        return HostResult<ICharacterLease>.Success(lease);
    });
}
