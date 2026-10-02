using TF2.Descent.Service.Api;
using SourceSharp.Host.Abstractions;
using P = SourceSharp.Host.Proto;

namespace SourceSharp.Host.Sdk.Tests;

/// <summary>IHostCommands: the stream's down half as events raised inside Pump.</summary>
public class CommandFacts
{
    static async Task<(SdkHarness H, HostSdk Sdk)> Connected(SdkHarness h, string id = "hub-1", InstanceKind kind = InstanceKind.Hub)
    {
        await h.Instance(id, kind, kind == InstanceKind.Level ? 3 : 0);
        var sdk = h.Sdk(id);
        await sdk.PumpUntil(() => sdk.Session.Connected && h.Streams.IsOpen(id), what: "the stream");
        return (h, sdk);
    }

    [Fact]
    public async Task The_simple_commands_arrive_as_typed_events()
    {
        await using var h = await SdkHarness.Start();
        var (_, sdk) = await Connected(h);
        var got = new List<object>();
        sdk.Commands.Drain += c => got.Add(c with { CommandId = "" });
        sdk.Commands.Kick += c => got.Add(c with { CommandId = "" });
        sdk.Commands.Shutdown += c => got.Add(c with { CommandId = "" });
        sdk.Commands.Retry += c => got.Add(c with { CommandId = "" });
        sdk.Commands.Say += c => got.Add(c with { CommandId = "" });
        await h.Streams.Send("hub-1", InstanceStreams.ToProto(new InstanceCommand.Drain("reap")));
        await h.Streams.Send("hub-1", InstanceStreams.ToProto(new InstanceCommand.Kick("7656", "banned")));
        await h.Streams.Send("hub-1", InstanceStreams.ToProto(new InstanceCommand.Shutdown("bye")));
        await h.Streams.Send("hub-1", InstanceStreams.ToProto(new InstanceCommand.Retry("7656")));
        await h.Streams.Send("hub-1", InstanceStreams.ToProto(new InstanceCommand.Say("hello")));
        await sdk.PumpUntil(() => got.Count == 5, what: "five events");
        Assert.Equal<object>([new DrainCommand("", "reap"), new KickCommand("", "7656", "banned"), new ShutdownCommand("", "bye"),
            new RetryCommand("", "7656"), new SayCommand("", "hello")], got);
    }

    [Fact]
    public async Task A_command_is_not_raised_until_Pump()
    {
        await using var h = await SdkHarness.Start();
        var (_, sdk) = await Connected(h);
        var said = 0;
        sdk.Commands.Say += _ => said++;
        var acked = sdk.Metrics.HeartbeatsAcked;
        await h.Streams.Send("hub-1", new P.ServerCommand { Say = new P.Say { Text = "x" } });
        // The stream is ordered: once a later ack has been read, the Say has been read too.
        await Pumping.Until(() => sdk.Metrics.HeartbeatsAcked > acked + 1, what: "a later ack");
        Assert.Equal(0, said);
        sdk.Pump();
        Assert.Equal(1, said);
    }

    [Fact]
    public async Task Exec_output_returns_to_the_service_under_the_command_id()
    {
        await using var h = await SdkHarness.Start();
        var (_, sdk) = await Connected(h);
        ExecRequest? exec = null;
        sdk.Commands.Exec += e => exec = e;
        await h.Streams.Send("hub-1", new P.ServerCommand { CommandId = "cmd-1", Exec = new P.Exec { Command = "status" } });
        await sdk.PumpUntil(() => exec is not null, what: "the Exec event");
        Assert.Equal("status", exec!.Command);
        Assert.True((await sdk.Pumped(exec.Reply("hostname: descent"))).Ok);
        Assert.Equal("hostname: descent", h.Chatter.ExecOutput("cmd-1"));
    }

