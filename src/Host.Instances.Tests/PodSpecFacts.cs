using System.Text.Json;
using k8s.Models;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Instances;

namespace SourceSharp.Host.Instances.Tests;

public class PodSpecFacts
{
    const string Token = "tok-3f9a1c0e5b7d4a2e8c6b0d1f3e5a7c9b";
    static readonly DateTimeOffset T0 = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    internal static InstanceRecord Row(InstanceKind kind, string id = "01J9ZABCDEFGHJKMNPQRSTVWXY", int depth = 7, string? hash = "abc123") =>
        new(id, kind, InstanceState.Creating, kind == InstanceKind.Hub ? 0 : depth, null, kind == InstanceKind.Hub ? null : hash,
            null, null, null, 0, "", null, null, null, 1, T0, null, null, null, null, null, null, null, 0, 27015);

    static InstanceOptions Options(Action<InstanceOptions>? change = null)
    {
        var o = new InstanceOptions
        {
            EngineImage = "reg.local/descent-engine:1",
            ModImage = new ImageRef { Registry = "reg.local", Name = "descent-mod", Tag = "42" },
            NodeSelector = new() { ["pool"] = "game" },
            Tolerations = [new PodToleration { Key = "game", Operator = "Equal", Value = "yes", Effect = "NoSchedule" }],
        };
        change?.Invoke(o);
        return o;
    }

    static readonly ModOptions Mod = new();

    static V1Pod Pod(InstanceKind kind, Action<InstanceOptions>? change = null) =>
        KubernetesMapping.ToPod(GamePodBuilder.Build(Options(change), Mod, Row(kind), Token), "descent");

    static V1Container Game(V1Pod p) => p.Spec.Containers.Single(c => c.Name == "game");
    static string? Env(V1Container c, string name) => c.Env.Single(e => e.Name == name).Value;

    [Fact]
    public void Name_is_descent_kind_and_lowercased_id()
    {
        Assert.Equal("descent-level-01j9zabcdefghjkmnpqrstvwxy", Pod(InstanceKind.Level).Metadata.Name);
        Assert.Equal("descent-hub-01j9zabcdefghjkmnpqrstvwxy", Pod(InstanceKind.Hub).Metadata.Name);
    }

    [Fact]
    public void Labels_carry_app_instance_and_kind()
    {
        var labels = Pod(InstanceKind.Level).Metadata.Labels;
        Assert.Equal("descent-game", labels["app"]);
        Assert.Equal("01J9ZABCDEFGHJKMNPQRSTVWXY", labels["descent/instance"]);
        Assert.Equal("level", labels["descent/kind"]);
        Assert.Equal("hub", Pod(InstanceKind.Hub).Metadata.Labels["descent/kind"]);
    }

    [Fact]
    public void Pod_is_never_restarted_and_has_the_configured_grace()
    {
        var p = Pod(InstanceKind.Level);
        Assert.Equal("Never", p.Spec.RestartPolicy);
        Assert.Equal(60, p.Spec.TerminationGracePeriodSeconds);
        Assert.Equal(90, Pod(InstanceKind.Level, o => o.ReapGrace = TimeSpan.FromSeconds(90)).Spec.TerminationGracePeriodSeconds);
    }

    [Fact]
    public void Mod_init_container_copies_the_mod_image_into_the_game_volume()
    {
        var mod = Pod(InstanceKind.Hub).Spec.InitContainers.Single(c => c.Name == "mod");
        Assert.Equal("reg.local/descent-mod:42", mod.Image);
        Assert.Equal(["sh", "-c", "cp -a /game/. /out/"], mod.Command);
        Assert.Equal(("game", "/out"), (mod.VolumeMounts.Single().Name, mod.VolumeMounts.Single().MountPath));
    }

    [Fact]
    public void Level_fetches_its_map_in_a_second_init_container()
    {
        var init = Pod(InstanceKind.Level).Spec.InitContainers;
        Assert.Equal(["mod", "fetch-level"], init.Select(c => c.Name));
        var fetch = init[1];
        Assert.Equal("reg.local/descent-engine:1", fetch.Image);
        Assert.Equal(["/opt/descent/fetch-level.sh", "descent-7-abc123"], fetch.Command);
        Assert.Equal("http://descent-service-internal:5000", Env(fetch, "DESCENT_INTERNAL_MAPS"));
        Assert.Equal(("maps-overlay", "/game/maps-overlay"), (fetch.VolumeMounts.Single().Name, fetch.VolumeMounts.Single().MountPath));
    }

    [Fact]
    public void Hub_has_no_fetch_level_init_container()
    {
        Assert.Equal(["mod"], Pod(InstanceKind.Hub).Spec.InitContainers.Select(c => c.Name));
    }

    [Fact]
    public void Fake_tier_skips_the_mod_init_container()
    {
        Assert.Null(Pod(InstanceKind.Hub, o => o.SkipModInit = true).Spec.InitContainers);
        Assert.Equal(["fetch-level"], Pod(InstanceKind.Level, o => o.SkipModInit = true).Spec.InitContainers.Select(c => c.Name));
    }

