using Google.Protobuf;
using SourceSharp.Host.Abstractions;
using P = SourceSharp.Host.Proto;

namespace Descent.Service.Api;

/// <summary>Records ↔ proto messages. Payloads stay opaque: sheets and items cross as Blob.</summary>
public static class Mapping
{
    public static P.Item ToProto(this ItemRecord i) => new()
    {
        Id = i.Id, Seed = i.Seed, BaseType = i.BaseType, Rarity = i.Rarity, Ilvl = i.ItemLevel, Count = i.Count, Identified = i.Identified,
        Instance = new P.Blob { Data = ByteString.CopyFrom(i.Instance), SchemaVersion = (uint)i.SchemaVersion },
        OwnerKind = (P.OwnerKind)(int)i.OwnerKind, OwnerId = i.OwnerId, Slot = i.Slot, Version = i.Version,
        ForCharacter = i.ForCharacter ?? "", InstanceId = i.InstanceId ?? "", Tier = i.Tier,
    };

    public static P.ItemsResponse ToProto(this IEnumerable<ItemRecord> items)
    {
        var r = new P.ItemsResponse();
        r.Items.AddRange(items.Select(ToProto));
        return r;
    }

    public static P.Character ToProto(this CharacterRecord c) => new()
    {
        Id = c.Id, Account = c.Account, ClassName = c.ClassName, Name = c.Name, Hardcore = c.Hardcore, Fallen = c.Fallen,
        Level = c.Level, Xp = c.Xp, ReachedDepth = c.ReachedDepth, Difficulty = c.Difficulty,
        Sheet = new P.Blob { Data = ByteString.CopyFrom(c.Sheet), SchemaVersion = (uint)c.SheetVersion },
        Australium = c.Australium, Version = c.Version,
    };

    public static P.Party ToProto(this Party p, string? liveInstance = null)
    {
        var r = new P.Party { Id = p.Id, Leader = p.Leader, LiveInstance = liveInstance ?? "" };
        r.Members.AddRange(p.Members);
        r.Invited.AddRange(p.Invited);
        return r;
    }

    public static P.Trade ToProto(this TradeRecord t)
    {
        var r = new P.Trade
        {
            Id = t.Id, A = t.A, B = t.B, State = (P.TradeStateKind)(int)t.State,
            AustraliumA = t.AustraliumA, AustraliumB = t.AustraliumB,
            LockedA = t.LockedA, LockedB = t.LockedB, ConfirmedA = t.ConfirmedA, ConfirmedB = t.ConfirmedB,
        };
        r.OfferA.AddRange(t.OfferA);
        r.OfferB.AddRange(t.OfferB);
        return r;
    }
}
