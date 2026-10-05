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
                found.AddRange(ManagedOnlyRule.Violations(File.ReadAllText(f), ManagedOnlyRule.UnsafeAllowed.Contains(p)).Select(v => $"{Path.GetRelativePath(RepoFiles.Root, f)}: {v}"));
            }
        }
        Assert.True(scanned > projects.Count, "the scan read no source files");
        Assert.Empty(found);
    }

    [Fact]
    public void The_unsafe_exemption_is_exactly_the_steam_binding() =>
        Assert.Equal(["Host.Steam"], ManagedOnlyRule.UnsafeAllowed.Order());

    [Fact]
    public void An_exempt_project_may_allow_unsafe_blocks_and_nothing_else()
    {
        const string csproj = "<PropertyGroup><AllowUnsafeBlocks>true</AllowUnsafeBlocks></PropertyGroup>";
        Assert.NotEmpty(ManagedOnlyRule.Violations(csproj));                       // refused without the exemption
        Assert.Empty(ManagedOnlyRule.Violations(csproj, allowUnsafeBlocks: true)); // accepted with it
        Assert.NotEmpty(ManagedOnlyRule.Violations("class A { [DllImport(\"x\")] static extern int F(); }", allowUnsafeBlocks: true));
    }

    [Fact]
    public void Every_unsafe_exemption_is_in_use()
    {
        // A stale exemption would let a later project slip through under an old name.
        foreach (var p in ManagedOnlyRule.UnsafeAllowed)
        {
            var csproj = File.ReadAllText(RepoFiles.Path_("src", p, p + ".csproj"));
            Assert.Contains("AllowUnsafeBlocks", ManagedOnlyRule.Violations(csproj));
        }
    }

    /// <summary>Every project <paramref name="project"/> reaches through ProjectReference, transitively.</summary>
    static HashSet<string> ProjectClosure(string project)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var todo = new Stack<string>([project]);
        while (todo.TryPop(out var p))
        {
            var csproj = File.ReadAllText(RepoFiles.Path_("src", p, p + ".csproj"));
            foreach (var r in ManagedOnlyRule.ReferencesOf(csproj).Where(r => RepoFiles.Projects().Contains(r)))
                if (seen.Add(r)) todo.Push(r);
        }
        return seen;
    }

    [Fact]
    public void Project_closure_follows_references_transitively()
    {
        // The walker must see past the first hop, or the refusal below proves nothing.
        // Host.Admin reaches Host.Contracts only through another project.
        Assert.Contains("Host.Contracts", ProjectClosure("Host.Admin"));
        Assert.DoesNotContain("Host.Contracts", ManagedOnlyRule.ReferencesOf(File.ReadAllText(RepoFiles.Path_("src", "Host.Admin", "Host.Admin.csproj"))));
    }

    [Theory]
    [InlineData("Host.Sdk")]
    [InlineData("Host.Contracts")]
    [InlineData("Host.Launcher")]
    [InlineData("Host.FakeGame")]
    public void Nothing_that_ships_into_a_game_pod_reaches_the_steam_binding(string project) =>
        Assert.DoesNotContain("Host.Steam", ProjectClosure(project));

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