    [Fact]
    public void Game_container_runs_the_engine_image_through_the_entrypoint()
    {
        var g = Game(Pod(InstanceKind.Level));
        Assert.Equal("reg.local/descent-engine:1", g.Image);
        Assert.Equal(["/opt/descent/game-entrypoint.sh"], g.Command);
    }

    [Fact]
    public void Game_args_are_config_args_then_the_pods_dedicated_run()
    {
        var args = Game(Pod(InstanceKind.Level)).Args;
        string[] expected =
        [
            "-nohltv", "+tv_enable", "0", "-devapi", "-devapisocket", "-condebug", "-nomessagebox", "-nocrashdialog", "-assertlog",
            "-game", "/game/mods/descent", "-console", "-port", "27015", "-ip", "0.0.0.0",
            "+maxplayers", "4", "+map", "descent-7-abc123", "+descent_instance", "01J9ZABCDEFGHJKMNPQRSTVWXY",
        ];
        Assert.Equal(expected, args);
    }

    [Fact]
    public void Hub_runs_the_town_map_with_hub_maxplayers()
    {
        var args = Game(Pod(InstanceKind.Hub)).Args.ToList();
        Assert.Equal("descent_town", args[args.IndexOf("+map") + 1]);
        Assert.Equal("32", args[args.IndexOf("+maxplayers") + 1]);
    }

    [Fact]
    public void Game_env_names_service_instance_sidecar_and_dirs()
    {
        var g = Game(Pod(InstanceKind.Level));
        Assert.Equal("descent-service:5001", Env(g, "DESCENT_SERVICE"));
        Assert.Equal("01J9ZABCDEFGHJKMNPQRSTVWXY", Env(g, "DESCENT_INSTANCE_ID"));
        Assert.Equal("127.0.0.1:5011", Env(g, "DESCENT_SIDECAR_INFO"));
        Assert.Equal("/tmp/game", Env(g, "TMPDIR"));
        Assert.Equal("/tmp/devapi", Env(g, "SOURCESHARP_DEVAPI_DIR"));
    }

    [Fact]
    public void Game_token_comes_from_the_pods_secret()
    {
        var p = Pod(InstanceKind.Level);
        var tok = Game(p).Env.Single(e => e.Name == "DESCENT_INSTANCE_TOKEN");
        Assert.Null(tok.Value);
        Assert.Equal(p.Metadata.Name + "-token", tok.ValueFrom.SecretKeyRef.Name);
        Assert.Equal("token", tok.ValueFrom.SecretKeyRef.Key);
    }

    [Fact]
    public void Token_never_appears_in_the_pod()
    {
        var spec = GamePodBuilder.Build(Options(), Mod, Row(InstanceKind.Level), Token);
        Assert.Equal(Token, spec.Secret!.Data["token"]); // positive arm: the token went somewhere
        var json = JsonSerializer.Serialize(KubernetesMapping.ToPod(spec, "descent"));
        Assert.DoesNotContain(Token, json);
        Assert.Contains("DESCENT_INSTANCE_TOKEN", json);
    }

    [Fact]
    public void Game_mounts_game_content_readonly_overlay_and_tmp()
    {
        var mounts = Game(Pod(InstanceKind.Level)).VolumeMounts.Select(m => (m.Name, m.MountPath, m.ReadOnlyProperty ?? false));
        Assert.Equal(
            [("game", "/game", false), ("content", "/game/content", true), ("maps-overlay", "/game/mods/descent/maps/pool", false), ("tmp", "/tmp", false)],
            mounts);
    }

    [Fact]
    public void Volumes_are_emptydirs_and_the_content_claim_readonly()
    {
        var v = Pod(InstanceKind.Level, o => o.ContentClaim = "tf2-content").Spec.Volumes.ToDictionary(x => x.Name);
        Assert.Equal(["game", "content", "maps-overlay", "tmp"], v.Keys);
        Assert.Equal("tf2-content", v["content"].PersistentVolumeClaim.ClaimName);
        Assert.True(v["content"].PersistentVolumeClaim.ReadOnlyProperty);
        Assert.All(new[] { "game", "maps-overlay", "tmp" }, n => Assert.NotNull(v[n].EmptyDir));
    }

    [Fact]
    public void Level_and_hub_get_their_own_resources()
    {
        var level = Game(Pod(InstanceKind.Level)).Resources;
        var hub = Game(Pod(InstanceKind.Hub)).Resources;
        Assert.Equal(("1", "1536Mi"), (level.Requests["cpu"].ToString(), level.Limits["memory"].ToString()));
        Assert.Equal(("2", "3Gi"), (hub.Requests["cpu"].ToString(), hub.Limits["memory"].ToString()));
    }

    [Fact]
    public void Pod_is_one_container_the_launcher_hosts_engine_and_relay()
    {
        Assert.Equal(["game"], Pod(InstanceKind.Level).Spec.Containers.Select(c => c.Name));
        Assert.Equal(["game"], Pod(InstanceKind.Hub).Spec.Containers.Select(c => c.Name));
    }

