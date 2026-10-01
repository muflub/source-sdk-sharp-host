using SourceSharp.Host.Contracts;

namespace SourceSharp.Host.Abstractions;

/// <summary>
/// Which rules module applies (D-H9): per instance, the module that instance's pod
/// announced; for the pool and the vendor roll, the module of the current mod image.
/// Host.Modules implements it; facts use a provider over FakeGameRules.
/// </summary>
public interface IRulesProvider
{
    /// <summary>The module of the current mod image, or null until one is known ("waiting for the hub's rules module").</summary>
    IGameRules? Current { get; }
    string? CurrentSha256 { get; }

    /// <summary>The module with this hash; throws HostRefusal unknown_module / quarantined.</summary>
    IGameRules For(string sha256);
}

/// <summary>One module for everything: the unit tier's provider.</summary>
public sealed class SingleRulesProvider(IGameRules rules, string sha256 = "fake") : IRulesProvider
{
    public IGameRules? Current => rules;
    public string? CurrentSha256 => sha256;
    public IGameRules For(string sha) => rules;
}
