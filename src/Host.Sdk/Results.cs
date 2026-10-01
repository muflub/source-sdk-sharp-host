using Grpc.Core;

namespace SourceSharp.Host.Sdk;

/// <summary>
/// Why the host (or the SDK itself, <see cref="HostRefusal.Local"/>) refused a call. Mirrors the
/// reasons of proto/common.proto so the game switches on an enum and never parses a string.
/// </summary>
public enum HostError
{
    /// <summary>A business reason this SDK has no name for; <see cref="HostRefusal.Reason"/> carries it (e.g. a checkpoint rule).</summary>
    Rejected,
    BadToken, UnknownInstance,
    WrongInstance, NotAHub, Quarantined,
    StaleLease, AlreadyLeased, WrongOwner, VersionConflict, NoCapacity, ForgedReveal, ReplayMismatch,
    TradeState, InsufficientFunds, NotInParty, PartyFull, PortMismatch,
    NoCharacter, NoItem, NoParty, NoTrade,
    PoolEmpty, InstanceCap,
    SdkVersion, ContractVersion,
    /// <summary>The host answered UNIMPLEMENTED without a known reason (a service that lacks the RPC).</summary>
    Unimplemented,
    /// <summary>Fail closed: the host could not be reached within the call's retries and deadlines.</summary>
    Unreachable,
    /// <summary>Local: the lease was released by this process; nothing was sent.</summary>
    LeaseReleased,
    /// <summary>Local: the lease was lost (revoked, stale, or heartbeats could not reach the host for longer than its TTL); nothing was sent.</summary>
    LeaseLost,
    /// <summary>The module upload was not accepted.</summary>
    ModuleRefused,
    /// <summary>The SDK was disposed while the call was pending.</summary>
    Disposed,
}

/// <summary>A refusal: the typed error, the wire reason (or the SDK's own), the human text, and whether it was decided locally.</summary>
public sealed record HostRefusal(HostError Error, string Reason, string Detail, bool Local = false)
{
    static readonly Dictionary<string, HostError> Reasons = new(StringComparer.Ordinal)
    {
        ["bad_token"] = HostError.BadToken, ["unknown_instance"] = HostError.UnknownInstance,
        ["wrong_instance"] = HostError.WrongInstance, ["not_a_hub"] = HostError.NotAHub, ["quarantined"] = HostError.Quarantined,
        ["stale_lease"] = HostError.StaleLease, ["already_leased"] = HostError.AlreadyLeased, ["wrong_owner"] = HostError.WrongOwner,
        ["version_conflict"] = HostError.VersionConflict, ["no_capacity"] = HostError.NoCapacity, ["forged_reveal"] = HostError.ForgedReveal,
        ["replay_mismatch"] = HostError.ReplayMismatch, ["trade_state"] = HostError.TradeState, ["insufficient_funds"] = HostError.InsufficientFunds,
        ["not_in_party"] = HostError.NotInParty, ["party_full"] = HostError.PartyFull, ["port_mismatch"] = HostError.PortMismatch,
        ["no_character"] = HostError.NoCharacter, ["no_item"] = HostError.NoItem, ["no_party"] = HostError.NoParty, ["no_trade"] = HostError.NoTrade,
        ["pool_empty"] = HostError.PoolEmpty, ["instance_cap"] = HostError.InstanceCap,
        ["sdk_version"] = HostError.SdkVersion, ["contract_version"] = HostError.ContractVersion,
    };

    /// <summary>The status codes that mean "try again with the same request id", never a business answer.</summary>
    internal static bool Transient(RpcException e) =>
        e.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded or StatusCode.Aborted
        || (e.StatusCode is StatusCode.Internal or StatusCode.Unknown or StatusCode.Cancelled && ReasonOf(e) is null);

    static string? ReasonOf(RpcException e)
    {
        var trailer = e.Trailers.GetValue("x-reason");
        if (!string.IsNullOrEmpty(trailer)) return trailer;
        var detail = e.Status.Detail ?? "";
        var colon = detail.IndexOf(':');
        if (colon <= 0) return null;
        var head = detail[..colon];
        return head.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_') ? head : null;
    }

    /// <summary>Maps a status the host sent to a typed refusal.</summary>
    public static HostRefusal FromRpc(RpcException e)
    {
        var reason = ReasonOf(e);
        if (reason is not null && Reasons.TryGetValue(reason, out var known))
            return new HostRefusal(known, reason, e.Status.Detail ?? "");
        if (reason is null && e.StatusCode == StatusCode.Unimplemented)
            return new HostRefusal(HostError.Unimplemented, "unimplemented", e.Status.Detail ?? "");
        if (reason is null && e.StatusCode == StatusCode.Unauthenticated)
            return new HostRefusal(HostError.BadToken, "bad_token", e.Status.Detail ?? "");
        return new HostRefusal(HostError.Rejected, reason ?? e.StatusCode.ToString(), e.Status.Detail ?? "");
    }

    internal static HostRefusal Unreachable(string detail) => new(HostError.Unreachable, "unreachable", detail, Local: true);
    internal static HostRefusal Disposed() => new(HostError.Disposed, "disposed", "the SDK was disposed", Local: true);
}

/// <summary>The outcome of a call with no value: ok, or a typed refusal. Never an exception the game must parse.</summary>
public sealed class HostResult
{
    public bool Ok => Refusal is null;
    public HostRefusal? Refusal { get; }
    HostResult(HostRefusal? refusal) => Refusal = refusal;
    public static HostResult Success { get; } = new(null);
    public static HostResult Refused(HostRefusal refusal) => new(refusal);
    public HostError? Error => Refusal?.Error;
    public override string ToString() => Ok ? "ok" : $"refused {Refusal!.Error} ({Refusal.Reason})";
}

/// <summary>The outcome of a call: a value, or a typed refusal.</summary>
public sealed class HostResult<T>
{
    readonly T? _value;
    public bool Ok => Refusal is null;
    public HostRefusal? Refusal { get; }
    public HostError? Error => Refusal?.Error;

    /// <summary>The value; reading it from a refusal throws, so check <see cref="Ok"/> first.</summary>
    public T Value => Ok ? _value! : throw new InvalidOperationException($"refused: {Refusal!.Error} ({Refusal.Reason}) {Refusal.Detail}");

    HostResult(T? value, HostRefusal? refusal) { _value = value; Refusal = refusal; }
    public static HostResult<T> Success(T value) => new(value, null);
    public static HostResult<T> Refused(HostRefusal refusal) => new(default, refusal);
    public HostResult<TOut> Map<TOut>(Func<T, TOut> f) => Ok ? HostResult<TOut>.Success(f(_value!)) : HostResult<TOut>.Refused(Refusal!);
    public HostResult Untyped() => Ok ? HostResult.Success : HostResult.Refused(Refusal!);
    public override string ToString() => Ok ? $"ok {_value}" : $"refused {Refusal!.Error} ({Refusal.Reason})";
}
