using System.Collections.Concurrent;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Contracts;

namespace SourceSharp.Host.Modules;

/// <summary>
/// Which rules module applies (D-H9): per instance, the module with that instance's hash,
/// loaded on demand from the module folder; for the pool and the vendor roll, the module of
/// the current mod image (<c>Instances.ModImage</c>), learned from the last instance that
/// booted from it. Null until one is known: "waiting for the hub's rules module".
/// </summary>
public sealed class RulesProvider : IRulesProvider
{
    readonly RulesModuleRegistry _registry;
    readonly Func<string> _currentModImage;
    readonly Func<Task<IReadOnlySet<string>>> _referencedHashes;
    readonly ConcurrentDictionary<string, string> _byModImage = new(StringComparer.Ordinal);

    /// <param name="currentModImage">The configured mod image as instance rows record it (<c>ImageRef.ToString()</c>), read on every call so a runtime change applies.</param>
    /// <param name="referencedHashes">The hashes a live instance, a ready level or an assignment refers to (supplied by the service).</param>
    public RulesProvider(RulesModuleRegistry registry, Func<string> currentModImage, Func<Task<IReadOnlySet<string>>> referencedHashes)
    {
        _registry = registry;
        _currentModImage = currentModImage;
        _referencedHashes = referencedHashes;
    }

    public string? CurrentSha256 =>
        _byModImage.TryGetValue(_currentModImage(), out var sha) && _registry.Find(sha)?.State == ModuleState.Approved ? sha : null;

    public IGameRules? Current => CurrentSha256 is { } sha ? For(sha) : null;

    /// <summary>
    /// The module with this hash. Throws <see cref="HostRefusal"/>: <c>unknown_module</c> (never
    /// recorded), <c>quarantined</c>, <c>module_pending</c> (awaiting approval),
    /// <c>module_missing</c> / <c>module_corrupt</c> (the files on disk are gone or altered).
    /// </summary>
    public IGameRules For(string sha256)
    {
        var sha = ModuleNames.Normalize(sha256);
        var row = (ModuleNames.IsSha(sha) ? _registry.Find(sha) : null)
            ?? throw HostRefusal.NotFound("unknown_module", $"no rules module {sha256}");
        return row.State switch
        {
            ModuleState.Quarantined => throw HostRefusal.Denied("quarantined", $"rules module {sha} is quarantined"),
            ModuleState.Pending => throw HostRefusal.Precondition("module_pending", $"rules module {sha} awaits approval"),
            _ => _registry.Loader.GetOrLoad(sha, _registry.DirectoryOf(sha), _registry.FilesOf(row)),
        };
    }

    /// <summary>An instance booted from <paramref name="modImage"/> running module <paramref name="sha256"/>: the lead calls it from Booting.</summary>
    public void NoteBooted(string instanceId, string? modImage, string? sha256)
    {
        if (string.IsNullOrEmpty(modImage) || string.IsNullOrEmpty(sha256)) return;
        _byModImage[modImage] = ModuleNames.Normalize(sha256);
    }

    /// <summary>After a service restart: relearns the image → module map from the booted, non-terminal instance rows, oldest first.</summary>
    public async Task RestoreAsync(IHostData data, CancellationToken ct = default)
    {
        var rows = await data.ReadAsync((tx, _) => tx.Instances.NonTerminal(), ct);
        foreach (var r in rows.Where(r => r.Booted is not null).OrderBy(r => r.Booted))
            NoteBooted(r.Id, r.ModImage, r.RulesSha256);
    }

    /// <summary>
    /// Unloads every context no live instance, ready level or assignment refers to, keeping
    /// the current mod image's module. Returns the hashes unloaded.
    /// </summary>
    public async Task<IReadOnlyList<string>> SweepAsync()
    {
        var keep = new HashSet<string>((await _referencedHashes()).Select(ModuleNames.Normalize), StringComparer.Ordinal);
        if (_byModImage.TryGetValue(_currentModImage(), out var current)) keep.Add(current);
        return _registry.Loader.UnloadExcept(keep);
    }
}
