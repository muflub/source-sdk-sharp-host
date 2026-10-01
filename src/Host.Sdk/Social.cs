using Google.Protobuf;
using Grpc.Core;
using P = SourceSharp.Host.Proto;

namespace SourceSharp.Host.Sdk;

/// <summary>§6.4 parties (D8). The caller's lease rides in every call.</summary>
public interface IParties
{
    Task<HostResult<HostParty>> Create(ICharacterLease leader);
    Task<HostResult<HostParty>> Invite(ICharacterLease leader, string partyId, string targetCharacter);
    Task<HostResult<HostParty>> Accept(ICharacterLease invitee, string partyId);
    Task<HostResult> Leave(ICharacterLease member, string partyId);
    Task<HostResult<HostParty>> Kick(ICharacterLease leader, string partyId, string targetCharacter);
    /// <summary>A party by id, or the party <paramref name="characterId"/> is in when the id is empty.</summary>
    Task<HostResult<HostParty>> Get(string partyId, string characterId = "");
}

/// <summary>
/// §6.4 trades, two-phase. The SDK keeps each trade's last known state and refuses a Confirm
/// before both sides have locked locally (<see cref="HostError.TradeState"/>, nothing sent).
/// </summary>
public interface ITrades
{
    Task<HostResult<HostTrade>> Open(ICharacterLease caller, string otherCharacter);
    Task<HostResult<HostTrade>> Offer(ICharacterLease caller, string tradeId, IReadOnlyList<string> itemIds, long australium = 0);
    Task<HostResult<HostTrade>> Lock(ICharacterLease caller, string tradeId);
    Task<HostResult<HostTrade>> Confirm(ICharacterLease caller, string tradeId);
    Task<HostResult<HostTrade>> Cancel(ICharacterLease caller, string tradeId);
    /// <summary>The last state this SDK saw for a trade (updated in Pump).</summary>
    HostTrade? Known(string tradeId);
}

/// <summary>§6.5 travel. A Started answer is followed by PrepareHop on the stream.</summary>
public interface ITravel
{
    Task<HostResult<TravelOutcome>> RequestDescent(ICharacterLease leader, string partyId, int depth);
    Task<HostResult<TravelOutcome>> StairsDown(ICharacterLease leader, string partyId, int depth);
    Task<HostResult<TravelOutcome>> TownPortal(ICharacterLease caller);
    Task<HostResult<TravelOutcome>> ReturnThroughPortal(ICharacterLease caller);
    Task<HostResult<TravelOutcome>> ReturnToCorpse(ICharacterLease caller);
}

/// <summary>§6.4 Lost &amp; Found.</summary>
public interface ILostAndFound
{
    Task<HostResult<IReadOnlyList<LostAndFoundEntry>>> List(string characterId);
    Task<HostResult<HostItem>> Reclaim(ICharacterLease lease, string entryId);
}

internal static class LeasedCall
{
    public static CharacterLease L(ICharacterLease lease) =>
        lease as CharacterLease ?? throw new ArgumentException("a lease from ICharacters.Lease", nameof(lease));

    public static Task<HostResult<TOut>> Run<TReq, TResp, TOut>(SdkCore core, ICharacterLease lease, TReq request, Action<TReq, ByteString> setToken,
        Func<TReq, Metadata, DateTime?, CancellationToken, AsyncUnaryCall<TResp>> rpc, Func<TResp, TOut> map, Action<HostResult<TOut>>? onMain = null) =>
        L(lease).Write(async token =>
        {
            setToken(request, token);
            return (await core.Rpc.Call((m, d, ct) => rpc(request, m, d, ct)).ConfigureAwait(false)).Map(map);
        }, onMain);
}

internal sealed class Parties(SdkCore core) : IParties
{
    P.PartyRequest Req(ICharacterLease lease, string partyId = "", string target = "") =>
        new() { RequestId = Rpc.NewRequestId(), CharacterId = lease.CharacterId, PartyId = partyId, TargetCharacter = target };

    static void Tok(P.PartyRequest r, ByteString t) => r.LeaseToken = t;

    public Task<HostResult<HostParty>> Create(ICharacterLease leader) =>
        LeasedCall.Run(core, leader, Req(leader), Tok, core.PartyClient.CreateAsync, p => p.ToSdk());

    public Task<HostResult<HostParty>> Invite(ICharacterLease leader, string partyId, string targetCharacter) =>
        LeasedCall.Run(core, leader, Req(leader, partyId, targetCharacter), Tok, core.PartyClient.InviteAsync, p => p.ToSdk());

    public Task<HostResult<HostParty>> Accept(ICharacterLease invitee, string partyId) =>
        LeasedCall.Run(core, invitee, Req(invitee, partyId), Tok, core.PartyClient.AcceptAsync, p => p.ToSdk());

    public async Task<HostResult> Leave(ICharacterLease member, string partyId) =>
        (await LeasedCall.Run(core, member, Req(member, partyId), Tok, core.PartyClient.LeaveAsync, _ => true)).Untyped();

    public Task<HostResult<HostParty>> Kick(ICharacterLease leader, string partyId, string targetCharacter) =>
        LeasedCall.Run(core, leader, Req(leader, partyId, targetCharacter), Tok, core.PartyClient.KickAsync, p => p.ToSdk());

    public Task<HostResult<HostParty>> Get(string partyId, string characterId = "") => core.Background(async () =>
    {
        var request = new P.PartyRequest { PartyId = partyId, CharacterId = characterId };
        return (await core.Rpc.Call((m, d, ct) => core.PartyClient.GetAsync(request, m, d, ct)).ConfigureAwait(false)).Map(p => p.ToSdk());
    });
}

