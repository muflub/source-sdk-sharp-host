using System.Text.Json.Nodes;
using Descent.MapForge;
using SourceSharp.Host.Abstractions;

namespace Descent.MapForge.Tests;

/// <summary>
/// §9.1 <c>mapforge lint</c>: the tools' lint, then the game's rules, each with a refusing
/// fixture. Every refusal first shows the unedited library passes (the accepted counterpart).
/// </summary>
public sealed class LintFacts
{
    const int Depths = 15;

    static async Task<IReadOnlyList<LintFinding>> Lint(Func<JsonObject, JsonObject>? manifest = null, Func<string, string>? vmf = null,
        bool content = false)
    {
        var json = manifest is null ? Samples.ManifestJson : manifest(JsonNode.Parse(Samples.ManifestJson)!.AsObject()).ToJsonString();
        var lib = Samples.CopyLibrary(json, vmf);
        return await MapForgeLint.LintSourceAsync(lib.Vmf, json, Samples.Mod, Depths, content ? lib.Directory : null);
    }

    static JsonObject Room(JsonObject m, string room) => m["rooms"]![room]!.AsObject();

    static async Task AcceptedThenRefused(string rule, Func<JsonObject, JsonObject>? manifest = null, Func<string, string>? vmf = null)
    {
        Assert.Empty(await Lint());
        var findings = await Lint(manifest, vmf);
        Assert.Contains(findings, f => f.Rule == rule);
    }

    [Fact]
    public async Task The_sample_library_with_its_manifest_passes_every_rule() => Assert.Empty(await Lint());

    [Fact]
    public async Task With_content_the_tools_model_check_runs_on_every_room_and_passes() => Assert.Empty(await Lint(content: true));

    [Fact]
    public async Task The_tools_refuse_a_library_with_two_rooms_of_one_name() =>
        await AcceptedThenRefused(MapForgeLint.Tools, vmf: v => v.Replace("\"name\" \"tee\"", "\"name\" \"cross\""));

    [Fact]
    public async Task A_missing_manifest_is_refused()
    {
        Assert.Empty(await Lint());
        var lib = Samples.CopyLibrary(manifest: null);
        var findings = await MapForgeLint.LintSourceAsync(lib.Vmf, null, Samples.Mod, Depths);
        Assert.Contains(findings, f => f.Rule == MapForgeLint.Manifest);
    }

    [Fact]
    public async Task A_manifest_that_is_not_json_is_refused()
    {
        Assert.Empty(await Lint());
        var lib = Samples.CopyLibrary();
        var findings = await MapForgeLint.LintSourceAsync(lib.Vmf, "{ not json", Samples.Mod, Depths);
        Assert.Contains(findings, f => f.Rule == MapForgeLint.Manifest);
    }

    [Fact]
    public async Task A_manifest_of_another_mod_is_refused() =>
        await AcceptedThenRefused(MapForgeLint.Mod, m => { m["mod"] = "othermod"; return m; });

    [Fact]
    public async Task An_unknown_tag_is_refused() =>
        await AcceptedThenRefused(MapForgeLint.UnknownTag, m => { Room(m, "hall")["tags"]!.AsArray().Add("lava"); return m; });

    [Fact]
    public async Task A_library_room_without_a_manifest_entry_is_refused() =>
        await AcceptedThenRefused(MapForgeLint.UnlistedRoom, m => { m["rooms"]!.AsObject().Remove("hall"); return m; });

    [Fact]
    public async Task A_manifest_entry_for_a_room_the_library_lacks_is_refused() =>
        await AcceptedThenRefused(MapForgeLint.UnknownRoom, m => { m["rooms"]!.AsObject()["ghost"] = JsonNode.Parse("""{ "tags": ["corridor"] }"""); return m; });

    [Fact]
    public async Task An_inverted_depth_range_is_refused() =>
        await AcceptedThenRefused(MapForgeLint.DepthRange, m => { Room(m, "hall")["depths"] = new JsonArray(9, 3); return m; });

    [Fact]
    public async Task A_weight_below_one_is_refused() =>
        await AcceptedThenRefused(MapForgeLint.DepthRange, m => { Room(m, "hall")["weight"] = 0; return m; });

    [Fact]
    public async Task A_served_depth_without_stairs_is_refused() =>
        await AcceptedThenRefused(MapForgeLint.TagCoverage, m => { Room(m, "corner")["depths"] = new JsonArray(1, 4); return m; });

    [Fact]
    public async Task A_boss_depth_without_a_boss_arena_is_refused()
    {
        Assert.Empty(await Lint());
        var findings = await Lint(m => { Room(m, "cross")["tags"] = new JsonArray("junction"); return m; });
        Assert.Contains(findings, f => f.Rule == MapForgeLint.TagCoverage && f.Message.Contains("depth 5 has no room tagged 'boss_arena'"));
    }

    [Fact]
    public async Task A_marker_the_room_does_not_hold_is_refused() =>
        await AcceptedThenRefused(MapForgeLint.Marker, m => { Room(m, "cross")["markers"]!.AsArray().Add("robot_spawn"); return m; });

    [Fact]
    public void In_a_library_with_role_rooms_start_must_be_the_up_room()
    {
        var markers = new HashSet<string>();
        RoomFacts[] rooms = [new("a", "up", markers), new("b", "down", markers), new("c", null, markers)];
        string Manifest(string aTags) => $$"""
            { "mod": "descent", "rooms": {
              "a": { "tags": [{{aTags}}] }, "b": { "tags": ["stairs"] }, "c": { "tags": ["corridor", "boss_arena"] } } }
            """;
        Assert.DoesNotContain(MapForgeLint.GameRules(Manifest("\"start\""), rooms, "descent", 15), f => f.Rule == MapForgeLint.Role);
        Assert.Contains(MapForgeLint.GameRules(Manifest("\"corridor\""), rooms, "descent", 15), f => f.Rule == MapForgeLint.Role);
    }

    [Fact]
    public void The_report_names_what_lint_does_not_check()
    {
        var report = JsonNode.Parse(MapForgeLint.Report([]))!;
        Assert.Contains("R19", report["notChecked"]!.GetValue<string>());
        Assert.True(report["ok"]!.GetValue<bool>());
    }

    [Fact]
    public void Library_version_changes_with_rooms_vmf()
    {
        var a = Samples.CopyLibrary();
        var b = Samples.CopyLibrary(editVmf: v => v + "\n");
        Assert.Equal(MapIdentity.LibraryVersionOf(a), MapIdentity.LibraryVersionOf(Samples.CopyLibrary()));
        Assert.NotEqual(MapIdentity.LibraryVersionOf(a), MapIdentity.LibraryVersionOf(b));
    }

    [Fact]
    public void Library_version_changes_with_library_json()
    {
        var a = Samples.CopyLibrary();
        var b = Samples.CopyLibrary(Samples.ManifestJson.Replace("\"weight\": 3", "\"weight\": 4"));
        Assert.NotEqual(MapIdentity.LibraryVersionOf(a), MapIdentity.LibraryVersionOf(b));
    }
}
