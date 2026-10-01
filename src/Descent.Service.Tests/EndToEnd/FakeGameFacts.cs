using System.Security.Cryptography;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.FakeGame.Control;
using SourceSharp.Host.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Descent.Service.Tests.EndToEnd;

/// <summary>The fake game server (§7.6) as a pod of the real service, behind the real gateway.</summary>
public class FakeGameFacts
{
    [Fact]
    public async Task The_hub_pod_boots_announces_its_module_by_hash_and_goes_live()
    {
        await using var w = await E2eWorld.Start();
        var hub = (await w.Hub())!;
        var status = await (await w.HubControl()).StatusAsync(new Empty());
        var sha = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(typeof(FakeGameRules).Assembly.Location)));
        Assert.Equal((true, true, "hub", sha), (status.Booted, status.MapReady, status.Kind, status.ModuleSha256));
        Assert.Equal(sha, hub.RulesSha256);
        // The service loaded it by that hash through Host.Modules (D-H9): the upload landed and replays.
        var rules = w.Service.Services.GetRequiredService<IRulesProvider>().For(sha);
        Assert.Equal("FakeGameRules", rules.GetType().Name);
        Assert.NotSame(typeof(FakeGameRules).Assembly, rules.GetType().Assembly); // its own load context, from the uploaded bytes
        Assert.True(status.ModuleUploaded);
    }

    [Fact]
    public async Task A_client_joins_the_hub_through_the_gateway_and_the_hub_leases_its_character()
    {
        await using var w = await E2eWorld.Start();
        var hub = (await w.Hub())!;
        var (client, characterId) = await w.Joined(76561198000000001);
        Assert.Equal(hub.Id, client.InstanceId);
        var lease = await w.Data.ReadAsync((tx, _) => tx.Leases.Get(characterId));
        Assert.Equal(hub.Id, lease!.InstanceId);
        // The session the gateway registered is the one the hub's PlayerJoined bound to the SteamID.
        var session = (await w.Data.ReadAsync((tx, _) => tx.Sessions.ForSteamId("76561198000000001"))).Single();
        Assert.Equal(client.LocalEndPoint.ToString(), session.ClientAddr);
    }
}
