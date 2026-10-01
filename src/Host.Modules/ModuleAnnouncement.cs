using System.Text.Json;
using System.Text.RegularExpressions;
using SourceSharp.Host.Contracts;

namespace SourceSharp.Host.Modules;

/// <summary>A private dependency the module declares: its assembly name and the sha256 of its bytes.</summary>
public sealed record ModuleDependencyInfo(string Assembly, string Sha256);

/// <summary>
/// What the SDK announces in <c>Booting</c> (proto <c>RulesModule</c>, D-H9): the module's
/// identity only. <see cref="Sha256"/> is the lowercase hex hash of the main assembly's bytes.
/// </summary>
public sealed record ModuleAnnouncement(
    string Assembly, string Version, string Sha256, string ContractVersion, IReadOnlyList<ModuleDependencyInfo> Deps);

public enum RulesModuleAnswerKind { Known, Send, Pending, Refused }

/// <summary>
/// The host's answer to an announcement or an upload. Known / Send / Pending map onto the
/// proto's <c>ModuleAnswer</c>; Refused carries a reason code (proto/common.proto style) and text.
/// </summary>
public sealed record RulesModuleAnswer(RulesModuleAnswerKind Kind, string? Reason = null, string? Message = null)
{
    public static readonly RulesModuleAnswer Known = new(RulesModuleAnswerKind.Known);
    public static readonly RulesModuleAnswer Send = new(RulesModuleAnswerKind.Send);
    public static readonly RulesModuleAnswer Pending = new(RulesModuleAnswerKind.Pending);
    public static RulesModuleAnswer Refused(string reason, string message) => new(RulesModuleAnswerKind.Refused, reason, message);
}

/// <summary>One file of a module: its file name in the module's folder and its expected hash.</summary>
internal sealed record ModuleFile(string Name, string Sha256);

internal static partial class ModuleNames
{
    public static string ContractsAssembly { get; } = typeof(IGameRules).Assembly.GetName().Name!;

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha();

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.\-]{0,199}$")]
    private static partial Regex SimpleName();

    public static string Normalize(string? sha256) => (sha256 ?? "").Trim().ToLowerInvariant();
    public static bool IsSha(string sha256) => Sha().IsMatch(sha256);
    public static bool IsAssemblyName(string name) =>
        SimpleName().IsMatch(name) && !name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !name.Contains("..", StringComparison.Ordinal);

    public static string FileOf(string assembly) => assembly + ".dll";

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The deps as rules_modules.deps_json stores them: sorted by assembly name, hashes lowercase.</summary>
    public static string DepsJson(IEnumerable<ModuleDependencyInfo> deps) =>
        JsonSerializer.Serialize(deps.Select(d => new ModuleDependencyInfo(d.Assembly, Normalize(d.Sha256)))
            .OrderBy(d => d.Assembly, StringComparer.Ordinal).ToList(), Json);

    public static IReadOnlyList<ModuleDependencyInfo> ParseDeps(string depsJson) =>
        string.IsNullOrWhiteSpace(depsJson) ? [] : JsonSerializer.Deserialize<List<ModuleDependencyInfo>>(depsJson, Json) ?? [];

    public static IReadOnlyList<ModuleFile> Files(string assembly, string sha256, IEnumerable<ModuleDependencyInfo> deps) =>
        [new(FileOf(assembly), Normalize(sha256)), .. deps.Select(d => new ModuleFile(FileOf(d.Assembly), Normalize(d.Sha256)))];
}
