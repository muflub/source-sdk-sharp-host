using Descent.Service.Ledger;
using Google.Protobuf;
using Grpc.Core;
using SourceSharp.Host.Abstractions;
using P = SourceSharp.Host.Proto;

namespace Descent.Service.Api;

/// <summary>§6.2. A level acts only on characters it leases; a hub may also list, create and delete.</summary>
public sealed class CharacterApi(IHostData data, DescentLedger ledger) : P.CharacterService.CharacterServiceBase
{
    public override Task<P.ListCharactersResponse> List(P.ListCharactersRequest request, ServerCallContext context) =>
        data.Read(context, async (tx, caller) =>
        {
            var all = await tx.Characters.List(request.Account);
            if (caller.Kind != InstanceKind.Hub)
            {
                // A level learns which character a joining player travels as: only the account's
                // characters whose claim (D-H12, set for every member by the trip) is this level.
                // Anything else stays the hub's; with none, the refusal is the hub-only one.
                var here = new List<CharacterRecord>();
                foreach (var c in all)
                    if (caller.LevelHash is { } hash && (await tx.LevelClaims.Get(c.Id, caller.Depth))?.LevelHash == hash) here.Add(c);
                if (here.Count == 0) DescentLedger.RequireHub(caller);
                all = here;
            }
            var r = new P.ListCharactersResponse();
            r.Characters.AddRange(all.Select(c => c.ToProto()));
            return r;
        });

    public override Task<P.Character> Create(P.CreateCharacterRequest request, ServerCallContext context) =>
        data.Idempotent(context, request.RequestId, P.Character.Parser, async (tx, caller) =>
            (await ledger.CreateCharacter(tx, caller, request.Account, request.ClassName, request.Name, request.Hardcore)).ToProto());

    public override Task<P.Ack> Delete(P.DeleteCharacterRequest request, ServerCallContext context) =>
        data.Idempotent(context, request.RequestId, P.Ack.Parser, async (tx, caller) =>
        {
            await DescentLedger.DeleteCharacter(tx, caller, request.CharacterId);
            return new P.Ack();
        });

    public override Task<P.LeaseResponse> Lease(P.LeaseRequest request, ServerCallContext context) =>
        data.Idempotent(context, request.RequestId, P.LeaseResponse.Parser, async (tx, caller) =>
        {
            var (lease, ch) = await ledger.LeaseCharacter(tx, caller, request.CharacterId);
            return new P.LeaseResponse { Token = ByteString.CopyFrom(lease.Token), ExpiresUnixMs = lease.Expires.ToUnixTimeMilliseconds(), Character = ch.ToProto() };
        });

    public override Task<P.Ack> Release(P.ReleaseRequest request, ServerCallContext context) =>
        data.Idempotent(context, request.RequestId, P.Ack.Parser, async (tx, caller) =>
        {
            await DescentLedger.ReleaseCharacter(tx, caller, request.CharacterId, request.LeaseToken.Bytes());
            return new P.Ack();
        });

    public override Task<P.CheckpointResponse> Checkpoint(P.CheckpointRequest request, ServerCallContext context) =>
        data.Idempotent(context, request.RequestId, P.CheckpointResponse.Parser, async (tx, caller) =>
        {
            var (ch, stale) = await ledger.Checkpoint(tx, caller, request.CharacterId, request.LeaseToken.Bytes(),
                request.Sheet.Data.Bytes(), (int)request.Sheet.SchemaVersion,
                request.Evidence?.Data.Bytes() ?? [], (int)(request.Evidence?.SchemaVersion ?? 0), request.ExpectedVersion);
            var r = new P.CheckpointResponse { Version = ch.Version };
            r.StaleReserveTiers.AddRange(stale);
            return r;
        });

    public override Task<P.Character> GetSheet(P.GetSheetRequest request, ServerCallContext context) =>
        data.Read(context, async (tx, _) =>
            (await tx.Characters.Get(request.CharacterId) ?? throw HostRefusal.NotFound("no_character", request.CharacterId)).ToProto());

    public override Task<P.Character> SetReachedDepth(P.SetReachedDepthRequest request, ServerCallContext context) =>
        data.Idempotent(context, request.RequestId, P.Character.Parser, async (tx, caller) =>
            (await DescentLedger.SetReachedDepth(tx, caller, request.CharacterId, request.LeaseToken.Bytes(), request.Depth)).ToProto());
}

