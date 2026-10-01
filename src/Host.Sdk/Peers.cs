using Grpc.Core;
using Grpc.Net.Client;
using SourceSharp.Host.Proto.Relay;
using P = SourceSharp.Host.Proto;

namespace SourceSharp.Host.Sdk;

/// <summary>
/// Who is behind a peer the engine sees (D-H10). Live peers from the pod's own launcher/sidecar
/// (PeerInfo.Resolve on loopback) first, then the service's history (ResolvePeer); a found answer
/// is cached for the peer's session, so logs and bans ask once.
/// </summary>
public interface IPeers
{
    /// <summary>The identity, or <see cref="HostError.Rejected"/> with reason "unknown_peer" when neither knows it.</summary>
    Task<HostResult<PeerIdentity>> Resolve(string peer);
    /// <summary>Drops a peer's cached answer (PlayerLeft does this).</summary>
    void Forget(string peer);
}

internal sealed class Peers : IPeers, IDisposable
{
    readonly SdkCore _core;
    readonly GrpcChannel? _sidecarChannel;
    readonly PeerInfo.PeerInfoClient? _sidecar;

    public Peers(SdkCore core)
    {
        _core = core;
        if (core.Endpoint.Sidecar is { } address)
        {
            _sidecarChannel = Rpc.CreateChannel(address, core.Options);
            _sidecar = new PeerInfo.PeerInfoClient(_sidecarChannel);
        }
    }

    public void Forget(string peer) => _core.PeerCache.TryRemove(peer, out _);

    public Task<HostResult<PeerIdentity>> Resolve(string peer)
    {
        if (_core.PeerCache.TryGetValue(peer, out var cached)) return Task.FromResult(HostResult<PeerIdentity>.Success(cached));
        return _core.Background(async () =>
        {
            if (_sidecar is not null)
            {
                try
                {
                    var e = await _sidecar.ResolveAsync(new ResolveRequest { Peer = peer }, deadline: DateTime.UtcNow + _core.Options.PeerInfoTimeout,
                        cancellationToken: _core.Stopping).ResponseAsync.ConfigureAwait(false);
                    if (e.Found)
                        return HostResult<PeerIdentity>.Success(_core.PeerCache[peer] =
                            new PeerIdentity(e.Peer.Length > 0 ? e.Peer : peer, e.ClientAddr, e.SessionId, e.Steamid, PeerSource.Sidecar));
                }
                catch (RpcException) { /* the launcher is down or does not know it: ask the service */ }
            }
            var request = new P.ResolvePeerRequest { Peer = peer };
            var r = await _core.Rpc.Call((m, d, ct) => _core.Instance.ResolvePeerAsync(request, m, d, ct)).ConfigureAwait(false);
            if (!r.Ok) return HostResult<PeerIdentity>.Refused(r.Refusal!);
            if (!r.Value.Found) return HostResult<PeerIdentity>.Refused(new HostRefusal(HostError.Rejected, "unknown_peer", $"neither the sidecar nor the service knows {peer}"));
            return HostResult<PeerIdentity>.Success(_core.PeerCache[peer] =
                new PeerIdentity(peer, r.Value.ClientAddr, r.Value.SessionId, r.Value.Steamid, PeerSource.Service));
        });
    }

    public void Dispose() => _sidecarChannel?.Dispose();
}
