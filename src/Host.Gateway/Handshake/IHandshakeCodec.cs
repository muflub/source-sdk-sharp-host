namespace SourceSharp.Host.Gateway.Handshake;

/// <summary>
/// The connectionless handshake the identity-first path (plan §8.1b) parses and answers. The relay
/// never reads a netchannel packet; these are the only packets it looks inside, and it never rewrites
/// one it forwards. Two wire formats: <see cref="SourceCodec"/> (docs/net-protocol.md §2, unverified
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
