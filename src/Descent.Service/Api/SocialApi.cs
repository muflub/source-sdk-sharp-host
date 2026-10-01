using Descent.Service.Ledger;
using Grpc.Core;
using SourceSharp.Host.Abstractions;
using P = SourceSharp.Host.Proto;

namespace Descent.Service.Api;

/// <summary>§6.4 parties (D8: at most four, leader invites and kicks).</summary>
public sealed class PartyApi(IHostData data) : P.PartyService.PartyServiceBase
{
    public const int MaxMembers = 4;

    static async Task<Party> Load(IReadTx tx, string partyId) =>
        await tx.Parties.Get(partyId) ?? throw HostRefusal.NotFound("no_party", partyId);

    static void Leader(Party p, string characterId)
    {
        if (p.Leader != characterId) throw HostRefusal.Denied("not_in_party", $"{characterId} does not lead {p.Id}");
    }

    public override Task<P.Party> Create(P.PartyRequest request, ServerCallContext context) =>
        data.Idempotent(context, request.RequestId, P.Party.Parser, async (tx, caller) =>
        {
            await DescentLedger.Leased(tx, caller, request.CharacterId, request.LeaseToken.Bytes());
            if (await tx.Parties.ForCharacter(request.CharacterId) is { } existing)
                throw HostRefusal.Precondition("party_full", $"{request.CharacterId} is already in {existing.Id}");
            return (await tx.Parties.Create(request.CharacterId)).ToProto();
        });

    public override Task<P.Party> Invite(P.PartyRequest request, ServerCallContext context) =>
        data.Idempotent(context, request.RequestId, P.Party.Parser, async (tx, caller) =>
        {
            await DescentLedger.Leased(tx, caller, request.CharacterId, request.LeaseToken.Bytes());
            var p = await Load(tx, request.PartyId);
            Leader(p, request.CharacterId);
            if (p.Members.Count + p.Invited.Count >= MaxMembers) throw HostRefusal.Precondition("party_full", $"{p.Id} has {MaxMembers}");
            return (await tx.Parties.Invite(p.Id, request.TargetCharacter)).ToProto();
        });

    public override Task<P.Party> Accept(P.PartyRequest request, ServerCallContext context) =>
        data.Idempotent(context, request.RequestId, P.Party.Parser, async (tx, caller) =>
        {
            await DescentLedger.Leased(tx, caller, request.CharacterId, request.LeaseToken.Bytes());
            if (await tx.Parties.ForCharacter(request.CharacterId) is { } existing && existing.Id != request.PartyId)
                throw HostRefusal.Precondition("party_full", $"{request.CharacterId} is already in {existing.Id}");
            return (await tx.Parties.Join(request.PartyId, request.CharacterId)).ToProto();
        });

    public override Task<P.Ack> Leave(P.PartyRequest request, ServerCallContext context) =>
        data.Idempotent(context, request.RequestId, P.Ack.Parser, async (tx, caller) =>
        {
            await DescentLedger.Leased(tx, caller, request.CharacterId, request.LeaseToken.Bytes());
            await tx.Parties.Remove(request.PartyId, request.CharacterId);
            return new P.Ack();
        });

    public override Task<P.Party> Kick(P.PartyRequest request, ServerCallContext context) =>
        data.Idempotent(context, request.RequestId, P.Party.Parser, async (tx, caller) =>
        {
            await DescentLedger.Leased(tx, caller, request.CharacterId, request.LeaseToken.Bytes());
            var p = await Load(tx, request.PartyId);
            Leader(p, request.CharacterId);
            return (await tx.Parties.Remove(p.Id, request.TargetCharacter) ?? p).ToProto();
        });

    public override Task<P.Party> Get(P.PartyRequest request, ServerCallContext context) =>
        data.Read(context, async (tx, _) =>
        {
            var p = request.PartyId.Length > 0 ? await Load(tx, request.PartyId)
                : await tx.Parties.ForCharacter(request.CharacterId) ?? throw HostRefusal.NotFound("no_party", request.CharacterId);
            var live = (await tx.Leases.Get(p.Leader))?.InstanceId;
            return p.ToProto(live);
        });
}

/// <summary>§6.4 trades: two-phase, both leases held by the calling hub, the second Confirm commits.</summary>
public sealed class TradeApi(IHostData data, DescentLedger ledger) : P.TradeService.TradeServiceBase
{
    public override Task<P.Trade> Open(P.TradeRequest request, ServerCallContext context) =>
        data.Idempotent(context, request.RequestId, P.Trade.Parser, async (tx, caller) =>
            (await ledger.TradeOpen(tx, caller, request.CharacterId, request.LeaseToken.Bytes(), request.OtherCharacter, request.RequestId)).ToProto());

    public override Task<P.Trade> Offer(P.TradeRequest request, ServerCallContext context) =>
        data.Idempotent(context, request.RequestId, P.Trade.Parser, async (tx, caller) =>
            (await ledger.TradeOffer(tx, caller, request.CharacterId, request.LeaseToken.Bytes(), request.TradeId, request.ItemIds, request.Australium, request.RequestId)).ToProto());

    public override Task<P.Trade> Lock(P.TradeRequest request, ServerCallContext context) =>
        data.Idempotent(context, request.RequestId, P.Trade.Parser, async (tx, caller) =>
            (await ledger.TradeLock(tx, caller, request.CharacterId, request.LeaseToken.Bytes(), request.TradeId, request.RequestId)).ToProto());

    public override Task<P.Trade> Confirm(P.TradeRequest request, ServerCallContext context) =>
        data.Idempotent(context, request.RequestId, P.Trade.Parser, async (tx, caller) =>
            (await ledger.TradeConfirm(tx, caller, request.CharacterId, request.LeaseToken.Bytes(), request.TradeId, request.RequestId)).ToProto());

    public override Task<P.Trade> Cancel(P.TradeRequest request, ServerCallContext context) =>
        data.Idempotent(context, request.RequestId, P.Trade.Parser, async (tx, caller) =>
            (await ledger.TradeCancel(tx, caller, request.CharacterId, request.LeaseToken.Bytes(), request.TradeId, request.RequestId)).ToProto());
}

public sealed class LostAndFoundApi(IHostData data, DescentLedger ledger) : P.LostAndFoundService.LostAndFoundServiceBase
{
    public override Task<P.LostAndFoundList> List(P.CharacterRef request, ServerCallContext context) =>
        data.Read(context, async (tx, _) =>
        {
            var r = new P.LostAndFoundList();
            foreach (var e in await tx.LostAndFound.ForCharacter(request.CharacterId))
            {
                var item = await tx.Items.Get(e.ItemId);
                if (item is null) continue;
                r.Entries.Add(new P.LostAndFoundEntry
                {
                    Id = e.Id, Item = item.ToProto(), Fee = e.Fee, SinceUnixMs = e.Since.ToUnixTimeMilliseconds(), OriginInstance = e.OriginInstance ?? "",
                });
            }
            return r;
        });

    /// <summary>The entry id travels in item_id (ItemActionRequest is shared).</summary>
    public override Task<P.Item> Reclaim(P.ItemActionRequest request, ServerCallContext context) =>
        data.Idempotent(context, request.RequestId, P.Item.Parser, async (tx, caller) =>
            (await ledger.Reclaim(tx, caller, request.CharacterId, request.LeaseToken.Bytes(), request.ItemId, request.RequestId)).ToProto());
}
