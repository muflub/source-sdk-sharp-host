using System.Text.RegularExpressions;
using SourceSharp.Host.Gateway.Tests.Support;

namespace SourceSharp.Host.Gateway.Tests;

/// <summary>The relay's /metrics series (plan Q25).</summary>
public class MetricsFacts
{
    static double Value(string text, string series)
    {
        var m = Regex.Match(text, $@"^{Regex.Escape(series)} (\S+)$", RegexOptions.Multiline);
        Assert.True(m.Success, $"{series} not exported");
        return double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task A_connected_client_is_counted_in_sessions_and_packets()
    {
        await using var rig = new Rig();
        var hub = rig.Backend("hub");
        rig.SetDefault(hub);
        var c = rig.Client(1);
        await c.ConnectAsync(Rig.T);
        await c.KeepaliveAsync(Rig.T);
        Assert.True(await c.WaitForAsync(x => x.Echoes.Count == 2, Rig.T)); // both echoes relayed before reading
        var text = await rig.Relay.Metrics.TextAsync();
        Assert.Equal(1, Value(text, "gateway_sessions"));
        Assert.Equal(4, Value(text, "gateway_packets_in_total"));  // q, k, keepalive after accept, keepalive
        Assert.Equal(4, Value(text, "gateway_packets_out_total")); // A, B, two echoes
        Assert.Equal(hub.Log.Sum(r => r.Bytes.Length), Value(text, "gateway_bytes_in_total"));
    }

    [Fact]
    public async Task An_expiry_is_counted_and_the_session_gauge_drops()
    {
        await using var rig = new Rig();
        var hub = rig.Backend("hub");
        rig.SetDefault(hub);
        await rig.Client(1).ConnectAsync(Rig.T);
        rig.Time.Advance(rig.Options.SessionExpiry);
        var text = await rig.Relay.Metrics.TextAsync();
        Assert.Equal((0d, 1d), (Value(text, "gateway_sessions"), Value(text, "gateway_session_expiries_total")));
    }

    [Fact]
    public async Task A_route_flip_is_counted()
    {
        await using var rig = new Rig();
        var a = rig.Backend("a");
        var b = rig.Backend("b");
        rig.SetDefault(a);
        var c = rig.Client(1);
        await c.ConnectAsync(Rig.T);
        rig.Relay.SetRoute(rig.Next(), c.LocalEndPoint.ToString(), b.EndPoint.ToString(), "76561198000000001");
        a.SendRetry(rig.PeerOf(c));
        Assert.True(await c.WaitForAsync(x => x.Connected && x.InstanceId == "b", Rig.T));
        Assert.Equal(1, Value(await rig.Relay.Metrics.TextAsync(), "gateway_route_flips_total"));
    }
}