/// <summary>§6.3: the ledger of §5 as RPCs.</summary>
public sealed class ItemApi(IHostData data, DescentLedger ledger) : P.ItemService.ItemServiceBase
{
    public override Task<P.TakeReserveResponse> TakeReserve(P.TakeReserveRequest request, ServerCallContext context) =>
        data.Idempotent(context, request.RequestId, P.TakeReserveResponse.Parser, async (tx, caller) =>
        {
            var items = await ledger.TakeReserve(tx, caller, request.CharacterId, request.LeaseToken.Bytes(),
                request.Wanted.Select(w => (w.Tier, w.Count)).ToList(), request.RequestId);
            var r = new P.TakeReserveResponse();
            r.Items.AddRange(items.Select(i => i.ToProto()));
            return r;
        });

    public override async Task<P.RevealResponse> Reveal(P.RevealRequest request, ServerCallContext context)
    {
        try
        {
            return await data.Idempotent(context, request.RequestId, P.RevealResponse.Parser, async (tx, caller) =>
            {
                var (item, booked) = await ledger.Reveal(tx, caller, request.CharacterId, request.LeaseToken.Bytes(), request.ItemId,
                    request.KillSeq, request.RobotTemplate, request.NearbyMembers, request.RequestId);
                return new P.RevealResponse { Item = item.ToProto(), AustraliumBooked = booked };
            });
        }
        catch (RpcException e) when (e.Trailers.GetValue("x-reason") == "forged_reveal")
        {
            // The refusal rolled its transaction back; the destruction is its own write (§5.2).
            await data.Write(context, async (tx, caller) =>
            {
                await DescentLedger.ForgedReveal(tx, caller, request.ItemId, request.KillSeq, request.RequestId);
                return true;
            });
            throw;
        }
    }

    public override Task<P.ItemsResponse> MintDrops(P.MintDropsRequest request, ServerCallContext context) =>
        data.Idempotent(context, request.RequestId, P.ItemsResponse.Parser, async (tx, caller) =>
            (await ledger.MintDrops(tx, caller, request.KillerCharacter, request.LeaseToken.Bytes(), request.RobotTemplate,
                request.NearbyMembers, request.KillSeq, request.Tier, request.RequestId)).ToProto());

    public override Task<P.Item> Claim(P.ItemActionRequest request, ServerCallContext context) =>
        data.Idempotent(context, request.RequestId, P.Item.Parser, async (tx, caller) =>
            (await ledger.Claim(tx, caller, request.CharacterId, request.LeaseToken.Bytes(), request.ItemId, request.RequestId)).ToProto());

    public override Task<P.Item> Drop(P.DropRequest request, ServerCallContext context) =>
        data.Idempotent(context, request.RequestId, P.Item.Parser, async (tx, caller) =>
            (await ledger.Drop(tx, caller, request.CharacterId, request.LeaseToken.Bytes(), request.ItemId, request.RequestId)).ToProto());

    public override Task<P.Item> Move(P.MoveRequest request, ServerCallContext context) =>
        data.Idempotent(context, request.RequestId, P.Item.Parser, async (tx, caller) =>
            (await ledger.Move(tx, caller, request.CharacterId, request.LeaseToken.Bytes(), request.ItemId, request.Slot, request.RequestId)).ToProto());

    public override Task<P.Item> StashMove(P.StashMoveRequest request, ServerCallContext context) =>
        data.Idempotent(context, request.RequestId, P.Item.Parser, async (tx, caller) =>
            (await ledger.StashMove(tx, caller, request.CharacterId, request.LeaseToken.Bytes(), request.ItemId, request.ToStash, request.Slot, request.RequestId)).ToProto());

    public override Task<P.ItemsResponse> GetBackpack(P.CharacterRef request, ServerCallContext context) =>
        data.Read(context, async (tx, _) =>
            (await tx.Items.Query(new ItemQuery(OwnerKind: OwnerKind.Character, OwnerId: request.CharacterId))).ToProto());

    public override Task<P.ItemsResponse> GetStash(P.StashRef request, ServerCallContext context) =>
        data.Read(context, async (tx, caller) =>
        {
            DescentLedger.RequireHub(caller);
            return (await tx.Items.Query(new ItemQuery(OwnerKind: OwnerKind.Stash, OwnerId: DescentLedger.StashOwner(request.Account, request.Hardcore)))).ToProto();
        });

