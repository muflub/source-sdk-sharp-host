using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Contracts;

namespace SourceSharp.Host.Modules;

/// <summary>
/// One collectible <see cref="AssemblyLoadContext"/> per module hash (plan §1.2, D-H9). The
/// entry type comes from <c>[assembly: HostRulesModule(typeof(X))]</c> and is instantiated
/// once per context. Host.Contracts always binds to the host's own copy, so the module's
/// <see cref="IGameRules"/> is the host's; private dependencies resolve from the module's folder.
/// </summary>
public sealed class RulesModuleLoader
{
    readonly ConcurrentDictionary<string, Lazy<LoadedModule>> _loaded = new(StringComparer.Ordinal);

    /// <summary>The hashes with a live load context.</summary>
    public IReadOnlyCollection<string> Loaded =>
        _loaded.Where(kv => kv.Value.IsValueCreated).Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList();

    internal IGameRules GetOrLoad(string sha256, string directory, IReadOnlyList<ModuleFile> files)
    {
        var lazy = _loaded.GetOrAdd(sha256, s => new Lazy<LoadedModule>(() => LoadedModule.Load(s, directory, files)));
        try
        {
            return lazy.Value.Rules;
        }
        catch
        {
            _loaded.TryRemove(new KeyValuePair<string, Lazy<LoadedModule>>(sha256, lazy));
            throw;
        }
    }

    /// <summary>Drops the module's context and starts its unload; callers still holding its rules keep it alive until they let go.</summary>
    public bool Unload(string sha256)
    {
        if (!_loaded.TryRemove(sha256, out var lazy)) return false;
        if (lazy.IsValueCreated) lazy.Value.Context.Unload();
        return true;
    }

    /// <summary>Unloads every context whose hash is not in <paramref name="keep"/>; returns the hashes unloaded.</summary>
    public IReadOnlyList<string> UnloadExcept(IReadOnlySet<string> keep)
    {
        var gone = new List<string>();
        foreach (var sha in _loaded.Keys.ToList())
            if (!keep.Contains(sha) && Unload(sha))
                gone.Add(sha);
        return gone;
    }

    /// <summary>For the unload fact: a weak handle on the module's context, or null when not loaded.</summary>
    internal WeakReference? ContextOf(string sha256) =>
        _loaded.TryGetValue(sha256, out var l) && l.IsValueCreated ? new WeakReference(l.Value.Context) : null;

    sealed record LoadedModule(RulesModuleLoadContext Context, IGameRules Rules)
    {
        public static LoadedModule Load(string sha256, string directory, IReadOnlyList<ModuleFile> files)
        {
            byte[]? main = null;
            foreach (var f in files)
            {
                var path = Path.Combine(directory, f.Name);
                if (!File.Exists(path))
                    throw HostRefusal.NotFound("module_missing", $"{sha256}: {f.Name} is not on disk");
                var bytes = File.ReadAllBytes(path);
                var actual = Convert.ToHexStringLower(SHA256.HashData(bytes));
                if (actual != f.Sha256)
                    throw HostRefusal.Precondition("module_corrupt", $"{sha256}: {f.Name} hashes to {actual}, recorded {f.Sha256}");
                main ??= bytes;
            }

            var context = new RulesModuleLoadContext(sha256, directory);
            try
            {
                var assembly = context.LoadFromStream(new MemoryStream(main!));
                var entry = assembly.GetCustomAttribute<HostRulesModuleAttribute>()
                    ?? throw HostRefusal.Precondition("module_invalid", $"{sha256}: {assembly.GetName().Name} carries no [HostRulesModule] of this host's Host.Contracts");
                if (!typeof(IGameRules).IsAssignableFrom(entry.RulesType) || entry.RulesType.IsAbstract)
                    throw HostRefusal.Precondition("module_invalid", $"{sha256}: {entry.RulesType.FullName} is not a concrete IGameRules");
                var rules = (IGameRules)Activator.CreateInstance(entry.RulesType)!;
                return new LoadedModule(context, rules);
            }
            catch
            {
                context.Unload();
                throw;
            }
        }
    }
}

/// <summary>
/// A module's own context. Host.Contracts is pinned to the host's copy (shared type
/// identity); any other name is looked for in the module's folder, loaded from bytes so no
/// file stays mapped; anything else (the framework) falls through to the default context.
/// </summary>
internal sealed class RulesModuleLoadContext(string sha256, string directory)
    : AssemblyLoadContext($"rules-module-{sha256[..Math.Min(12, sha256.Length)]}", isCollectible: true)
{
    protected override Assembly? Load(AssemblyName name)
    {
        if (name.Name == ModuleNames.ContractsAssembly)
            return typeof(IGameRules).Assembly;
        if (name.Name is null || !ModuleNames.IsAssemblyName(name.Name))
            return null;
        var path = Path.Combine(directory, ModuleNames.FileOf(name.Name));
        return File.Exists(path) ? LoadFromStream(new MemoryStream(File.ReadAllBytes(path))) : null;
    }
}
