namespace ModuleFixture.Dep;

/// <summary>
/// A private dependency of ModuleFixture.AlwaysDrop: the host must resolve it from the
/// module's own folder (plan §1.2), never from its own directory.
/// </summary>
public static class DropBonus
{
    public const long PerDepth = 7;
    public static long For(int depth) => PerDepth * depth;
}
