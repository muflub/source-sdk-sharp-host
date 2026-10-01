using SourceSharp.Host.Abstractions;

namespace SourceSharp.Host.Tests;

public class ServiceOptionsFacts
{
    static IReadOnlyList<string> Validate(Action<ServiceOptions> edit, string env = "Production")
    {
        var o = new ServiceOptions();
        edit(o);
        return ServiceOptionsValidator.Validate(o, env);
    }

    [Fact]
    public void Defaults_are_valid_in_production() => Assert.Empty(Validate(_ => { }));

    [Fact]
    public void Admin_on_ipv6_loopback_is_accepted() => Assert.Empty(Validate(o => o.Listen.Admin = "[::1]:5000"));

    [Fact]
    public void Admin_on_any_address_is_refused() =>
        Assert.Contains(Validate(o => o.Listen.Admin = "0.0.0.0:5000"), e => e.Contains("loopback"));

    [Fact]
    public void Admin_on_a_pod_address_is_refused() =>
        Assert.Contains(Validate(o => o.Listen.Admin = "10.42.0.7:5000"), e => e.Contains("loopback"));

    [Fact]
    public void Admin_that_is_not_an_endpoint_is_refused() =>
        Assert.Contains(Validate(o => o.Listen.Admin = "localhost"), e => e.Contains("not ip:port"));

    [Fact]
    public void Fake_engine_image_is_refused_outside_development() =>
        Assert.Contains(Validate(o => o.Instances.EngineImage = "localhost:30500/descent-fake:abc"), e => e.Contains("fake"));

    [Fact]
    public void Fake_engine_image_is_accepted_in_development() =>
        Assert.Empty(Validate(o => o.Instances.EngineImage = "localhost:30500/descent-fake:abc", "Development"));

    [Fact]
    public void Skipping_the_mod_init_container_is_refused_outside_development() =>
        Assert.Contains(Validate(o => o.Instances.SkipModInit = true), e => e.Contains("SkipModInit"));

    [Fact]
    public void Skipping_the_level_fetch_is_refused_outside_development() =>
        Assert.Contains(Validate(o => o.Instances.SkipLevelFetch = true), e => e.Contains("SkipLevelFetch"));

    [Fact]
    public void Extra_pod_env_is_refused_outside_development() =>
        Assert.Contains(Validate(o => o.Instances.ExtraEnv["X"] = "1"), e => e.Contains("ExtraEnv"));

    [Fact]
    public void The_fake_tier_with_interpose_is_refused() =>
        Assert.Contains(Validate(o => { o.Instances.SkipModInit = true; o.Instances.RelayMode = "Interpose"; }, "Development"), e => e.Contains("RelayMode"));

    [Fact]
    public void The_fake_tier_with_loopback_is_accepted() =>
        Assert.Empty(Validate(o => { o.Instances.SkipModInit = true; o.Instances.RelayMode = "Loopback"; }, "Development"));

    [Fact]
    public void Mod_name_with_a_dash_is_refused() =>
        Assert.Contains(Validate(o => o.Mod.Name = "des-cent"), e => e.Contains("Mod.Name"));

    [Fact]
    public void Lease_ttl_not_above_the_heartbeat_is_refused() =>
        Assert.Contains(Validate(o => o.Lease.Ttl = TimeSpan.FromSeconds(5)), e => e.Contains("Lease.Ttl"));

    [Fact]
    public void Image_ref_prefers_the_digest_over_the_tag()
    {
        var r = new ImageRef { Registry = "reg:5000", Name = "descent-mod", Tag = "v1", Digest = "sha256:ab" };
        Assert.Equal("reg:5000/descent-mod@sha256:ab", r.ToString());
        r.Digest = null;
        Assert.Equal("reg:5000/descent-mod:v1", r.ToString());
    }

    [Fact]
    public void Per_depth_pool_size_falls_back_to_the_default()
    {
        var m = new MapPoolOptions { DefaultPerDepth = 3, PerDepth = { [5] = 1 } };
        Assert.Equal((3, 1), (m.PerDepthFor(4), m.PerDepthFor(5)));
    }

    [Fact]
    public void Contract_supports_same_major_and_not_newer_minor()
    {
        Assert.True(SourceSharp.Host.Contracts.HostContract.Supports("1.0.0"));
        Assert.False(SourceSharp.Host.Contracts.HostContract.Supports("2.0.0"));
        Assert.False(SourceSharp.Host.Contracts.HostContract.Supports("1.1.0"));
        Assert.False(SourceSharp.Host.Contracts.HostContract.Supports("garbage"));
    }
}
