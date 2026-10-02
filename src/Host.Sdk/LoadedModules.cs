using System.Reflection;
using System.Runtime.InteropServices;

namespace SourceSharp.Host.Sdk;

/// <summary>One thing the server runs, as Booting reports it: an assembly, or a module the game declares.</summary>
public sealed record LoadedModuleInfo(string Name, string Version, string Kind);

/// <summary>
/// What a server reports at Booting: every non-framework assembly loaded in its process, in every
/// load context (the .NET runtime's own directory is left out), then what the game declares. Bounded, so a
/// runaway report cannot swell a boot.
/// </summary>
public static class LoadedModules
{
    public const int MaxModules = 512, MaxText = 200;

    public static IReadOnlyList<LoadedModuleInfo> Collect(Func<IEnumerable<LoadedModuleInfo>>? declared = null) =>
        Collect(AppDomain.CurrentDomain.GetAssemblies(), declared);

    /// <summary>The report over <paramref name="loaded"/> (facts pass their own; the SDK passes the process's, every load context).</summary>
    internal static IReadOnlyList<LoadedModuleInfo> Collect(IEnumerable<Assembly> loaded, Func<IEnumerable<LoadedModuleInfo>>? declared)
    {
        var runtime = Path.GetFullPath(RuntimeEnvironment.GetRuntimeDirectory());
        var assemblies = loaded
            // Only the runtime's own files are left out: an assembly loaded from bytes (a collectible
            // per-side context, as SourceSharp runs the game) has no Location and is reported.
            .Where(a => !a.IsDynamic && !(a.Location.Length > 0 && Path.GetFullPath(a.Location).StartsWith(runtime, StringComparison.Ordinal)))
            .Select(a => new LoadedModuleInfo(a.GetName().Name ?? "?", VersionOf(a), "assembly"));
        IEnumerable<LoadedModuleInfo> extra;
        try { extra = declared?.Invoke().ToList() ?? []; }
        catch (Exception e) when (e is not OutOfMemoryException) { extra = [new LoadedModuleInfo("declared_modules_failed", Cut(e.Message), "error")]; }
        return assemblies.OrderBy(m => m.Name, StringComparer.Ordinal)
            .Concat(extra)
            .Select(m => new LoadedModuleInfo(Cut(m.Name), Cut(m.Version), Cut(m.Kind)))
            .DistinctBy(m => (m.Kind, m.Name))
            .Take(MaxModules)
            .ToList();
    }

    static string VersionOf(Assembly a) =>
        a.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? a.GetName().Version?.ToString() ?? "";

    static string Cut(string s) => s.Length <= MaxText ? s : s[..MaxText];
}
