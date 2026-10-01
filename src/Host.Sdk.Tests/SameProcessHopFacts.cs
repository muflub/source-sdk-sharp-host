using TF2.Descent.Service.Api;
using SourceSharp.Host.Abstractions;

namespace SourceSharp.Host.Sdk.Tests;

/// <summary>
/// D-H13's same-process hop: a local service sends PrepareHop with the target's map and
/// connection record; when the game boots that map the SDK becomes the target instance.
/// </summary>
public class SameProcessHopFacts
{
    const string Steam = "76561198000000001";
    const string Map = "tf2_descent-1-abc";

    static async Task<(SdkHarness H, HostSdk Sdk)> Hub(SdkHarness h, bool module = false)
    {
        await h.Instance("hub-1", InstanceKind.Hub);
        await h.Instance("lvl-9", InstanceKind.Level, 1, InstanceState.Booting);
        var o = SdkHarness.FastOptions();
        if (module) o.ModuleSource = new FixedModuleSource(SessionFacts.ModuleOnDisk().Module);
        var sdk = h.Sdk("hub-1", o);
        await sdk.PumpUntil(() => sdk.Session.Connected && h.Streams.IsOpen("hub-1"), what: "the stream");
        return (h, sdk);
    }

    static readonly string[] Files = ["/local/maps/levels/tf2_descent-1-abc.bsp", "/local/maps/levels/tf2_descent-1-abc.nav3d", "/local/maps/levels/tf2_descent-1-abc.map2d"];

    static string Record(SdkHarness h, string instance = "lvl-9") =>
        new HostLocalFile(h.Proxy.Uri.ToString(), instance, SdkHarness.Token(instance), Map: Map, LevelFiles: Files, Port: 27015).ToJson();

    static async Task<HopRequest> Hop(SdkHarness h, HostSdk sdk, string map = Map, string? record = null)
    {
        HopRequest? hop = null;
        sdk.Commands.PrepareHop += r => hop = r;
        await h.Streams.Send("hub-1", InstanceStreams.ToProto(new InstanceCommand.PrepareHop(Steam, "lvl-9", map, record ?? "")));
        await sdk.PumpUntil(() => hop is not null, what: "PrepareHop");
        return hop!;
    }

    [Fact]
    public async Task A_hop_with_the_targets_record_is_same_process_and_names_the_map()
    {
        await using var h = await SdkHarness.Start();
        var (_, sdk) = await Hub(h);
        var hop = await Hop(h, sdk, record: Record(h));
        Assert.Equal((true, Map, "lvl-9"), (hop.IsSameProcess, hop.Map, hop.TargetInstance));
    }

    [Fact]
    public async Task A_same_process_hop_carries_the_targets_map_and_level_files()
    {
        await using var h = await SdkHarness.Start();
        var (_, sdk) = await Hub(h);
        var hop = await Hop(h, sdk, record: Record(h));
        Assert.Equal(Map, hop.Target!.Map);
        Assert.Equal(Files, hop.Target.LevelFiles);
    }

    [Fact]
    public async Task A_hop_without_a_record_is_an_ordinary_hop()
    {
        await using var h = await SdkHarness.Start();
        var (_, sdk) = await Hub(h);
        var hop = await Hop(h, sdk);
        Assert.False(hop.IsSameProcess);
        Assert.Null(hop.Target);
        Assert.Equal(Map, hop.Map);
    }

    [Fact]
    public async Task A_record_for_another_instance_is_not_followed()
    {
        await using var h = await SdkHarness.Start();
        var (_, sdk) = await Hub(h);
        var hop = await Hop(h, sdk, record: Record(h, "lvl-other"));
        Assert.False(hop.IsSameProcess);
    }

    [Fact]
    public async Task Booting_the_hops_map_boots_as_the_target_instance()
    {
        await using var h = await SdkHarness.Start();
        var (_, sdk) = await Hub(h, module: true);
        var hop = await Hop(h, sdk, record: Record(h));
        await sdk.Pumped(hop.Ready());
        Assert.Single(h.Hops.Ready); // HopReady went out as the hub, before the switch
        Assert.Equal("hub-1", sdk.Endpoint.InstanceId);

        var boot = await sdk.Pumped(sdk.Session.Booting(Map));
        Assert.True(boot.Ok, boot.ToString());
        Assert.Equal(("level", 1), (boot.Value.InstanceKind, boot.Value.Depth));
        Assert.Equal("lvl-9", sdk.Endpoint.InstanceId);
    }

    [Fact]
    public async Task After_the_switch_the_stream_is_the_targets()
    {
        await using var h = await SdkHarness.Start();
        var (_, sdk) = await Hub(h, module: true);
        var hop = await Hop(h, sdk, record: Record(h));
        await sdk.Pumped(hop.Ready());
        Assert.False(h.Streams.IsOpen("lvl-9"));
        await sdk.Pumped(sdk.Session.Booting(Map));
        await sdk.PumpUntil(() => h.Streams.IsOpen("lvl-9") && !h.Streams.IsOpen("hub-1"), what: "the target's stream, and the hub's closed");
    }

    [Fact]
    public async Task Booting_another_map_stays_the_requesting_instance()
    {
        await using var h = await SdkHarness.Start();
        var (_, sdk) = await Hub(h, module: true);
        await Hop(h, sdk, record: Record(h));
        // The hub row is live, so Booting as the hub is refused; as lvl-9 (booting) it would succeed.
        var boot = await sdk.Pumped(sdk.Session.Booting("tf2_descent_town"));
        Assert.False(boot.Ok);
        Assert.Equal("not_booting", boot.Refusal!.Reason);
        Assert.Equal("hub-1", sdk.Endpoint.InstanceId);
    }
}
