using Descent.Service.Gateway;
using Descent.Service.Tests.Ledger;
using Microsoft.Extensions.Logging.Abstractions;
using SourceSharp.Host.Abstractions;

namespace Descent.Service.Tests.Gateway;

/// <summary>The control client: the registry stays the authority for the table it syncs.</summary>
public class GatewayControlClientFacts
{
    [Fact]
    public async Task A_route_the_service_set_is_in_the_next_synced_table()
    {
        await using var h = await LedgerHarness.Create();
        var now = h.D.Clock.GetUtcNow();
        await h.W(tx => tx.Sessions.Upsert(new SessionRecord("s1", "1.2.3.4:27005", "765", null, "10.0.0.1:5010", "127.1.0.1:29005", "open", now, now, now)));
        var options = new ServiceOptions(); // no GatewayControl address: nothing is sent, the table is still built
        options.Listen.GatewayControl = "";
        using var client = new GatewayControlClient(options, h.D.Data, h.D.Clock, NullLogger<GatewayControlClient>.Instance);
        Assert.Equal("10.0.0.1:5010", Assert.Single((await client.BuildTable(default)).Routes).Backend); // before: the old backend

        await client.SetRoute("1.2.3.4:27005", "10.0.0.2:5010", "765", holdUntilReady: false);

        Assert.Equal("10.0.0.2:5010", Assert.Single((await client.BuildTable(default)).Routes).Backend);
    }

    [Fact]
    public async Task A_route_to_another_backend_forgets_the_peer_it_had_on_the_old_one()
    {
        await using var h = await LedgerHarness.Create();
        var now = h.D.Clock.GetUtcNow();
        await h.W(tx => tx.Sessions.Upsert(new SessionRecord("s1", "1.2.3.4:27005", "765", null, "10.0.0.1:5010", "127.1.0.2:29005", "open", now, now, now)));
        var options = new ServiceOptions();
        options.Listen.GatewayControl = "";
        using var client = new GatewayControlClient(options, h.D.Data, h.D.Clock, NullLogger<GatewayControlClient>.Instance);

        await client.SetRoute("1.2.3.4:27005", "10.0.0.2:5010", "765", holdUntilReady: false);

        // Peers are unique only within a backend: the old peer must not match anyone's join on the new one.
        Assert.Null(await h.R(tx => tx.Sessions.ByBackendPeer("10.0.0.2:5010", "127.1.0.2:29005")));
    }
}
