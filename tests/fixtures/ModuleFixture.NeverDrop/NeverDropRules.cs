using SourceSharp.Host.Contracts;

[assembly: HostRulesModule(typeof(ModuleFixture.NeverDropRules))]

namespace ModuleFixture;

/// <summary>No kill ever drops: the other half of the fixture pair.</summary>
public sealed class NeverDropRules : FixtureRulesBase
{
    public override KillRoll KillRoll(ulong instanceSeed, uint killSeq, string robotTemplate, int depth, int partySize) =>
        new(false, -1, 0);
}
