using System.Text.RegularExpressions;
using SourceSharp.Host.Testing;

namespace SourceSharp.Host.Sdk.Tests;

/// <summary>
/// §6.6 / TF2 plan R15: Host.Sdk ships into the game, which is managed-only. The SDK reads its
/// own project and sources and refuses an engine assembly and every native construct; the fake
/// game server, the SDK's proof, uses the SDK and never the proto directly.
/// </summary>
public partial class ManagedOnlySdkFacts
{
    static readonly string[] Allowed = ["Host.Proto", "Host.Contracts", "Grpc.Net.Client", "Google.Protobuf"];
    // Split so Host.Tests' repository-wide scan does not see this file's test input as a violation.
    const string Unsafe = "Allow" + "UnsafeBlocks";

    [GeneratedRegex(@"\bSourceSharp\.Abi\b")]
    private static partial Regex EngineAssembly();

    static string Csproj(string project) => RepoFiles.Path_("src", project, project + ".csproj");

    [Fact]
    public void The_sdk_references_only_the_proto_the_contracts_and_the_grpc_client()
    {
        var refs = ManagedOnlyRule.ReferencesOf(File.ReadAllText(Csproj("Host.Sdk")));
        Assert.NotEmpty(refs); // the reader saw the file's references at all
        Assert.Empty(refs.Except(Allowed));
    }

    [Fact]
    public void The_sdk_project_and_sources_carry_no_native_construct_and_no_engine_assembly()
    {
        var files = RepoFiles.Sources("Host.Sdk").Append(Csproj("Host.Sdk")).ToList();
        Assert.True(files.Count >= 10, $"the scan found only {files.Count} files");
        var found = new List<string>();
        foreach (var f in files)
        {
            var text = File.ReadAllText(f);
            Assert.False(string.IsNullOrWhiteSpace(text), $"{f} is empty");
            found.AddRange(ManagedOnlyRule.Violations(text).Select(v => $"{Path.GetFileName(f)}: {v}"));
            if (EngineAssembly().IsMatch(text)) found.Add($"{Path.GetFileName(f)}: SourceSharp.Abi");
        }
        Assert.Empty(found);
    }

    [Fact]
    public void The_scan_would_see_a_planted_violation()
    {
        // The same checks, on the SDK's real csproj with one line added: a check that cannot fail is not evidence.
        var planted = File.ReadAllText(Csproj("Host.Sdk"))
            .Replace("</Project>", $"  <PropertyGroup><{Unsafe}>true</{Unsafe}></PropertyGroup>\n  <ItemGroup><ProjectReference Include=\"../../sharp/src/SourceSharp.Abi/SourceSharp.Abi.csproj\" /></ItemGroup>\n</Project>");
        Assert.NotEmpty(ManagedOnlyRule.Violations(planted));
        Assert.Contains("SourceSharp.Abi", ManagedOnlyRule.ReferencesOf(planted).Except(Allowed));
        Assert.Matches(EngineAssembly(), planted);
    }

    [Fact]
    public void The_fake_game_server_uses_the_sdk_and_not_the_proto()
    {
        var refs = ManagedOnlyRule.ReferencesOf(File.ReadAllText(Csproj("Host.FakeGame")));
        Assert.Contains("Host.Sdk", refs);
        Assert.DoesNotContain("Host.Proto", refs);
        var usesProto = RepoFiles.Sources("Host.FakeGame").Where(f => File.ReadAllText(f).Contains("SourceSharp.Host.Proto")).ToList();
        Assert.Empty(usesProto);
    }
}
