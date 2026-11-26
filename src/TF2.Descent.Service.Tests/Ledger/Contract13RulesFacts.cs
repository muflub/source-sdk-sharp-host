using TF2.Descent.Service.Ledger;
using Microsoft.Extensions.Options;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Contracts;
using SourceSharp.Host.Testing;

namespace TF2.Descent.Service.Tests.Ledger;

/// <summary>Contract 1.3: ValidateEdit is the admin edit's alone; a game server's checkpoint still asks ValidateCheckpoint.</summary>
public class Contract13RulesFacts
{
    /// <summary>A checkpoint without evidence raises nothing; an edit may raise anything.</summary>
    sealed class EditAwareRules(FakeGameRules f) : Contract10Rules(f), IGameRules
    {
        ValidationResult IGameRules.ValidateCheckpoint(CharacterSheet before, CharacterSheet after, CheckpointEvidence evidence) =>
            evidence.Data.Length == 0 && FakeGameRules.SheetCodec.Read(after).Xp > FakeGameRules.SheetCodec.Read(before).Xp
                ? ValidationResult.Fail("no_evidence") : Fake.ValidateCheckpoint(before, after, evidence);
        ValidationResult IGameRules.ValidateEdit(CharacterSheet before, CharacterSheet after) => ValidationResult.Pass;
    }

    static async Task<HostRefusal?> CheckpointXp(LedgerHarness h, DescentLedger ledger, long xp)
    {
        var ch = await h.Character();
        await h.Lease(ch, h.Hub);
        var row = (await h.R(tx => tx.Characters.Get(ch)))!;
        var sheet = FakeGameRules.SheetCodec.Write(FakeGameRules.SheetCodec.Read(new CharacterSheet(row.Sheet, row.SheetVersion)) with { Xp = xp });
        try { await h.W(tx => ledger.Checkpoint(tx, h.Hub, ch, h.Token(ch), sheet.Data, sheet.SchemaVersion, [], 1, row.Version)); return null; }
        catch (HostRefusal e) { return e; }
    }

    [Fact]
    public async Task A_checkpoint_asks_ValidateCheckpoint_not_ValidateEdit()
    {
        var h = await LedgerHarness.Create();
        await using var _ = h;
        var ledger = new DescentLedger(new SingleRulesProvider(new EditAwareRules(h.Rules)), Options.Create(h.Options));
        Assert.Null(await CheckpointXp(h, ledger, 0)); // the same call without a raise is accepted
        Assert.Equal("no_evidence", (await CheckpointXp(h, ledger, 500))?.Reason);
    }
}