    [Fact]
    public async Task HopReady_is_sent_only_after_the_game_checkpoints_and_completes_the_hop()
    {
        await using var h = await SdkHarness.Start();
        var (_, sdk) = await Connected(h);
        var c = (await sdk.Pumped(sdk.Characters.Create("76561198000000001", "scout", "Ann"))).Value;
        var lease = (await sdk.Pumped(sdk.Characters.Lease(c.Id))).Value;
        h.Hops.Watch = c.Id;
        HopRequest? hop = null;
        sdk.Commands.PrepareHop += r => hop = r;

        await h.Streams.Send("hub-1", InstanceStreams.ToProto(new InstanceCommand.PrepareHop("76561198000000001", "lvl-9")));
        await sdk.PumpUntil(() => hop is not null, what: "PrepareHop");
        Assert.Equal(("76561198000000001", "lvl-9", "lvl-9:76561198000000001"), (hop!.SteamId, hop.TargetInstance, hop.HopId));
        await Task.Delay(200);
        Assert.Empty(h.Hops.Ready); // the SDK never answers for the game

        var cp = await sdk.Pumped(lease.Checkpoint(c.Sheet));
        await sdk.Pumped(hop.Ready());
        var (hopId, steamId, versionAtHop) = Assert.Single(h.Hops.Ready);
        Assert.Equal(("lvl-9:76561198000000001", "76561198000000001"), (hopId, steamId));
        Assert.Equal(cp.Value.Version, versionAtHop); // the checkpoint landed before HopReady
    }

    [Fact]
    public async Task A_hop_completed_twice_sends_HopReady_once()
    {
        await using var h = await SdkHarness.Start();
        var (_, sdk) = await Connected(h);
        HopRequest? hop = null;
        sdk.Commands.PrepareHop += r => hop = r;
        await h.Streams.Send("hub-1", InstanceStreams.ToProto(new InstanceCommand.PrepareHop("7656", "lvl-2")));
        await sdk.PumpUntil(() => hop is not null, what: "PrepareHop");
        var first = hop!.Ready();
        Assert.Same(first, hop.Ready());
        await sdk.Pumped(first);
        Assert.Single(h.Hops.Ready);
    }

    [Fact]
    public async Task NextLevel_arrives_with_the_map_and_where_to_download_it()
    {
        await using var h = await SdkHarness.Start();
        var (_, sdk) = await Connected(h, "lvl-1", InstanceKind.Level);
        NextLevelInfo? next = null;
        sdk.Commands.NextLevel += n => next = n;
        await h.Streams.Send("lvl-1", InstanceStreams.ToProto(new InstanceCommand.NextLevel(2, "tf2_descent-2-abc", "http://127.0.0.1:5004/")));
        await sdk.PumpUntil(() => next is not null, what: "NextLevel");
        Assert.Equal((2, "tf2_descent-2-abc"), (next!.Depth, next.Map));
        Assert.Equal("http://127.0.0.1:5004/maps/tf2_descent-2-abc.bsp.bz2", next.MapUrl);
    }

    [Fact]
    public async Task NextLevel_carries_the_bsps_sha256_as_32_bytes()
    {
        await using var h = await SdkHarness.Start();
        var (_, sdk) = await Connected(h, "lvl-1", InstanceKind.Level);
        NextLevelInfo? next = null;
        sdk.Commands.NextLevel += n => next = n;
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("bsp"u8));
        await h.Streams.Send("lvl-1", InstanceStreams.ToProto(new InstanceCommand.NextLevel(2, "tf2_descent-2-abc", "", sha)));
        await sdk.PumpUntil(() => next is not null, what: "NextLevel");
        Assert.Equal(Convert.FromHexString(sha), next!.MapSha256);
    }

    [Fact]
    public async Task NextLevel_without_a_hash_has_none()
    {
        await using var h = await SdkHarness.Start();
        var (_, sdk) = await Connected(h, "lvl-1", InstanceKind.Level);
        NextLevelInfo? next = null;
        sdk.Commands.NextLevel += n => next = n;
        await h.Streams.Send("lvl-1", InstanceStreams.ToProto(new InstanceCommand.NextLevel(2, "tf2_descent-2-abc", "")));
        await sdk.PumpUntil(() => next is not null, what: "NextLevel");
        Assert.Null(next!.MapSha256);
    }

    [Fact]
    public void A_NextLevel_without_a_download_url_has_no_map_url() =>
        Assert.Null(new NextLevelInfo("c", 2, "tf2_descent-2-abc", "").MapUrl);
}
