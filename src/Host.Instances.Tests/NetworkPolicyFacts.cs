using SourceSharp.Host.Testing;
using YamlDotNet.RepresentationModel;

namespace SourceSharp.Host.Instances.Tests;

/// <summary>D-H10: deploy/k8s/base/networkpolicy.yaml's game-pods policy.</summary>
public class NetworkPolicyFacts
{
    static YamlMappingNode GamePods()
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(File.ReadAllText(RepoFiles.Path_("deploy", "k8s", "base", "networkpolicy.yaml"))));
        var doc = stream.Documents.Select(d => (YamlMappingNode)d.RootNode)
            .Single(d => ((YamlScalarNode)((YamlMappingNode)d["metadata"])["name"]).Value == "game-pods");
        return (YamlMappingNode)doc["spec"];
    }

    static IEnumerable<YamlMappingNode> Rules(string kind) => ((YamlSequenceNode)GamePods()[kind]).Cast<YamlMappingNode>();

    static string From(YamlMappingNode rule) =>
        ((YamlScalarNode)((YamlMappingNode)((YamlMappingNode)((YamlMappingNode)((YamlSequenceNode)rule["from"])[0])["podSelector"])["matchLabels"])["app"]).Value!;

    static IEnumerable<(string Protocol, string? Port)> Ports(YamlMappingNode rule) =>
        rule.Children.ContainsKey("ports")
            ? ((YamlSequenceNode)rule["ports"]).Cast<YamlMappingNode>().Select(p =>
                (((YamlScalarNode)p["protocol"]).Value!, p.Children.ContainsKey("port") ? ((YamlScalarNode)p["port"]).Value : null))
            : [("ANY", null)];

    /// <summary>plan_steam_auth S-6: a game pod can reach nothing on the internet, so its engine can reach no Steam.</summary>
    [Fact]
    public void Game_pods_reach_only_the_service_and_dns()
    {
        var egress = Rules("egress").ToList();
        Assert.Equal(2, egress.Count);
        var service = egress.Single(r => r.Children.ContainsKey("to"));
        Assert.Equal("tf2-descent-service",
            ((YamlScalarNode)((YamlMappingNode)((YamlMappingNode)((YamlMappingNode)((YamlSequenceNode)service["to"])[0])["podSelector"])["matchLabels"])["app"]).Value);
        Assert.Equal([("TCP", "5001"), ("TCP", "5000")], Ports(service));
        var dns = egress.Single(r => !r.Children.ContainsKey("to"));
        Assert.Equal([("UDP", "53"), ("TCP", "53")], Ports(dns));
    }

    [Fact]
    public void Gateway_reaches_only_the_sidecar_relay_port()
    {
        var gateway = Rules("ingress").Single(r => From(r) == "tf2-descent-gateway");
        Assert.Equal([("TCP", "5010")], Ports(gateway));
    }

    [Fact]
    public void No_ingress_rule_admits_udp()
    {
        var ports = Rules("ingress").SelectMany(Ports).ToList();
        Assert.NotEmpty(ports);
        Assert.All(ports, p => Assert.Equal("TCP", p.Protocol));
    }

    [Fact]
    public void Only_the_gateway_and_the_service_are_admitted()
    {
        Assert.Equal(["tf2-descent-gateway", "tf2-descent-service"], Rules("ingress").Select(From).Order());
    }

    [Fact]
    public void Egress_is_the_service_api_and_maps_and_dns()
    {
        var egress = Rules("egress").SelectMany(Ports).Select(p => $"{p.Protocol}/{p.Port}").Order(StringComparer.Ordinal);
        Assert.Equal(["TCP/5000", "TCP/5001", "TCP/53", "UDP/53"], egress);
    }
}
