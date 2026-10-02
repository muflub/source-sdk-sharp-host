using System.Text.Json;
using SourceSharp.Host.Abstractions;

namespace SourceSharp.Host.Sdk.Tests;

/// <summary>Booting reports what the server runs: its non-framework assemblies and the modules the game declares.</summary>
public class LoadedModulesFacts
{
    [Fact]
    public void The_report_has_the_sdk_and_no_framework_assembly()
    {
        var loaded = LoadedModules.Collect();
        var sdk = Assert.Single(loaded, m => m.Name == "SourceSharp.Host.Sdk");
        Assert.StartsWith(HostSdk.Version, sdk.Version);
        Assert.Equal("assembly", sdk.Kind);
        Assert.DoesNotContain(loaded, m => m.Name == "System.Private.CoreLib");
    }

    [Fact]
    public void An_assembly_loaded_from_bytes_into_a_collectible_context_is_reported()
    {
        // SourceSharp runs the game in a collectible per-side AssemblyLoadContext, loaded from
        // bytes: such an assembly has no Location. Measured on a hosted boot: none was reported.
        var alc = new System.Runtime.Loader.AssemblyLoadContext("side-server", isCollectible: true);
        try
        {
            using var bytes = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fractions.dll"));
            var side = alc.LoadFromStream(bytes);
            Assert.Equal("", side.Location); // the stimulus: no file behind it
            var loaded = LoadedModules.Collect([side, typeof(object).Assembly], null);
            Assert.Equal(["Fractions"], loaded.Select(m => m.Name)); // and still no framework assembly
        }
        finally { alc.Unload(); }
    }

    [Fact]
    public void Declared_modules_follow_the_assemblies()
    {
        var loaded = LoadedModules.Collect(() => [new LoadedModuleInfo("tf2_descent", "31347c3", "mod")]);
        Assert.Equal(new LoadedModuleInfo("tf2_descent", "31347c3", "mod"), loaded[^1]);
    }

    [Fact]
    public void A_declaration_that_throws_is_reported_not_raised()
    {
        var loaded = LoadedModules.Collect(() => throw new InvalidOperationException("mods.yaml unreadable"));
        Assert.Contains(loaded, m => m.Kind == "error" && m.Version.Contains("mods.yaml unreadable"));
    }

    [Fact]
    public async Task The_service_records_what_the_server_reported_at_booting()
    {
        await using var h = await SdkHarness.Start();
        await h.Instance("lvl-1", InstanceKind.Level, 3, InstanceState.Booting);
        var o = SdkHarness.FastOptions();
        o.ModuleSource = new FixedModuleSource(SessionFacts.ModuleOnDisk().Module);
        o.DeclaredModules = () => [new LoadedModuleInfo("tf2_descent", "31347c3", "mod")];
        var sdk = h.Sdk("lvl-1", o);
        var boot = await sdk.Pumped(sdk.Session.Booting("tf2_descent-3-abc"));
        Assert.True(boot.Ok, boot.ToString());

        var row = Assert.Single(await h.Data.ReadAsync((tx, _) => tx.Audit.List(new AuditQuery(Action: "instance.modules", Target: "lvl-1"))));
        using var doc = JsonDocument.Parse(row.AfterJson!);
        Assert.Equal(HostSdk.Version, doc.RootElement.GetProperty("sdk").GetString());
        var loaded = doc.RootElement.GetProperty("loaded").EnumerateArray()
            .Select(m => (m.GetProperty("Name").GetString(), m.GetProperty("Kind").GetString())).ToList();
        Assert.Contains(("SourceSharp.Host.Sdk", "assembly"), loaded);
        Assert.Contains(("tf2_descent", "mod"), loaded);
    }
}
