using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using SourceSharp.Host.Abstractions;
using P = SourceSharp.Host.Proto;

namespace TF2.Descent.Service.Api;

/// <summary>
/// D-H17: the key/value store for game servers. Who may reach which owner is decided here, from
/// host.db's rows (read only); the bytes live in store.db. A server reaches its own instance, its
/// own level copy, a character it holds the lease of, and TOWN; a hub may also name any claimed or
/// running level copy, so town can tell a level where an arriving player spawns. Writes to TOWN
/// tell the hubs, and a hub's write to a level tells that level's running instance (StoreChanged).
/// </summary>
public sealed class StoreApi(IHostData data, IKeyValueStore store, IInstanceCommands commands) : P.StoreService.StoreServiceBase
{
    public const string RefusedAction = "store.refused";

    public override Task<P.StoreGetResponse> Get(P.StoreGetRequest request, ServerCallContext context) =>
        Run(context, request.Ref, request.Key, async (owner, _) =>
        {
            var v = await store.Get(owner, request.Key, context.CancellationToken);
            return v is null ? new P.StoreGetResponse() : new P.StoreGetResponse { Found = true, Value = ByteString.CopyFrom(v.Value), Version = v.Version };
        });

    public override Task<P.StoreSetResponse> Set(P.StoreSetRequest request, ServerCallContext context) =>
        Run(context, request.Ref, request.Key, async (owner, caller) =>
        {
            var version = await store.Set(owner, request.Key, request.Value.ToByteArray(), request.HasExpectedVersion ? request.ExpectedVersion : null, context.CancellationToken);
            await Notify(caller, owner, request.Key, context.CancellationToken);
            return new P.StoreSetResponse { Version = version };
        });

    public override Task<P.StoreDeleteResponse> Delete(P.StoreDeleteRequest request, ServerCallContext context) =>
        Run(context, request.Ref, request.Key, async (owner, caller) =>
        {
            var deleted = await store.Delete(owner, request.Key, request.HasExpectedVersion ? request.ExpectedVersion : null, context.CancellationToken);
            if (deleted) await Notify(caller, owner, request.Key, context.CancellationToken);
            return new P.StoreDeleteResponse { Deleted = deleted };
        });

    public override Task<P.StoreListResponse> List(P.StoreListRequest request, ServerCallContext context) =>
        Run(context, request.Ref, request.Prefix, async (owner, _) =>
        {
            var take = request.PageSize is > 0 and <= 500 ? request.PageSize : 100;
            var keys = await store.List(owner, request.Prefix, request.PageToken.Length == 0 ? null : request.PageToken, take + 1, context.CancellationToken);
            var r = new P.StoreListResponse();
            r.Keys.AddRange(keys.Take(take).Select(k => new P.StoreKey { Key = k.Key, Version = k.Version, Size = k.Size }));
            if (keys.Count > take) r.NextPageToken = keys[take - 1].Key;
            return r;
        });

    async Task<T> Run<T>(ServerCallContext context, P.StoreRef? reference, string key, Func<StoreOwner, InstanceRecord, Task<T>> work)
    {
        var caller = context.Caller();
        StoreOwner? owner = null;
        try
        {
            owner = await Owner(caller, reference ?? new P.StoreRef(), context.CancellationToken);
            return await work(owner.Value, caller);
        }
        catch (HostRefusal e)
        {
            // Limit refusals are audited (a server filling its store is worth seeing); version
            // mismatches and access refusals are ordinary answers.
            if (owner is { } o && e.Reason is "store_full" or "value_too_large" or "key_too_long")
                await data.WriteAsync(async (tx, _) =>
                {
                    await tx.Audit.Write(RefusedAction, o.ToString(), null, new { key, reason = e.Reason }, $"instance:{caller.Id}");
                    return true;
                }, CancellationToken.None);
            throw Api.ToRpc(e);
        }
    }

    Task<StoreOwner> Owner(InstanceRecord caller, P.StoreRef r, CancellationToken ct) => (StoreScope)(int)r.Scope switch
    {
        StoreScope.Instance => Task.FromResult(new StoreOwner(StoreScope.Instance, caller.Id)),
        StoreScope.Town => Task.FromResult(StoreOwner.Town),
        StoreScope.Level => LevelOwner(caller, r.LevelId, ct),
        StoreScope.Character => CharacterOwner(caller, r.CharacterId, r.LeaseToken.ToByteArray(), ct),
        _ => throw new HostRefusal(RefusalCode.InvalidArgument, "bad_scope", $"store scope {r.Scope}"),
    };

    async Task<StoreOwner> LevelOwner(InstanceRecord caller, string levelId, CancellationToken ct)
    {
        if (caller.Kind == InstanceKind.Level)
        {
            var own = caller.LevelCopy ?? throw HostRefusal.Precondition("no_level", $"{caller.Id} runs no level yet");
            if (levelId.Length > 0 && levelId != own)
                throw HostRefusal.Denied("not_your_level", $"{caller.Id} runs {own}, not {levelId}");
            return new StoreOwner(StoreScope.Level, own);
        }
        if (levelId.Length == 0) throw HostRefusal.Precondition("no_level", "a hub names the level_id it addresses");
        // A hub reaches a level copy someone claims or something runs: never one nothing would clean up.
        var known = await data.ReadAsync(async (tx, _) => await tx.LevelClaims.ClaimsOnCopy(levelId) > 0
            || (await tx.Instances.NonTerminal()).Any(i => i.LevelCopy == levelId), ct);
        if (!known) throw HostRefusal.NotFound("no_level", $"no claim or instance on level {levelId}");
        return new StoreOwner(StoreScope.Level, levelId);
    }

    async Task<StoreOwner> CharacterOwner(InstanceRecord caller, string characterId, byte[] token, CancellationToken ct)
    {
        var lease = await data.ReadAsync((tx, _) => tx.Leases.Get(characterId), ct);
        if (lease is null || !CryptographicOperations.FixedTimeEquals(lease.Token, token))
            throw HostRefusal.Precondition("stale_lease", $"no current lease on {characterId} with that token");
        if (lease.InstanceId != caller.Id)
            throw HostRefusal.Denied("wrong_instance", $"{characterId} is leased by {lease.InstanceId}, not {caller.Id}");
        return new StoreOwner(StoreScope.Character, characterId);
    }

    /// <summary>TOWN writes reach the other hubs; a hub's write to a level reaches that level's running instance.</summary>
    async Task Notify(InstanceRecord caller, StoreOwner owner, string key, CancellationToken ct)
    {
        if (owner.Scope is not (StoreScope.Town or StoreScope.Level)) return;
        if (owner.Scope == StoreScope.Level && caller.Kind != InstanceKind.Hub) return;
        var running = await data.ReadAsync((tx, _) => tx.Instances.NonTerminal(), ct);
        var targets = owner.Scope == StoreScope.Town
            ? running.Where(i => i.Kind == InstanceKind.Hub)
            : running.Where(i => i.Kind == InstanceKind.Level && i.LevelCopy == owner.Id);
        foreach (var i in targets.Where(i => i.Id != caller.Id))
            await commands.Send(i.Id, new InstanceCommand.StoreChanged(owner.Scope, owner.Id, key), ct);
    }
}
