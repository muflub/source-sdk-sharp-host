using System.Security.Cryptography;
using System.Text;
using SourceSharp.Host.Abstractions;

namespace SourceSharp.Host.Instances.Tests;

/// <summary>§7.5 MaxLevelPods at RequestLevel; D-H5 one token per pod, hash stored, constant-time check.</summary>
public class CapacityAndAuthFacts
{
    [Fact]
    public async Task Request_past_max_level_pods_is_refused_with_a_typed_result()
    {
        await using var h = new ManagerHarness(o => o.Instances.MaxLevelPods = 2);
        await h.RequestLevel();
        await h.RequestLevel();
        var third = await h.M.RequestLevel(3, "party-3", "h3", 1);
        Assert.Equal(new LevelRequestResult.AtCapacity(2, 2), third);
        Assert.Equal(2, (await h.D.Read(tx => tx.Instances.List(InstanceKind.Level))).Count);
    }

    [Fact]
    public async Task Capacity_frees_when_a_level_is_reaped()
    {
        await using var h = new ManagerHarness(o => o.Instances.MaxLevelPods = 1);
        var row = await h.LiveLevel();
        Assert.IsType<LevelRequestResult.AtCapacity>(await h.M.RequestLevel(3, null, "h2", 1));
        await h.M.Drain(row.Id, "done", "admin@localhost");
        await h.Tick();
        Assert.IsType<LevelRequestResult.Accepted>(await h.M.RequestLevel(3, null, "h2", 1));
    }

    [Fact]
    public async Task Hub_does_not_count_against_max_level_pods()
    {
        await using var h = new ManagerHarness(o => { o.Instances.MaxLevelPods = 1; o.Instances.Hubs = 1; });
        await h.M.Start();
        Assert.IsType<LevelRequestResult.Accepted>(await h.M.RequestLevel(1, null, "h", 1));
    }

    [Fact]
    public async Task Pods_token_authenticates_its_instance()
    {
        await using var h = new ManagerHarness();
        var r = await h.RequestLevel();
        await h.Tick();
        var row = await h.Row(r.Id);
        Assert.Equal(r.Id, (await h.M.Authenticate(r.Id, h.Token(row)))?.Id);
    }

    [Fact]
    public async Task Wrong_token_is_refused()
    {
        await using var h = new ManagerHarness();
        var r = await h.RequestLevel();
        await h.Tick();
        var token = h.Token(await h.Row(r.Id));
        Assert.Null(await h.M.Authenticate(r.Id, token[..^1] + (token[^1] == '0' ? '1' : '0')));
    }

    [Fact]
    public async Task Another_instances_token_is_refused()
    {
        await using var h = new ManagerHarness();
        var a = await h.RequestLevel(hash: "a");
        var b = await h.RequestLevel(hash: "b");
        await h.Tick();
        Assert.NotNull(await h.M.Authenticate(b.Id, h.Token(await h.Row(b.Id))));
        Assert.Null(await h.M.Authenticate(a.Id, h.Token(await h.Row(b.Id))));
    }

    [Fact]
    public async Task Ended_instance_no_longer_authenticates()
    {
        await using var h = new ManagerHarness();
        var row = await h.LiveLevel();
        var token = h.Token(row);
        await h.M.OnStreamClosed(row.Id);
        Assert.Null(await h.M.Authenticate(row.Id, token));
    }

    [Fact]
    public async Task Row_stores_the_sha256_hex_of_the_token_never_the_token()
    {
        await using var h = new ManagerHarness();
        var r = await h.RequestLevel();
        await h.Tick();
        var row = await h.Row(r.Id);
        var token = h.Token(row);
        Assert.Equal(64, token.Length); // 32 random bytes, hex
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token))), row.TokenHash);
        Assert.DoesNotContain(token, row.ToString());
    }

    [Fact]
    public async Task Every_instance_gets_its_own_token()
    {
        await using var h = new ManagerHarness();
        await h.RequestLevel(hash: "a");
        await h.RequestLevel(hash: "b");
        await h.Tick();
        Assert.Equal(2, h.Pods.Secrets.Values.Select(s => s.Secret.Data["token"]).Distinct().Count());
    }
}
