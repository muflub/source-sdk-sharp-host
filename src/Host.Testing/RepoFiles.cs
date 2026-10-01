namespace SourceSharp.Host.Testing;

/// <summary>Finds this repository's files from a test's own location, never from an absolute path.</summary>
public static class RepoFiles
{
    public static string Root { get; } = FindRoot();

    static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Host.slnx")))
                return dir.FullName;
        throw new InvalidOperationException($"Host.slnx not found above {AppContext.BaseDirectory}");
    }

    public static string Path_(params string[] parts) => Path.Combine([Root, .. parts]);

    /// <summary>Every project directory under src/, by name.</summary>
    public static IReadOnlyList<string> Projects() =>
        Directory.GetDirectories(Path.Combine(Root, "src"))
            .Where(d => File.Exists(Path.Combine(d, Path.GetFileName(d) + ".csproj")))
            .Select(Path.GetFileName).Cast<string>().Order().ToList();

    /// <summary>A project's own .cs files, never its build output.</summary>
    public static IEnumerable<string> Sources(string project) =>
        Directory.EnumerateFiles(Path.Combine(Root, "src", project), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));
}
