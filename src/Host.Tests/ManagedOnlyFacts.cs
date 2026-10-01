using SourceSharp.Host.Testing;

namespace SourceSharp.Host.Tests;

public class ManagedOnlyFacts
{
    // The refusals first: a check that cannot fail is not evidence.
    [Fact]
    public void Clean_code_has_no_violations() =>
        Assert.Empty(ManagedOnlyRule.Violations("class A { static int F() => 1; } // [DllImport(\"x\")] in a comment"));

    [Fact]
    public void DllImport_is_refused() =>
        Assert.Single(ManagedOnlyRule.Violations("class A { [DllImport(\"libc\")] static extern int getpid(); }"), v => v.Contains("DllImport"));

    [Fact]
    public void LibraryImport_is_refused() =>
        Assert.NotEmpty(ManagedOnlyRule.Violations("partial class A { [LibraryImport(\"libc\")] private static partial int getpid(); }"));

    [Fact]
    public void Extern_method_is_refused() =>
        Assert.NotEmpty(ManagedOnlyRule.Violations("class A { static extern void F(); }"));

    [Fact]
    public void Extern_alias_is_allowed() =>
        Assert.Empty(ManagedOnlyRule.Violations("extern alias Foo; class A {}"));

    [Fact]
    public void AllowUnsafeBlocks_is_refused() =>
        Assert.NotEmpty(ManagedOnlyRule.Violations("<PropertyGroup><AllowUnsafeBlocks>true</AllowUnsafeBlocks></PropertyGroup>"));

    [Fact]
    public void Every_project_file_and_source_is_managed_only()
    {
        var projects = RepoFiles.Projects();
        Assert.True(projects.Count >= 20, $"expected the plan's projects, found {projects.Count}");
        var scanned = 0;
        var found = new List<string>();
        foreach (var p in projects)
        {
            var files = RepoFiles.Sources(p).Append(RepoFiles.Path_("src", p, p + ".csproj"));
            foreach (var f in files)
            {
                scanned++;
                // This file names the forbidden constructs as test inputs; the rule's own source names them in its pattern.
                if (f.EndsWith("ManagedOnlyFacts.cs") || f.EndsWith("ManagedOnlyRule.cs")) continue;
                found.AddRange(ManagedOnlyRule.Violations(File.ReadAllText(f)).Select(v => $"{Path.GetRelativePath(RepoFiles.Root, f)}: {v}"));
            }
        }
        Assert.True(scanned > projects.Count, "the scan read no source files");
        Assert.Empty(found);
    }

    [Theory]
    [InlineData("Host.Contracts", new string[0])]
    [InlineData("Host.Sdk", new[] { "Host.Proto", "Host.Contracts", "Grpc.Net.Client", "Google.Protobuf" })]
    [InlineData("Host.Proto", new[] { "Google.Protobuf", "Grpc.Net.Client", "Grpc.Tools" })]
    public void Assemblies_that_ship_into_the_game_reference_only_their_closure(string project, string[] allowed)
    {
        var csproj = File.ReadAllText(RepoFiles.Path_("src", project, project + ".csproj"));
        var extra = ManagedOnlyRule.ReferencesOf(csproj).Except(allowed).ToList();
        Assert.Empty(extra);
    }

    [Fact]
    public void Reference_reader_sees_an_engine_reference()
    {
        var refs = ManagedOnlyRule.ReferencesOf("<ItemGroup><ProjectReference Include=\"..\\..\\sharp\\src\\SourceSharp.Abi\\SourceSharp.Abi.csproj\" /></ItemGroup>");
        Assert.Equal(["SourceSharp.Abi"], refs);
    }

    [Fact]
    public void Reference_reader_keeps_a_dotted_package_id_whole() =>
        Assert.Equal(["Grpc.Net.Client"], ManagedOnlyRule.ReferencesOf("<PackageReference Include=\"Grpc.Net.Client\" />"));
}
