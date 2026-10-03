using TF2.Descent.Service.Api;
using Grpc.Core;
using SourceSharp.Host.Abstractions;
using P = SourceSharp.Host.Proto;

namespace TF2.Descent.Service.Travel;

/// <summary>§6.5 TravelService: every trip goes through the coordinator; a refusal is a named status.</summary>
public sealed class TravelApi(ITripPlanner travel, IHostData data, Anchors anchors) : P.TravelService.TravelServiceBase
{
    // A trip runs several transactions (claims, the instance request, the hand-out), so its
    // idempotency is look-up-then-store around it rather than one transaction.
    Task<P.TravelResponse> Go(P.TravelRequest r, ServerCallContext ctx, TravelKind kind) =>
        data.IdempotentOutside(ctx, r.RequestId, P.TravelResponse.Parser, caller =>
            travel.Go(caller, r.CharacterId, r.LeaseToken.ToByteArray(), r.PartyId, r.Depth, kind, r.RequestId, ctx.CancellationToken));

    public override Task<P.TravelResponse> RequestDescent(P.TravelRequest request, ServerCallContext context) => Go(request, context, TravelKind.Descend);
    public override Task<P.TravelResponse> StairsDown(P.TravelRequest request, ServerCallContext context) => Go(request, context, TravelKind.StairsDown);
    public override Task<P.TravelResponse> TownPortal(P.TravelRequest request, ServerCallContext context) => Go(request, context, TravelKind.TownPortal);
    public override Task<P.TravelResponse> ReturnThroughPortal(P.TravelRequest request, ServerCallContext context) => Go(request, context, TravelKind.ReturnThroughPortal);
    public override Task<P.TravelResponse> ReturnToCorpse(P.TravelRequest request, ServerCallContext context) => Go(request, context, TravelKind.ReturnToCorpse);

    public override Task<P.TravelResponse> ReturnToAnchor(P.ReturnToAnchorRequest r, ServerCallContext ctx) =>
        data.IdempotentOutside(ctx, r.RequestId, P.TravelResponse.Parser, caller =>
            travel.ToAnchor(caller, r.CharacterId, r.LeaseToken.ToByteArray(), r.AnchorOwner, r.RequestId, ctx.CancellationToken));

    public override async Task<P.Anchor> PlaceAnchor(P.PlaceAnchorRequest r, ServerCallContext ctx)
    {
        var placed = await data.Idempotent(ctx, r.RequestId, P.Anchor.Parser, (tx, caller) => Anchors.Place(tx, caller, r.CharacterId, r.LeaseToken.ToByteArray(), r.Position, r.Yaw));
        await anchors.Notify(r.CharacterId, ctx.CancellationToken);
        return placed;
    }

    public override async Task<P.Ack> ClearAnchor(P.TravelRequest r, ServerCallContext ctx)
    {
        var ack = await data.Idempotent(ctx, r.RequestId, P.Ack.Parser, (tx, caller) => Anchors.Clear(tx, caller, r.CharacterId, r.LeaseToken.ToByteArray()));
        await anchors.Notify(r.CharacterId, ctx.CancellationToken);
        return ack;
    }

    public override Task<P.AnchorList> ListPartyAnchors(P.TravelRequest r, ServerCallContext ctx) =>
        data.Read(ctx, (tx, caller) => Anchors.ListParty(tx, caller, r.CharacterId, r.LeaseToken.ToByteArray()));
}
