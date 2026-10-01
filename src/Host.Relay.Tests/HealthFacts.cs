using System.Net;
using System.Text.Json.Nodes;
using SourceSharp.Host.FakeClient;

namespace SourceSharp.Host.Relay.Tests;

/// <summary>/healthz as the relay library serves it for any host (D-H11).</summary>
public class HealthFacts
{
    static async Task<(HttpStatusCode Code, JsonObject Body)> Get(RelayHost h)
    {
        using var http = new HttpClient { DefaultRequestVersion = HttpVersion.Version20, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact };
        var r = await http.GetAsync($"http://{h.EndPoint}/healthz");
        return (r.StatusCode, JsonNode.Parse(await r.Content.ReadAsStringAsync())!.AsObject());
    }

    [Fact]
    public async Task Healthz_reports_the_relay_up_with_its_peer_count()
    {
        using var engine = new ToyBackend("pod");
        await using var h = new RelayHost(engine.EndPoint);
        var (code, body) = await Get(h);
        Assert.Equal(HttpStatusCode.OK, code);
        Assert.Equal((true, 0), (body["relay"]!["up"]!.GetValue<bool>(), body["relay"]!["peers"]!.GetValue<int>()));
    }

    [Fact]
    public async Task Healthz_carries_the_hosts_own_state()
    {
        using var engine = new ToyBackend("pod");
        await using var h = new RelayHost(engine.EndPoint, health: j => j["engine"] = new JsonObject { ["state"] = "running" });
        Assert.Equal("running", (await Get(h)).Body["engine"]!["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task Healthz_is_503_when_the_host_says_not_ok()
    {
        using var engine = new ToyBackend("pod");
        await using var h = new RelayHost(engine.EndPoint, health: j => j["ok"] = false);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Get(h)).Code);
    }
}
