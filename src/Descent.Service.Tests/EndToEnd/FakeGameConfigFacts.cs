using SourceSharp.Host.FakeGame;

namespace Descent.Service.Tests.EndToEnd;

/// <summary>What the fake reads from a game pod's arguments and environment.</summary>
public class FakeGameConfigFacts
{
    static readonly Dictionary<string, string?> Pod = new()
    {
        ["DESCENT_INSTANCE_ID"] = "env-id", ["RELAY_Listen"] = "127.0.0.9:5010", ["RELAY_InfoListen"] = "127.0.0.9:5011",
        ["RELAY_Health"] = "127.0.0.9:5012", ["RELAY_EngineEndpoint"] = "127.0.0.9:27015", ["FAKEGAME_HeartbeatMs"] = "250",
    };

    [Fact]
    public void The_engine_arguments_name_the_port_the_map_and_the_instance()
    {
        var c = FakeGameConfig.From(["-game", "descent", "-port", "28015", "+map", "descent-3-abc", "+descent_instance", "lvl-7"], Pod);
        Assert.Equal((28015, "descent-3-abc", "lvl-7", 250), (c.GamePort, c.Map, c.InstanceId, c.Fake.HeartbeatMs));
    }

    [Fact]
    public void The_relay_runs_in_loopback_mode_even_when_the_pod_asks_for_interpose()
    {
        var loopback = FakeGameConfig.From([], Pod);
        Assert.Equal(("Loopback", (string?)null), (loopback.Relay.Mode, loopback.Note)); // the unpatched input is taken as is
        var c = FakeGameConfig.From([], new Dictionary<string, string?>(Pod) { ["RELAY_Mode"] = "Interpose" });
        Assert.Equal("Loopback", c.Relay.Mode);
        Assert.Contains("Interpose ignored", c.Note);
    }

    [Fact]
    public void Events_echo_to_stderr_only_when_the_environment_asks()
    {
        Assert.False(FakeGameConfig.From([], Pod).Fake.EchoEvents);
        Assert.True(FakeGameConfig.From([], new Dictionary<string, string?>(Pod) { ["FAKEGAME_EchoEvents"] = "true" }).Fake.EchoEvents);
    }

    [Fact]
    public void The_control_port_binds_every_address_only_when_asked()
    {
        var c = FakeGameConfig.From([], new Dictionary<string, string?>(Pod) { ["FAKEGAME_ControlListen"] = "127.0.0.9:5020" });
        Assert.Equal("127.0.0.9:5020", c.Fake.Control.ToString());
        var any = FakeGameConfig.From([], new Dictionary<string, string?>(Pod) { ["FAKEGAME_ControlListen"] = "127.0.0.9:5020", ["FAKEGAME_ControlAnyAddress"] = "true" });
        Assert.Equal("0.0.0.0:5020", any.Fake.Control.ToString());
    }
}
