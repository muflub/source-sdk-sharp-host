namespace SourceSharp.Host.Abstractions;

/// <summary>
/// How the pool lays levels out (plan §9.1 Layout, D-H7). Bound by the composition root
/// (section <c>MapPoolLayout</c>); kept apart from <see cref="MapPoolOptions"/> because it goes
/// away once the rules module's layout (TF2 plan 6b) is the only one.
/// </summary>
public sealed class MapPoolLayoutOptions
{
    /// <summary>
    /// Until 6b exists, the tools' own seeded generator lays out every level and the rules
    /// module's <c>GenerateLayout</c> is not called. On by default while no real rules module
    /// provides layouts (FakeGameRules' layout ignores sockets and the linker refuses it).
    /// </summary>
    public bool ToolLayout { get; set; } = true;
    public int Rows { get; set; } = 3;
    public int Columns { get; set; } = 3;
    /// <summary>The pool key's difficulty (Q17); one difficulty in v1.</summary>
    public int Difficulty { get; set; }
    /// <summary>Write up_map / down_map (the library has role rooms).</summary>
    public bool Transitions { get; set; } = true;
}
