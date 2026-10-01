using Microsoft.Extensions.DependencyInjection;
using System.Net;
using SourceSharp.Host.Abstractions;

namespace Descent.Service.Tests.Api;

/// <summary>/ops/dev/levels: the fake tier's pool, refused anywhere else.</summary>
public class DevSeedFacts
{
    [Fact]
    public async Task Seeding_levels_is_refused_outside_the_fake_tier()
    {
        await using var svc = await TestService.StartAsync();
        var r = await svc.Http(ListenerRole.Admin).PostAsync("/ops/dev/levels?from=1&to=1&per=1", null);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task On_the_fake_tier_seeded_levels_hand_out()
    {
        await using var svc = await TestService.StartAsync(extra: new() { ["Instances:SkipLevelFetch"] = "true" });
        var r = await svc.Http(ListenerRole.Admin).PostAsync("/ops/dev/levels?from=1&to=2&per=2", null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var pool = svc.App.Services.GetRequiredService<IMapPool>();
        var data = svc.App.Services.GetRequiredService<IHostData>();
        var level = await data.WriteAsync((tx, _) => pool.HandOutAsync(tx, 2, "inst-x"));
        Assert.Equal((2, LevelState.HandedOut), (level!.Depth, level.State));
    }
}