internal sealed class Trades(SdkCore core) : ITrades
{
    readonly Dictionary<string, HostTrade> _known = []; // main thread only: written in Pump

    P.TradeRequest Req(ICharacterLease lease, string tradeId = "") =>
        new() { RequestId = Rpc.NewRequestId(), CharacterId = lease.CharacterId, TradeId = tradeId };

    static void Tok(P.TradeRequest r, ByteString t) => r.LeaseToken = t;

    void Track(HostResult<HostTrade> r)
    {
        if (r.Ok) _known[r.Value.Id] = r.Value;
    }

    Task<HostResult<HostTrade>> Run(ICharacterLease lease, P.TradeRequest request, Func<P.TradeRequest, Metadata, DateTime?, CancellationToken, AsyncUnaryCall<P.Trade>> rpc) =>
        LeasedCall.Run(core, lease, request, Tok, rpc, t => t.ToSdk(), Track);

    public HostTrade? Known(string tradeId) => _known.TryGetValue(tradeId, out var t) ? t : null;

    public Task<HostResult<HostTrade>> Open(ICharacterLease caller, string otherCharacter)
    {
        var r = Req(caller);
        r.OtherCharacter = otherCharacter;
        return Run(caller, r, core.TradeClient.OpenAsync);
    }

    public Task<HostResult<HostTrade>> Offer(ICharacterLease caller, string tradeId, IReadOnlyList<string> itemIds, long australium = 0)
    {
        var r = Req(caller, tradeId);
        r.ItemIds.AddRange(itemIds);
        r.Australium = australium;
        return Run(caller, r, core.TradeClient.OfferAsync);
    }

    public Task<HostResult<HostTrade>> Lock(ICharacterLease caller, string tradeId) => Run(caller, Req(caller, tradeId), core.TradeClient.LockAsync);

    public Task<HostResult<HostTrade>> Confirm(ICharacterLease caller, string tradeId)
    {
        var t = Known(tradeId);
        if (t is null || !t.BothLocked || t.State is TradeStateKind.Committed or TradeStateKind.Cancelled)
            return Task.FromResult(HostResult<HostTrade>.Refused(new HostRefusal(HostError.TradeState, "trade_state",
                t is null ? $"trade {tradeId} is unknown to this SDK" : $"trade {tradeId} is {t.State}, locked a={t.LockedA} b={t.LockedB}: both sides lock before Confirm",
                Local: true)));
        return Run(caller, Req(caller, tradeId), core.TradeClient.ConfirmAsync);
    }

    public Task<HostResult<HostTrade>> Cancel(ICharacterLease caller, string tradeId) => Run(caller, Req(caller, tradeId), core.TradeClient.CancelAsync);
}

internal sealed class Travel(SdkCore core) : ITravel
{
    Task<HostResult<TravelOutcome>> Run(ICharacterLease lease, string partyId, int depth,
        Func<P.TravelRequest, Metadata, DateTime?, CancellationToken, AsyncUnaryCall<P.TravelResponse>> rpc) =>
        LeasedCall.Run(core, lease, new P.TravelRequest { RequestId = Rpc.NewRequestId(), CharacterId = lease.CharacterId, PartyId = partyId, Depth = depth },
            (r, t) => r.LeaseToken = t, rpc, r => r.ToSdk());

    public Task<HostResult<TravelOutcome>> RequestDescent(ICharacterLease leader, string partyId, int depth) => Run(leader, partyId, depth, core.TravelClient.RequestDescentAsync);
    public Task<HostResult<TravelOutcome>> StairsDown(ICharacterLease leader, string partyId, int depth) => Run(leader, partyId, depth, core.TravelClient.StairsDownAsync);
    public Task<HostResult<TravelOutcome>> TownPortal(ICharacterLease caller) => Run(caller, "", 0, core.TravelClient.TownPortalAsync);
    public Task<HostResult<TravelOutcome>> ReturnThroughPortal(ICharacterLease caller) => Run(caller, "", 0, core.TravelClient.ReturnThroughPortalAsync);
    public Task<HostResult<TravelOutcome>> ReturnToCorpse(ICharacterLease caller) => Run(caller, "", 0, core.TravelClient.ReturnToCorpseAsync);
}

internal sealed class LostAndFoundApi(SdkCore core) : ILostAndFound
{
    public Task<HostResult<IReadOnlyList<LostAndFoundEntry>>> List(string characterId) => core.Background(async () =>
    {
        var request = new P.CharacterRef { CharacterId = characterId };
        var r = await core.Rpc.Call((m, d, ct) => core.LostAndFoundClient.ListAsync(request, m, d, ct)).ConfigureAwait(false);
        return r.Map(l => (IReadOnlyList<LostAndFoundEntry>)l.Entries
            .Select(e => new LostAndFoundEntry(e.Id, e.Item.ToSdk(), e.Fee, DateTimeOffset.FromUnixTimeMilliseconds(e.SinceUnixMs), e.OriginInstance)).ToList());
    });

    public Task<HostResult<HostItem>> Reclaim(ICharacterLease lease, string entryId) =>
        LeasedCall.Run(core, lease, new P.ItemActionRequest { RequestId = Rpc.NewRequestId(), CharacterId = lease.CharacterId, ItemId = entryId },
            (r, t) => r.LeaseToken = t, core.LostAndFoundClient.ReclaimAsync, i => i.ToSdk());
}
