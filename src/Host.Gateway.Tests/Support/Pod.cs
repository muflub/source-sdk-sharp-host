using System.Net;
using SourceSharp.Host.FakeClient;
using SourceSharp.Host.Proto.Relay;
using SourceSharp.Host.Relay;

namespace SourceSharp.Host.Gateway.Tests.Support;

/// <summary>
/// A game pod in-process: a <see cref="ToyBackend"/> as the engine and a <see cref="RelayHost"/> in
/// front of it. The gateway's route table names the sidecar (<see cref="EndPoint"/>); the receive log
/// is the engine's.
/// </summary>
public sealed class Pod : IAsyncDisposable
{
    public ToyBackend Engine { get; }
    public RelayHost Sidecar { get; }

    public Pod(string instanceId, Action<RelayOptions>? configure = null, int port = 0)
    {
        Engine = new ToyBackend(instanceId);
        Sidecar = new RelayHost(Engine.EndPoint, configure: configure, port: port);
    }

    /// <summary>The route table's backend: the sidecar's PeerRelay endpoint.</summary>
    public IPEndPoint EndPoint => Sidecar.EndPoint;
    public IReadOnlyList<Received> Log => Engine.Log;
    public IReadOnlyList<IPEndPoint> Senders => Engine.Senders;
    public IReadOnlyDictionary<IPEndPoint, ulong> Accepted => Engine.Accepted;
    public void SendRetry(IPEndPoint peer) => Engine.SendRetry(peer);
    public Task<bool> WaitForAsync(Func<IReadOnlyList<Received>, bool> condition, TimeSpan? timeout = null) => Engine.WaitForAsync(condition, timeout);
    public PeerInfo.PeerInfoClient Info => Sidecar.Info;

    public async ValueTask DisposeAsync()
    {
        await Sidecar.DisposeAsync();
        Engine.Dispose();
    }
}