    public override Task<P.ItemsResponse> GetVendorStock(P.VendorRef request, ServerCallContext context) =>
        data.Read(context, async (tx, caller) =>
        {
            DescentLedger.RequireHub(caller);
            return (await tx.Items.Query(new ItemQuery(OwnerKind: OwnerKind.Vendor, OwnerId: DescentLedger.VendorOwner(caller.Id, request.Vendor)))).ToProto();
        });

    public override Task<P.Item> Buy(P.ItemActionRequest request, ServerCallContext context) =>
        data.Idempotent(context, request.RequestId, P.Item.Parser, async (tx, caller) =>
            (await ledger.Buy(tx, caller, request.CharacterId, request.LeaseToken.Bytes(), request.ItemId, request.Vendor, request.RequestId)).ToProto());

    public override Task<P.WalletResponse> Sell(P.ItemActionRequest request, ServerCallContext context) =>
        data.Idempotent(context, request.RequestId, P.WalletResponse.Parser, async (tx, caller) =>
            new P.WalletResponse { Australium = await ledger.Sell(tx, caller, request.CharacterId, request.LeaseToken.Bytes(), request.ItemId, request.Vendor, request.RequestId) });

    public override Task<P.Item> BuyBack(P.ItemActionRequest request, ServerCallContext context) =>
        data.Idempotent(context, request.RequestId, P.Item.Parser, async (tx, caller) =>
            (await ledger.BuyBack(tx, caller, request.CharacterId, request.LeaseToken.Bytes(), request.ItemId, request.Vendor, request.RequestId)).ToProto());

    Task<P.Item> One(P.ItemActionRequest request, ServerCallContext context, string operation) =>
        data.Idempotent(context, request.RequestId, P.Item.Parser, async (tx, caller) =>
            (await ledger.Transform(tx, caller, request.CharacterId, request.LeaseToken.Bytes(), operation, [request.ItemId], null, request.RequestId))[0].ToProto());

    public override Task<P.Item> OpenCrate(P.ItemActionRequest request, ServerCallContext context) => One(request, context, "crate");
    public override Task<P.Item> Identify(P.ItemActionRequest request, ServerCallContext context) => One(request, context, "identify");
    public override Task<P.Item> Repair(P.ItemActionRequest request, ServerCallContext context) => One(request, context, "repair");

    public override Task<P.ItemsResponse> Salvage(P.ItemActionRequest request, ServerCallContext context) =>
        data.Idempotent(context, request.RequestId, P.ItemsResponse.Parser, async (tx, caller) =>
            (await ledger.Transform(tx, caller, request.CharacterId, request.LeaseToken.Bytes(), "salvage", [request.ItemId], null, request.RequestId)).ToProto());

    public override Task<P.Item> Craft(P.CraftRequest request, ServerCallContext context) =>
        data.Idempotent(context, request.RequestId, P.Item.Parser, async (tx, caller) =>
            (await ledger.Transform(tx, caller, request.CharacterId, request.LeaseToken.Bytes(), "craft", request.Inputs, request.Recipe, request.RequestId))[0].ToProto());

    public override Task<P.ItemsResponse> RecordDeath(P.RecordDeathRequest request, ServerCallContext context) =>
        data.Idempotent(context, request.RequestId, P.ItemsResponse.Parser, async (tx, caller) =>
            (await ledger.RecordDeath(tx, caller, request.CharacterId, request.LeaseToken.Bytes(), request.RequestId)).ToProto());

    public override Task<P.ItemsResponse> LootCorpse(P.LootCorpseRequest request, ServerCallContext context) =>
        data.Idempotent(context, request.RequestId, P.ItemsResponse.Parser, async (tx, caller) =>
            (await ledger.LootCorpse(tx, caller, request.CharacterId, request.LeaseToken.Bytes(), request.RequestId)).ToProto());

    public override Task<P.ItemsResponse> ListWorldItems(P.ListWorldItemsRequest request, ServerCallContext context) =>
        data.Read(context, async (tx, caller) =>
            (await tx.Items.Query(new ItemQuery(OwnerKind: OwnerKind.World, InstanceId: caller.Id, Take: 10_000))).ToProto());
}
