using System.Text.RegularExpressions;

namespace SourceSharp.Host.Testing;

/// <summary>
/// CLAUDE.md "Everything is managed C#" (TF2 plan R15): no DllImport, LibraryImport,
/// extern or AllowUnsafeBlocks anywhere, and the two assemblies that ship into the game
/// (Host.Sdk, Host.Contracts) reference nothing but their allowed closure.
/// One exemption (plan_steam_auth S-2): <see cref="UnsafeAllowed"/> may set AllowUnsafeBlocks, and nothing else.
/// </summary>
public static partial class ManagedOnlyRule
{
    /// <summary>The projects allowed AllowUnsafeBlocks: the Steamworks binding, which pins buffers for the flat C API.
    /// DllImport, LibraryImport and extern stay refused there too.</summary>
    public static readonly IReadOnlySet<string> UnsafeAllowed = new HashSet<string>(StringComparer.Ordinal) { "Host.Steam" };

    [GeneratedRegex(@"\[\s*(DllImport|LibraryImport)(Attribute)?\b|\bextern\s+(?!alias\b)\w|\bAllowUnsafeBlocks\b")]
    private static partial Regex Forbidden();

    [GeneratedRegex(@"//[^\n]*|/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comments();

    /// <summary>Each forbidden construct in <paramref name="text"/>, comments ignored.</summary>
    public static IReadOnlyList<string> Violations(string text, bool allowUnsafeBlocks = false)
    {
        var code = Comments().Replace(text, " ");
        return Forbidden().Matches(code).Select(m => m.Value.Trim())
            .Where(v => !(allowUnsafeBlocks && v == "AllowUnsafeBlocks")).ToList();
    }

    [GeneratedRegex(@"<(ProjectReference|PackageReference|Reference)\s+Include=""([^""]+)""")]
    private static partial Regex References();

    /// <summary>The reference names a project file declares (project file names without extension, or package ids).</summary>
    public static IReadOnlyList<string> ReferencesOf(string csprojText) =>
        References().Matches(csprojText)
            .Select(m => m.Groups[2].Value.Replace('\\', '/').Split('/')[^1])
            .Select(n => n.EndsWith(".csproj", StringComparison.Ordinal) ? n[..^".csproj".Length] : n)
            .ToList();
}
