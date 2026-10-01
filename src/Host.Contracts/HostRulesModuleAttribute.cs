namespace SourceSharp.Host.Contracts;

/// <summary>
/// Names the <see cref="IGameRules"/> implementation of a rules module:
/// <c>[assembly: HostRulesModule(typeof(DescentRules))]</c>. The SDK finds the assembly
/// that carries it, hashes it and announces it at boot (D-H9); the host instantiates the
/// named type in the module's own load context.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class HostRulesModuleAttribute(Type rulesType) : Attribute
{
    public Type RulesType { get; } = rulesType;
}

/// <summary>This contract's own version (semver), announced by the SDK and checked by the host.</summary>
public static class HostContract
{
    public const string Version = "1.0.0";

    /// <summary>
    /// Whether a module built against <paramref name="moduleContract"/> can be loaded by a host
    /// on <see cref="Version"/>: same major, module minor not newer than the host's.
    /// </summary>
    public static bool Supports(string moduleContract)
    {
        if (!System.Version.TryParse(moduleContract, out var m) || !System.Version.TryParse(Version, out var h))
            return false;
        return m.Major == h.Major && m.Minor <= h.Minor;
    }
}
