using Descent.Service.Gateway;
using Descent.Service.Workers;
using SourceSharp.Host.Abstractions;

namespace Descent.Service;

/// <summary>
/// Read-only JSON for the live gates and operators (loopback admin listener only, reached with
/// `make admin`): the gates assert on row transitions, audit counts and released leases (CLAUDE.md),
/// which the Blazor pages do not serve as data.
/// </summary>
public static class OpsEndpoints
{
    public static void MapOps(this WebApplication app)
    {
        var ops = app.MapGroup("/ops");
        ops.MapGet("/instances", (IHostData data, CancellationToken ct) =>
            data.ReadAsync(async (tx, _) => (await tx.Instances.List(take: 1000)).Select(i => new
            {
                i.Id, kind = i.Kind.ToString(), state = i.State.ToString(), i.Depth, i.PodName, i.PodUid, i.PodIp, i.LevelHash, i.Players, i.Reason,
            }), ct)).On(ListenerRole.Admin);

        ops.MapGet("/audit/counts", (IHostData data, CancellationToken ct) =>
            data.ReadAsync(async (tx, _) =>
            {
                var rows = await tx.Audit.List(new AuditQuery(Take: 100_000));
                return rows.GroupBy(r => r.Action).ToDictionary(g => g.Key, g => g.Count());
            }, ct)).On(ListenerRole.Admin);

        ops.MapGet("/audit", (IHostData data, string? action, string? target, CancellationToken ct) =>
            data.ReadAsync((tx, _) => tx.Audit.List(new AuditQuery(Action: action, Target: target, Take: 500)), ct)).On(ListenerRole.Admin);

        ops.MapGet("/leases", (IHostData data, CancellationToken ct) =>
            data.ReadAsync(async (tx, _) => (await tx.Leases.All()).Select(l => new { l.CharacterId, l.InstanceId, l.Expires }), ct)).On(ListenerRole.Admin);

        ops.MapGet("/items/totals", (IHostData data, CancellationToken ct) =>
            data.ReadAsync(async (tx, _) =>
            {
                var (minted, terminal, live) = await tx.Items.Totals();
                var byOwner = new Dictionary<string, int>();
                foreach (var k in Enum.GetValues<OwnerKind>())
                    byOwner[k.ToString()] = await tx.Items.Count(new ItemQuery(OwnerKind: k));
                return new { minted, terminal, live, balanced = minted - terminal == live, byOwner };
            }, ct)).On(ListenerRole.Admin);

        ops.MapGet("/sessions", (IHostData data, CancellationToken ct) =>
            data.ReadAsync((tx, _) => tx.Sessions.Open(), ct)).On(ListenerRole.Admin);

        ops.MapGet("/characters/{id}", async (IHostData data, string id, CancellationToken ct) =>
        {
            var c = await data.ReadAsync(async (tx, _) => await tx.Characters.Get(id) is { } ch
                ? new
                {
                    ch.Id, ch.Account, ch.Australium, ch.ReachedDepth,
                    lostAndFound = (await tx.LostAndFound.ForCharacter(id)).Select(e => new { e.Id, e.ItemId, e.Fee, e.OriginInstance }),
                }
                : null, ct);
            return c is null ? Results.NotFound() : Results.Json(c);
        }).On(ListenerRole.Admin);

        ops.MapGet("/items/{id}", async (IHostData data, string id, CancellationToken ct) =>
            await data.ReadAsync((tx, _) => tx.Items.Get(id), ct) is { } i
                ? Results.Json(new { i.Id, ownerKind = i.OwnerKind.ToString(), i.OwnerId, i.InstanceId })
                : Results.NotFound()).On(ListenerRole.Admin);

        // §5.4 on demand, report only (the cached sums are not fixed here): the soak's invariants.
        ops.MapPost("/reconcile", (IHostData data, IAdminLedger ledger, CancellationToken ct) =>
            data.WriteAsync((tx, _) => ledger.Reconcile(tx, fixCachedSums: false), ct)).On(ListenerRole.Admin);

        // TIER=fake only: Australium for a gate that reclaims from Lost & Found (the fake has no vendor).
        ops.MapPost("/dev/australium", async (IHostData data, ServiceOptions o, IWebHostEnvironment env, string character, long amount, CancellationToken ct) =>
        {
            if (!FakeTier(env, o)) return Results.Problem("granting Australium is for TIER=fake (Development with Instances.SkipLevelFetch)", statusCode: 403);
            var balance = await data.WriteAsync(async (tx, _) =>
            {
                var b = await tx.Characters.AddAustralium(character, amount, "dev_grant", null);
                await tx.Audit.Write("dev.grant_australium", character, null, new { amount });
                return b;
            }, ct);
            return Results.Json(new { australium = balance });
        }).On(ListenerRole.Admin);

        // TIER=fake only: a synthetic ready pack assigned to depths [from, to] with `per` ready levels
        // each. The fake game loads no map, so the levels need no files; refused anywhere else.
        ops.MapPost("/dev/levels", async (IHostData data, ServiceOptions o, IWebHostEnvironment env, int from, int to, int per, CancellationToken ct) =>
        {
            if (!FakeTier(env, o))
                return Results.Problem("seeding levels is for TIER=fake (Development with Instances.SkipLevelFetch)", statusCode: 403);
            var hashes = await data.WriteAsync(async (tx, _) =>
            {
                var version = await tx.Library.NextVersion(o.Mod.Name, "fake");
                var pack = await tx.Library.AddPack(new PackRecord(0, o.Mod.Name, "fake", version, "fake-seed", "fake", "fake-seed",
                    o.Admin.Actor, tx.Now, 0, "", null, "TIER=fake seed", PackState.Ready));
                var made = new List<string>();
                for (var d = from; d <= to; d++)
                {
                    await tx.Library.Assign(new PackAssignment(o.Mod.Name, d, pack.Id, true, o.Admin.Actor, tx.Now, OldLevelsPolicy.Drain));
                    for (var i = 0; i < per; i++)
                    {
                        var hash = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8));
                        await tx.Levels.Add(new LevelRecord(hash, pack.Id, d, "fake", 0, 0, LevelState.Ready, tx.Now, null, 0, null, null));
                        made.Add($"{d}:{hash}");
                    }
                }
                await tx.Audit.Write("dev.seed_levels", o.Mod.Name, null, new { from, to, per, pack = pack.Id });
                return made;
            }, ct);
            return Results.Json(hashes);
        }).On(ListenerRole.Admin);

        ops.MapGet("/status", (IMapPool pool, GatewayState gateway, MaintenanceState maintenance, TimeProvider clock) => new
        {
            version = ServiceApp.Version,
            pool = pool.Status,
            gateway = new { reachable = gateway.Reachable(clock.GetUtcNow()), gateway.GatewayId, gateway.Sessions, gateway.LastPing },
            maintenance,
        }).On(ListenerRole.Admin);
    }

    static bool FakeTier(IWebHostEnvironment env, ServiceOptions o) => env.IsDevelopment() && o.Instances.SkipLevelFetch;
}
