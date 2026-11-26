using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Contracts;
using SourceSharp.Host.Testing;

namespace SourceSharp.Host.Admin.Tests;

/// <summary>
/// Contract 1.3 (descent X4): the admin character edit asks <see cref="IGameRules.ValidateEdit"/>,
/// so rules whose checkpoint earns nothing without evidence can still let an operator raise XP or level.
/// </summary>
public sealed class EditValidationFacts
{
    /// <summary>A checkpoint raises XP or level only with play evidence, as Descent's RU13 does. No ValidateEdit.</summary>
    internal class EvidenceRules(FakeGameRules f) : Contract10Rules(f), IGameRules
    {
        ValidationResult IGameRules.ValidateCheckpoint(CharacterSheet before, CharacterSheet after, CheckpointEvidence evidence)
        {
            var (a, b) = (FakeGameRules.SheetCodec.Read(before), FakeGameRules.SheetCodec.Read(after));
            if (evidence.Data.Length == 0 && (b.Xp > a.Xp || b.Level > a.Level)) return ValidationResult.Fail("no_evidence");
            return Fake.ValidateCheckpoint(before, after, evidence);
        }
    }

    /// <summary>The same checkpoint, and an edit that may raise anything but never change the class.</summary>
    sealed class EditAwareRules(FakeGameRules f) : EvidenceRules(f), IGameRules
    {
        ValidationResult IGameRules.ValidateEdit(CharacterSheet before, CharacterSheet after) =>
            FakeGameRules.SheetCodec.Read(before).ClassName == FakeGameRules.SheetCodec.Read(after).ClassName
                ? ValidationResult.Pass : ValidationResult.Fail("edit_class");
    }

    static string Sheet(int level, long xp, string cls = "scout") =>
        System.Text.Json.JsonSerializer.Serialize(new FakeGameRules.Sheet(cls, "Ann", level, xp, 0, false, false));

    [Fact]
    public async Task Rules_without_ValidateEdit_check_an_edit_as_a_checkpoint_with_no_evidence()
    {
        await using var w = new AdminWorld(f => new EvidenceRules(f));
        var c = await w.Character(level: 10);
        var r = await w.Actions.EditCharacter(c.Id, c.Version, Sheet(level: 12, xp: 500));
        Assert.Equal((AdminOutcome.Refused, "rule:no_evidence"), (r.Outcome, r.Reason));
        Assert.Equal(10, (await w.Get(c.Id))!.Level);
    }

    [Fact]
    public async Task An_edit_asks_ValidateEdit_so_the_rules_can_let_it_raise_xp_and_level()
    {
        await using var w = new AdminWorld(f => new EditAwareRules(f));
        var c = await w.Character(level: 10);
        var r = await w.Actions.EditCharacter(c.Id, c.Version, Sheet(level: 12, xp: 500));
        Assert.True(r.Ok, r.Message);
        var after = (await w.Get(c.Id))!;
        Assert.Equal((12, 500L), (after.Level, after.Xp));
    }

    [Fact]
    public async Task A_ValidateEdit_refusal_refuses_the_edit_with_the_rule_named()
    {
        await using var w = new AdminWorld(f => new EditAwareRules(f));
        var c = await w.Character(level: 10);
        var r = await w.Actions.EditCharacter(c.Id, c.Version, Sheet(level: 10, xp: 0, cls: "heavy"));
        Assert.Equal((AdminOutcome.Refused, "rule:edit_class"), (r.Outcome, r.Reason));
        Assert.Equal("scout", (await w.Get(c.Id))!.ClassName);
    }
}
