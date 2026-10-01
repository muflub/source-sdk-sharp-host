using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SourceSharp.Host.Abstractions;

namespace Descent.Service.Tests.Api;

/// <summary>/ops/characters, /ops/items and /ops/dev/australium: what live/h7 asserts on.</summary>
public class OpsCharacterFacts
{
    static async Task<(CharacterRecord Character, ItemRecord Item)> Seeded(TestService svc)
    {
        var data = svc.App.Services.GetRequiredService<IHostData>();
        return await data.WriteAsync(async (tx, _) =>
        {
            var c = await tx.Characters.Create("76561198000000009", "scout", "Ann", false, [1], 1, 1, 0);
            await tx.Characters.AddAustralium(c.Id, 40, "seed", null);
            var item = await tx.Items.Mint(new NewItem(7, "scattergun", 1, 1, 1, true, [1], 1), OwnerKind.Corpse, c.Id, null, 0, "test", null);
            await tx.LostAndFound.Add(c.Id, item.Id, 12, "lvl-1");
            return (c, item);
        });
    }

    [Fact]
    public async Task A_character_shows_its_australium_and_lost_and_found_entries()
    {
        await using var svc = await TestService.StartAsync();
        var (c, item) = await Seeded(svc);
        var json = await svc.Http(ListenerRole.Admin).GetFromJsonAsync<JsonElement>($"/ops/characters/{c.Id}");
        Assert.Equal(40, json.GetProperty("australium").GetInt64());
        var entry = Assert.Single(json.GetProperty("lostAndFound").EnumerateArray());
        Assert.Equal((item.Id, 12L), (entry.GetProperty("itemId").GetString(), entry.GetProperty("fee").GetInt64()));
    }

    [Fact]
    public async Task An_item_shows_its_owner()
    {
        await using var svc = await TestService.StartAsync();
        var (c, item) = await Seeded(svc);
        var json = await svc.Http(ListenerRole.Admin).GetFromJsonAsync<JsonElement>($"/ops/items/{item.Id}");
        Assert.Equal(("Corpse", c.Id), (json.GetProperty("ownerKind").GetString(), json.GetProperty("ownerId").GetString()));
    }

    [Fact]
    public async Task Granting_australium_is_refused_outside_the_fake_tier()
    {
        await using var svc = await TestService.StartAsync();
        var (c, _) = await Seeded(svc);
        var r = await svc.Http(ListenerRole.Admin).PostAsync($"/ops/dev/australium?character={c.Id}&amount=100", null);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task On_the_fake_tier_a_grant_is_booked_on_the_ledger()
    {
        await using var svc = await TestService.StartAsync(extra: new() { ["Instances:SkipLevelFetch"] = "true" });
        var (c, _) = await Seeded(svc);
        var r = await svc.Http(ListenerRole.Admin).PostAsync($"/ops/dev/australium?character={c.Id}&amount=100", null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var data = svc.App.Services.GetRequiredService<IHostData>();
        var ledger = await data.ReadAsync((tx, _) => tx.Characters.AustraliumLedger(c.Id));
        Assert.Contains(ledger, e => e.Delta == 100 && e.Reason == "dev_grant");
    }

    [Fact]
    public async Task Reconcile_reports_a_lease_on_an_instance_that_is_not_live()
    {
        await using var svc = await TestService.StartAsync();
        var (c, _) = await Seeded(svc);
        var clean = await svc.Http(ListenerRole.Admin).PostAsync("/ops/reconcile", null);
        Assert.Equal(HttpStatusCode.OK, clean.StatusCode);
        Assert.DoesNotContain("lease_orphan", await clean.Content.ReadAsStringAsync()); // the unpatched state reports none
        var data = svc.App.Services.GetRequiredService<IHostData>();
        await data.WriteAsync((tx, _) => tx.Leases.Acquire(c.Id, "gone-instance", TimeSpan.FromMinutes(5)));
        var json = await (await svc.Http(ListenerRole.Admin).PostAsync("/ops/reconcile", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(json.EnumerateArray(), f => f.GetProperty("kind").GetString() == "lease_orphan" && f.GetProperty("target").GetString() == c.Id);
    }
}
