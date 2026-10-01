using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Grpc.Core;
using SourceSharp.Host.Proto.Relay;

namespace SourceSharp.Host.Gateway.Relay;

/// <summary>
/// A session's current link to a backend: one PeerRelay stream to the game pod's sidecar
/// (relay.proto). Datagrams go up through an ordered queue (so the packet path never awaits);
/// the sidecar answers Opened with the loopback peer the engine sees for this session.
/// </summary>
public sealed class StreamLink(IPEndPoint backend)
{
    /// <summary>The sidecar's PeerRelay endpoint (the route table's "backend").</summary>
    public IPEndPoint Backend { get; } = backend;
    public Channel<RelayUp> Up { get; } = Channel.CreateUnbounded<RelayUp>(new UnboundedChannelOptions { SingleReader = true });
    public CancellationTokenSource Stop { get; } = new();
    public AsyncDuplexStreamingCall<RelayUp, RelayDown>? Call { get; set; }
    /// <summary>True once the sidecar answered Opened; until then the session holds.</summary>
    public bool Open { get; set; }
    /// <summary>The address the engine sees for this session (from Opened).</summary>
    public IPEndPoint? Peer { get; set; }
}

/// <summary>One client address and everything the relay keeps for it (plan §8.1).</summary>
public sealed class Session(string id, SocketAddress clientAddress, IPEndPoint client, DateTimeOffset now)
{
    public string Id { get; } = id;
    /// <summary>The key the public socket's receive path looks the session up by.</summary>
    public SocketAddress ClientAddress { get; } = clientAddress;
    public IPEndPoint Client { get; } = client;
    public string ClientKey { get; } = client.ToString();
    public DateTimeOffset Opened { get; } = now;
    public DateTimeOffset LastSeen { get; set; } = now;

    public ulong? SteamId { get; set; }
    /// <summary>Identified sessions may be routed off the default backend; unidentified ones never.</summary>
    public bool Identified => SteamId is not null;
    /// <summary>The explicit route; null means the default backend.</summary>
    public IPEndPoint? Route { get; set; }
    /// <summary>A peer to ask the sidecar for on the next Open (from SyncTable after a gateway restart).</summary>
    public IPEndPoint? PeerHint { get; set; }

    public StreamLink? Link { get; set; }
    /// <summary>The backend of the last open link, so the next Opened reports SessionMoved.</summary>
    public IPEndPoint? MovedFrom { get; set; }
    public bool EverLinked { get; set; }
    /// <summary>After a stream failed before Opened: no new attempt before this.</summary>
    public DateTimeOffset RetryAt { get; set; }
    public IPEndPoint? RetryBackend { get; set; }

    public long PacketsIn { get; set; }
    public long PacketsOut { get; set; }
    public long BytesIn { get; set; }
    public long BytesOut { get; set; }

    /// <summary>Where the identity-first handshake is (§8.1b); None when it is off.</summary>
    public IdentityPhase Phase { get; set; }
    public int GatewayChallenge { get; set; }
    public byte[]? HeldGetChallenge { get; set; }

    /// <summary>Datagrams held until the link is open (§8.1b); null when not holding.</summary>
    public HoldBuffer? Hold { get; set; }
    public string? HoldReason { get; set; }
    public DateTimeOffset HoldSince { get; set; }

    public string State => Hold is not null ? "holding" : Link is { Open: true } ? "relaying" : "unlinked";
}

public enum IdentityPhase
{
    None,
    /// <summary>The gateway answered the client's getchallenge with its own challenge.</summary>
    Challenged,
    /// <summary>The connect's SteamID is with the service (Identify); the connect is held, never forwarded.</summary>
    Identifying,
    /// <summary>The client's getchallenge went to the chosen backend; from here the gateway touches nothing.</summary>
    Replayed,
}
