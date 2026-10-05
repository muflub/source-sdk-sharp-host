using System.Text;
using SourceSharp.Host.FakeGame.Control;

namespace TF2.Descent.Service.Tests.EndToEnd;

/// <summary>
/// plan_steam_auth end to end (the service, the gateway with the fake Steam, the fake game through the SDK, toy
/// clients): a client is validated before it reaches the hub, and a gateway restart re-auths a playing client
/// through the game without a reconnect (S-11, S-20).
/// </summary>
public class SteamEndToEndFacts
{
    const ulong Alice = 76561198000000001UL;

    static int Begins(E2eWorld w) => w.Steam!.Calls.Count(c => c.StartsWith("begin:", StringComparison.Ordinal));

    static async Task<IReadOnlyList<string>> HubEvents(E2eWorld w) => (await (await w.HubControl()).StatusAsync(new Empty())).Events;

    [Fact]
    public async Task A_client_is_validated_by_steam_before_it_joins_the_hub()
    {
        await using var w = await E2eWorld.Start(steam: true);
        var (c, _) = await w.Joined(Alice);
        Assert.Equal(1, Begins(w));
        Assert.Equal("valid", w.Relay.SessionOf(c.LocalEndPoint)!.Auth);
        var session = w.Relay.SessionOf(c.LocalEndPoint)!.SessionId;
        await w.Until(async () => await w.Data.ReadAsync((tx, _) => tx.SteamAuth.Get(session)) is { State: "valid" }, "the verdict row");
    }

    [Fact]
    public async Task A_gateway_restart_reauths_a_playing_client_through_the_game_and_it_keeps_playing()
    {
        await using var w = await E2eWorld.Start(steam: true);
        var (c, _) = await w.Joined(Alice);
        var hub = (await w.Hub())!.Id;
        var epochBefore = w.Steam!.Status.Epoch;

        await w.RestartGatewayAsync();
        Assert.NotEqual(epochBefore, w.Steam!.Status.Epoch); // a new Steam logon: the old auth is gone

        await w.Until(() =>
        {
            c.SendKeepalive(); // a playing client keeps sending; the carried session sees it
            return Task.FromResult(w.Relay.SessionOf(c.LocalEndPoint)?.Auth == "valid");
        }, "the carried session re-authed");

        Assert.Equal(1, c.ReauthsAnswered);
        Assert.Contains($"reauth {Alice} Ok", await HubEvents(w));
        Assert.Equal(hub, await c.KeepaliveAsync(E2eWorld.T)); // still on the hub: no reconnect
        var session = w.Relay.SessionOf(c.LocalEndPoint)!.SessionId;
        await w.Until(async () => await w.Data.ReadAsync((tx, _) => tx.SteamAuth.Get(session)) is { State: "valid" } row
                                  && row.Epoch == w.Steam.Status.Epoch, "the row rewritten under the new logon");
    }

    [Fact]
    public async Task A_client_whose_fresh_ticket_steam_denies_is_kicked_after_the_restart()
    {
        await using var w = await E2eWorld.Start(steam: true);
        var (c, _) = await w.Joined(Alice);
        c.ReauthTicketTail = Encoding.ASCII.GetBytes("deny:VAC banned");

        await w.RestartGatewayAsync();

        await w.Until(async () =>
        {
            c.SendKeepalive();
            return (await HubEvents(w)).Any(e => e.StartsWith($"kick {Alice} Steam: VAC banned", StringComparison.Ordinal));
        }, "the kick at the hub");
        Assert.Equal(1, c.ReauthsAnswered);
    }
}
