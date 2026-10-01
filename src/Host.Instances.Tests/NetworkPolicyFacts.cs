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

    [Fact]
    public void Gateway_reaches_only_the_sidecar_relay_port()
    {
        var gateway = Rules("ingress").Single(r => From(r) == "descent-gateway");
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
        Assert.Equal(["descent-gateway", "descent-service"], Rules("ingress").Select(From).Order());
    }

    [Fact]
    public void Egress_is_the_service_api_and_maps_and_dns()
    {
        var egress = Rules("egress").SelectMany(Ports).Select(p => $"{p.Protocol}/{p.Port}").Order(StringComparer.Ordinal);
        Assert.Equal(["TCP/5000", "TCP/5001", "TCP/53", "UDP/53"], egress);
    }
}
