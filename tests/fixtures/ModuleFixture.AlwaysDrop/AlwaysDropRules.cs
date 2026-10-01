using ModuleFixture.Dep;
using SourceSharp.Host.Contracts;

[assembly: HostRulesModule(typeof(ModuleFixture.AlwaysDropRules))]

namespace ModuleFixture;

/// <summary>Every kill drops tier 0; the Australium comes from the private dependency.</summary>
public sealed class AlwaysDropRules : FixtureRulesBase
{
    public override KillRoll KillRoll(ulong instanceSeed, uint killSeq, string robotTemplate, int depth, int partySize) =>
        new(true, 0, DropBonus.For(depth));
}