    [Fact]
    public void Fake_tier_pod_has_the_same_single_container_shape()
    {
        var fake = Game(Pod(InstanceKind.Level, o => o.SkipModInit = true));
        var real = Game(Pod(InstanceKind.Level));
        Assert.Equal(real.Ports.Select(p => (p.Name, p.ContainerPort)), fake.Ports.Select(p => (p.Name, p.ContainerPort)));
        Assert.Equal(real.ReadinessProbe.HttpGet.Path, fake.ReadinessProbe.HttpGet.Path);
    }

    [Fact]
    public void Game_declares_the_relay_and_health_ports_and_no_udp()
    {
        Assert.Equal([("relay", 5010, "TCP"), ("health", 5012, "TCP")],
            Game(Pod(InstanceKind.Level)).Ports.Select(p => (p.Name, p.ContainerPort, p.Protocol)));
    }

    [Fact]
    public void Relay_and_health_ports_come_from_config()
    {
        var g = Game(Pod(InstanceKind.Level, o => { o.RelayPort = 6010; o.HealthPort = 6012; }));
        Assert.Equal([6010, 6012], g.Ports.Select(p => p.ContainerPort));
        Assert.Equal("6012", g.ReadinessProbe.HttpGet.Port.Value);
    }

    [Fact]
    public void Game_readiness_is_healthz_on_the_health_port()
    {
        var probe = Game(Pod(InstanceKind.Level)).ReadinessProbe;
        Assert.Equal(("/healthz", "5012"), (probe.HttpGet.Path, probe.HttpGet.Port.Value));
    }

    [Fact]
    public void Game_liveness_is_healthz_on_the_health_port()
    {
        var probe = Game(Pod(InstanceKind.Level)).LivenessProbe;
        Assert.Equal(("/healthz", "5012", 3), (probe.HttpGet.Path, probe.HttpGet.Port.Value, probe.FailureThreshold));
    }

    [Fact]
    public void Game_env_names_the_launchers_engine_dir()
    {
        Assert.Equal("/opt/srcds", Env(Game(Pod(InstanceKind.Level)), "LAUNCHER_EngineDir"));
    }

    [Fact]
    public void A_level_fetches_its_map_unless_the_fake_tier_skips_it()
    {
        Assert.Contains(Pod(InstanceKind.Level).Spec.InitContainers, c => c.Name == "fetch-level");
        Assert.DoesNotContain(Pod(InstanceKind.Level, o => o.SkipLevelFetch = true).Spec.InitContainers, c => c.Name == "fetch-level");
    }

    [Fact]
    public void Extra_env_reaches_the_game_container() =>
        Assert.Equal("true", Env(Game(Pod(InstanceKind.Level, o => o.ExtraEnv["FAKEGAME_ControlAnyAddress"] = "true")), "FAKEGAME_ControlAnyAddress"));

    [Fact]
    public void Game_pods_interpose_by_default_so_the_engine_sees_real_client_addresses() =>
        Assert.Equal("Interpose", Env(Game(Pod(InstanceKind.Level)), "RELAY_Mode"));

    [Fact]
    public void Game_env_names_the_mod_the_entrypoint_hands_to_bootstrap() =>
        Assert.Equal(new ModOptions().Name, Env(Game(Pod(InstanceKind.Hub)), "DESCENT_MOD"));

    [Fact]
    public void Relay_env_matches_the_declared_ports_and_the_engine_port()
    {
        var g = Game(Pod(InstanceKind.Level));
        Assert.Equal(("0.0.0.0:5010", "127.0.0.1:5011", "0.0.0.0:5012", "127.0.0.1:27015"),
            (Env(g, "RELAY_Listen"), Env(g, "RELAY_InfoListen"), Env(g, "RELAY_Health"), Env(g, "RELAY_EngineEndpoint")));
    }

    [Fact]
    public void Node_selector_and_tolerations_pass_through()
    {
        var p = Pod(InstanceKind.Level);
        Assert.Equal("game", p.Spec.NodeSelector["pool"]);
        var t = p.Spec.Tolerations.Single();
        Assert.Equal(("game", "Equal", "yes", "NoSchedule"), (t.Key, t.OperatorProperty, t.Value, t.Effect));
    }

    [Fact]
    public void Secret_is_owned_by_the_pod_uid_and_holds_the_token()
    {
        var spec = GamePodBuilder.Build(Options(), Mod, Row(InstanceKind.Level), Token);
        var secret = KubernetesMapping.ToSecret(spec.Secret!, new PodRef(spec.Name, "uid-1"), "descent");
        Assert.Equal(spec.Name + "-token", secret.Metadata.Name);
        var owner = secret.Metadata.OwnerReferences.Single();
        Assert.Equal(("Pod", spec.Name, "uid-1"), (owner.Kind, owner.Name, owner.Uid));
        Assert.Equal(Token, secret.StringData["token"]);
    }

    [Fact]
    public void Level_without_a_hash_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() => GamePodBuilder.Build(Options(), Mod, Row(InstanceKind.Level, hash: null), Token));
    }
}
