using Descent.Service.Api;
using Grpc.Core;
using SourceSharp.Host.Abstractions;
using P = SourceSharp.Host.Proto;

namespace Descent.Service.Travel;

/// <summary>§6.5 TravelService: every trip goes through the coordinator; a refusal is a named status.</summary>
public sealed class TravelApi(ITripPlanner travel, IHostData data) : P.TravelService.TravelServiceBase
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
}
