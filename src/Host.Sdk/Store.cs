using Google.Protobuf;
using Grpc.Core;
using P = SourceSharp.Host.Proto;

namespace SourceSharp.Host.Sdk;

/// <summary>D-H17: whose data a key belongs to.</summary>
public enum HostStoreScope { Unspecified, Level, Instance, Character, Town }

/// <summary>A stored value and its version (pass the version back as expectedVersion to compare-and-set).</summary>
public sealed record StoreEntry(byte[] Value, long Version);

public sealed record StoreKey(string Key, long Version, int Size);

/// <summary>One page of keys; <see cref="NextPageToken"/> is null on the last.</summary>
public sealed record StoreKeyPage(IReadOnlyList<StoreKey> Keys, string? NextPageToken);

/// <summary>
/// D-H17: bytes this server keeps across restarts; the host never reads them. Limits: key 128
/// bytes, value 64 KiB, 256 keys and 4 MiB per owner (TOWN 1024 keys, 16 MiB).
/// </summary>
public interface IHostStore
{
    /// <summary>This level copy's data: a fresh instance of it reads the same (a level only).</summary>
    IStoreView Level { get; }
    /// <summary>This instance's data: survives the game server restarting, a fresh instance starts empty.</summary>
    IStoreView Instance { get; }
    /// <summary>One space every hub and level shares; town hears StoreChanged when a level writes it.</summary>
    IStoreView Town { get; }
    /// <summary>From town: a level copy someone claims or something runs (e.g. "arrival/&lt;character&gt;" before ReturnToAnchor); that level hears StoreChanged.</summary>
    IStoreView LevelCopy(string levelId);
    /// <summary>The character's data, while this server holds its lease.</summary>
    IStoreView Character(ICharacterLease lease);
}

public interface IStoreView
{
    /// <summary>The entry, or null when the key is not there.</summary>
    Task<HostResult<StoreEntry?>> Get(string key);
    /// <summary>
    /// Writes and answers the new version. <paramref name="expectedVersion"/>: null writes regardless,
    /// 0 creates only, anything else compare-and-sets (<see cref="HostError.VersionMismatch"/>).
    /// </summary>
    Task<HostResult<long>> Set(string key, byte[] value, long? expectedVersion = null);
    /// <summary>False when the key was not there.</summary>
    Task<HostResult<bool>> Delete(string key, long? expectedVersion = null);
    Task<HostResult<StoreKeyPage>> List(string prefix = "", string? pageToken = null, int pageSize = 100);
}

internal sealed class HostStore(SdkCore core) : IHostStore
{
    public IStoreView Level { get; } = new View(core, () => new P.StoreRef { Scope = P.StoreScope.Level }, null);
    public IStoreView Instance { get; } = new View(core, () => new P.StoreRef { Scope = P.StoreScope.Instance }, null);
    public IStoreView Town { get; } = new View(core, () => new P.StoreRef { Scope = P.StoreScope.Town }, null);
    public IStoreView LevelCopy(string levelId) => new View(core, () => new P.StoreRef { Scope = P.StoreScope.Level, LevelId = levelId }, null);
    public IStoreView Character(ICharacterLease lease) =>
        new View(core, () => new P.StoreRef { Scope = P.StoreScope.Character, CharacterId = lease.CharacterId }, lease);

    /// <summary>A store owner. With a lease every call carries its token and is serialised with the lease's other writes.</summary>
    sealed class View(SdkCore core, Func<P.StoreRef> reference, ICharacterLease? lease) : IStoreView
    {
        Task<HostResult<TOut>> Run<TReq, TResp, TOut>(TReq request, Action<TReq, P.StoreRef> setRef,
            Func<TReq, Metadata, DateTime?, CancellationToken, AsyncUnaryCall<TResp>> rpc, Func<TResp, TOut> map)
        {
            var r = reference();
            setRef(request, r);
            if (lease is not null)
                return LeasedCall.Run(core, lease, request, (_, token) => r.LeaseToken = token, rpc, map);
            return core.Background(async () => (await core.Rpc.Call((m, d, ct) => rpc(request, m, d, ct)).ConfigureAwait(false)).Map(map));
        }

        public Task<HostResult<StoreEntry?>> Get(string key) =>
            Run(new P.StoreGetRequest { Key = key }, (q, r) => q.Ref = r, core.StoreClient.GetAsync,
                v => v.Found ? new StoreEntry(v.Value.ToByteArray(), v.Version) : null);

        public Task<HostResult<long>> Set(string key, byte[] value, long? expectedVersion = null)
        {
            var q = new P.StoreSetRequest { Key = key, Value = ByteString.CopyFrom(value) };
            if (expectedVersion is { } v) q.ExpectedVersion = v;
            return Run(q, (x, r) => x.Ref = r, core.StoreClient.SetAsync, a => a.Version);
        }

        public Task<HostResult<bool>> Delete(string key, long? expectedVersion = null)
        {
            var q = new P.StoreDeleteRequest { Key = key };
            if (expectedVersion is { } v) q.ExpectedVersion = v;
            return Run(q, (x, r) => x.Ref = r, core.StoreClient.DeleteAsync, a => a.Deleted);
        }

        public Task<HostResult<StoreKeyPage>> List(string prefix = "", string? pageToken = null, int pageSize = 100) =>
            Run(new P.StoreListRequest { Prefix = prefix, PageToken = pageToken ?? "", PageSize = pageSize }, (x, r) => x.Ref = r, core.StoreClient.ListAsync,
                a => new StoreKeyPage(a.Keys.Select(k => new StoreKey(k.Key, k.Version, k.Size)).ToList(), a.NextPageToken.Length == 0 ? null : a.NextPageToken));
    }
}
