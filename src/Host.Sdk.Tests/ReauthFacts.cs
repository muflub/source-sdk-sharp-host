using TF2.Descent.Service.Api;
using SourceSharp.Host.Abstractions;

namespace SourceSharp.Host.Sdk.Tests;

/// <summary>SDK 2.6.0 (plan_steam_auth S-20): a Reauth command reaches the game as an event; Submit reaches the service.</summary>
public class ReauthFacts
{
    [Fact]
    public async Task A_reauth_command_arrives_with_its_fields_and_submit_carries_the_ticket_back()
    {
        await using var h = await SdkHarness.Start();
        await h.Instance("hub-1", InstanceKind.Hub, 0);
        var sdk = h.Sdk("hub-1");
        await sdk.PumpUntil(() => sdk.Session.Connected && h.Streams.IsOpen("hub-1"), what: "the stream");
        ReauthRequest? got = null;
        sdk.Commands.Reauth += r => got = r;
        var deadline = DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000);

        await h.Streams.Send("hub-1", InstanceStreams.ToProto(new InstanceCommand.Reauth("7656", 90071992547409921UL, "nonce-1", deadline, "127.1.0.5:29005")));
        await sdk.PumpUntil(() => got is not null, what: "the reauth event");

        Assert.Equal(("7656", 90071992547409921UL, "nonce-1", deadline, "127.1.0.5:29005"),
                     (got!.SteamId, got.GameServerSteamId, got.Nonce, got.Deadline, got.Peer));

        h.Sessions.TicketOutcome = "Denied";
        var submitted = got.Submit([1, 2, 3]);
        await sdk.PumpUntil(() => submitted.IsCompleted, what: "the submit");

        var (peer, nonce, ticket) = Assert.Single(h.Sessions.Tickets);
        Assert.Equal(("127.1.0.5:29005", "nonce-1"), (peer, nonce));
        Assert.Equal([1, 2, 3], ticket);
        Assert.Equal(TicketOutcome.Denied, (await submitted).Value);
    }

    [Fact]
    public async Task An_outcome_this_sdk_does_not_know_reads_as_unavailable()
    {
        await using var h = await SdkHarness.Start();
        await h.Instance("hub-1", InstanceKind.Hub, 0);
        var sdk = h.Sdk("hub-1");
        await sdk.PumpUntil(() => sdk.Session.Connected && h.Streams.IsOpen("hub-1"), what: "the stream");
        ReauthRequest? got = null;
        sdk.Commands.Reauth += r => got = r;
        await h.Streams.Send("hub-1", InstanceStreams.ToProto(new InstanceCommand.Reauth("7656", 1, "n", DateTimeOffset.UnixEpoch, "p")));
        await sdk.PumpUntil(() => got is not null, what: "the reauth event");

        h.Sessions.TicketOutcome = "SomethingNewer";
        var submitted = got!.Submit([9]);
        await sdk.PumpUntil(() => submitted.IsCompleted, what: "the submit");
        Assert.Equal(TicketOutcome.Unavailable, (await submitted).Value);
    }
}

/// <summary>A game's own facts build a ReauthRequest with their own submit (no real host can send one).</summary>
public class ReauthRequestForGameFacts
{
    [Fact]
    public async Task A_game_built_request_carries_its_fields_and_submit_runs_the_games_function()
    {
        byte[]? seen = null;
        var r = new ReauthRequest("c1", "7656", 9, "n1", DateTimeOffset.UnixEpoch, "127.1.0.2:29005",
            t => { seen = t; return Task.FromResult(HostResult<TicketOutcome>.Success(TicketOutcome.Denied)); });

        var outcome = await r.Submit([1, 2]);

        Assert.Equal(("7656", 9UL, "n1", "127.1.0.2:29005"), (r.SteamId, r.GameServerSteamId, r.Nonce, r.Peer));
        Assert.Equal([1, 2], seen);
        Assert.Equal(TicketOutcome.Denied, outcome.Value);
    }
}

/// <summary>SDK 2.6.0 (plan_steam_auth S-18): the mod's browser block rides on Booting.</summary>
public class BrowserBlockFacts
{
    [Fact]
    public async Task A_block_with_known_keys_is_stored_for_the_publisher()
    {
        await using var h = await SdkHarness.Start();
        await h.Instance("hub-1", InstanceKind.Hub, 0, InstanceState.Booting);
        var o = SdkHarness.FastOptions();
        o.ModuleSource = new FixedModuleSource(SessionFacts.ModuleOnDisk().Module);
        var sdk = h.Sdk("hub-1", o);
        sdk.Session.Browser = new BrowserBlock("Descent {players}", Map: "depth {deepest}", Rules: new Dictionary<string, string> { ["open"] = "{levels_open}" });

        var r = await sdk.Pumped(sdk.Session.Booting("tf2_descent_town"));

        Assert.True(r.Ok, r.Refusal?.ToString());
        var stored = await h.Data.ReadAsync((tx, _) => tx.Settings.Get(TF2.Descent.Service.Gateway.BrowserPublisher.BlockKey));
        var block = Google.Protobuf.JsonParser.Default.Parse<SourceSharp.Host.Proto.Browser>(stored!.ValueJson);
        Assert.Equal(("Descent {players}", "depth {deepest}", "{levels_open}"), (block.Name, block.Map, block.Rules["open"]));
    }

    [Fact]
    public async Task A_block_naming_an_unknown_key_refuses_booting()
    {
        await using var h = await SdkHarness.Start();
        await h.Instance("hub-1", InstanceKind.Hub, 0, InstanceState.Booting);
        var o = SdkHarness.FastOptions();
        o.ModuleSource = new FixedModuleSource(SessionFacts.ModuleOnDisk().Module);
        var sdk = h.Sdk("hub-1", o);
        sdk.Session.Browser = new BrowserBlock("Descent {playres}");

        var r = await sdk.Pumped(sdk.Session.Booting("tf2_descent_town"));

        Assert.False(r.Ok);
        Assert.Contains("{playres}", r.Refusal!.ToString());
        Assert.Null(await h.Data.ReadAsync((tx, _) => tx.Settings.Get(TF2.Descent.Service.Gateway.BrowserPublisher.BlockKey)));
    }
}
