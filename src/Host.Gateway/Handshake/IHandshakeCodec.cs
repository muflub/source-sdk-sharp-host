namespace SourceSharp.Host.Gateway.Handshake;

/// <summary>
/// The connectionless handshake the identity-first path (plan §8.1b) and Steam validation
/// (plan_steam_auth S-3, S-5) parse and answer. The relay never reads a netchannel packet; these are the
/// only packets it looks inside. It rewrites exactly one it forwards: the backend's challenge, which is
/// stamped with the gateway's own game-server identity (plan_steam_auth S-4, ruled 2026-10-04). Two wire formats: <see cref="SourceCodec"/> (docs/net-protocol.md §2, unverified
/// until the H0f capture) and <see cref="ToyCodec"/> (the fake tier's toy protocol, plan §7.6).
/// </summary>
public interface IHandshakeCodec
{
    string Name { get; }
    /// <summary>A client's A2S_GETCHALLENGE.</summary>
    bool IsGetChallenge(ReadOnlySpan<byte> p);
    /// <summary>A server's S2C_CHALLENGE.</summary>
    bool IsChallenge(ReadOnlySpan<byte> p);
    /// <summary>A client's C2S_CONNECT: its challenge, and its SteamID when the ticket is readable.</summary>
    bool TryReadConnect(ReadOnlySpan<byte> p, out int challenge, out ulong? steamId);
    /// <summary>A client's C2S_CONNECT: the SteamID its ticket claims (unverified until Steam answers) and where the
    /// ticket bytes Steam validates lie in <paramref name="p"/>.</summary>
    bool TryReadTicket(ReadOnlySpan<byte> p, out ulong steamId, out Range ticket);
    /// <summary>The backend's challenge <paramref name="p"/> as the client must see it: the gateway's game-server
    /// SteamID and secure flag, and <paramref name="authProtocol"/> when given. Every other byte is unchanged.</summary>
    byte[] RewriteChallenge(ReadOnlySpan<byte> p, ulong gameServerSteamId, bool secure, int? authProtocol = null);
    /// <summary>The gateway's own S2C_CHALLENGE answering <paramref name="getChallenge"/> (echoing its client challenge).</summary>
    byte[] Challenge(int challenge, ReadOnlySpan<byte> getChallenge);
    /// <summary>An S2C_CONNREJECT answering <paramref name="connect"/>.</summary>
    byte[] Reject(ReadOnlySpan<byte> connect, string reason);
}

public static class HandshakeCodecs
{
    /// <summary>The codec named by <c>GatewayOptions.Wire</c>: "Source" (default) or "Toy".</summary>
    public static IHandshakeCodec For(string wire) => wire switch
    {
        "Toy" => new ToyCodec(),
        "Source" => new SourceCodec(),
        _ => throw new ArgumentException($"unknown wire format '{wire}' (Source | Toy)"),
    };
}
