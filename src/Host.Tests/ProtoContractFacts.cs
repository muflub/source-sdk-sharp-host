using Google.Protobuf.Reflection;
using SourceSharp.Host.Proto;

namespace SourceSharp.Host.Tests;

/// <summary>Plan H1b: every mutating request carries request_id; character writes carry lease_token.</summary>
public class ProtoContractFacts
{
    // Reads, streams, and calls whose idempotency is their own identity.
    static readonly HashSet<string> NotMutating =
    [
        "InstanceService.Connect", "InstanceService.UploadModule", "InstanceService.ResolvePeer",
        "InstanceService.ExecResult", "InstanceService.Log",
        "CharacterService.List", "CharacterService.GetSheet",
        "ItemService.GetBackpack", "ItemService.GetStash", "ItemService.GetVendorStock", "ItemService.ListWorldItems",
        "PartyService.Get", "LostAndFoundService.List",
    ];

    // Mutations that act on a character but hold no lease on it yet (the hub creates, deletes and leases).
    static readonly HashSet<string> NoLease =
    [
        "CharacterService.Create", "CharacterService.Delete", "CharacterService.Lease",
    ];

    static IEnumerable<(string Name, MethodDescriptor Method)> GameMethods() =>
        GameReflection.Descriptor.Services.SelectMany(s => s.Methods.Select(m => ($"{s.Name}.{m.Name}", m)));

    [Fact]
    public void The_game_api_has_its_services() =>
        Assert.Equal(
            ["CharacterService", "InstanceService", "ItemService", "LostAndFoundService", "PartyService", "TradeService", "TravelService"],
            GameReflection.Descriptor.Services.Select(s => s.Name).Order());

    [Fact]
    public void Every_mutating_game_request_carries_request_id()
    {
        var mutating = GameMethods().Where(m => !NotMutating.Contains(m.Name)).ToList();
        Assert.True(mutating.Count > 30, $"only {mutating.Count} mutating methods seen");
        var missing = mutating.Where(m => m.Method.InputType.FindFieldByName("request_id") is null).Select(m => m.Name).ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void Every_request_naming_a_character_it_writes_carries_lease_token()
    {
        var writes = GameMethods()
            .Where(m => !NotMutating.Contains(m.Name) && !NoLease.Contains(m.Name))
            .Where(m => m.Method.InputType.FindFieldByName("character_id") is not null
                     || m.Method.InputType.FindFieldByName("killer_character") is not null)
            .ToList();
        Assert.True(writes.Count > 25, $"only {writes.Count} character writes seen");
        var missing = writes.Where(m => m.Method.InputType.FindFieldByName("lease_token") is null).Select(m => m.Name).ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void Every_gateway_control_call_carries_the_table_version()
    {
        var control = SourceSharp.Host.Proto.Gateway.GatewayReflection.Descriptor.Services.Single(s => s.Name == "GatewayControl");
        var missing = control.Methods
            .Where(m => m.Name is not ("Snapshot" or "SetServerInfo" or "ReleasePin"))
            .Where(m => m.InputType.FindFieldByName("version") is null)
            .Select(m => m.Name).ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void The_opaque_payloads_are_bytes_with_a_schema_version()
    {
        var blob = Blob.Descriptor;
        Assert.Equal(FieldType.Bytes, blob.FindFieldByName("data").FieldType);
        Assert.Equal(FieldType.UInt32, blob.FindFieldByName("schema_version").FieldType);
        Assert.Equal(Blob.Descriptor, Character.Descriptor.FindFieldByName("sheet").MessageType);
        Assert.Equal(Blob.Descriptor, Item.Descriptor.FindFieldByName("instance").MessageType);
    }
}
