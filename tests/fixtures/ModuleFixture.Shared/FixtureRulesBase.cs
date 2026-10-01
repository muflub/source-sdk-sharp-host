using SourceSharp.Host.Contracts;

namespace ModuleFixture;

/// <summary>
/// The shared body of the fixture rules modules (plan §1.2): compiled into each fixture
/// assembly as source, so each module is self-contained and only its KillRoll differs.
/// Everything the facts do not call refuses loudly.
/// </summary>
public abstract class FixtureRulesBase : IGameRules
{
    public abstract KillRoll KillRoll(ulong instanceSeed, uint killSeq, string robotTemplate, int depth, int partySize);

    static NotSupportedException No() => new("fixture rules module: not used by the facts");

    public RolledItem RollItem(ItemRollContext ctx, ulong seed) => throw No();
    public bool Replays(RolledItem item) => throw No();
    public IReadOnlyList<RolledItem> BossDrops(ItemRollContext ctx, string robotTemplate, ulong seed) => throw No();
    public int BackpackSlots(CharacterSheet sheet) => 20;
    public int StashSlots(IReadOnlyList<CharacterSheet> living) => 50;
    public CharacterSheet NewCharacter(string className, string name, bool hardcore) => throw No();
    public ValidationResult ValidateCheckpoint(CharacterSheet before, CharacterSheet after, CheckpointEvidence evidence) => ValidationResult.Pass;
    public TradeResult ApplyTrade(TradeState trade) => new(true);
    public LevelPlan GenerateLayout(LayoutKey key) => throw No();
    public LevelRow LevelTable(int depth) => new(depth, depth * 5, $"depth{depth}", depth * 3);
    public IReadOnlyList<string> Tiers { get; } = ["Stock", "Vintage", "Strange", "Unusual", "Australium"];
    public IReadOnlyList<RolledItem> VendorStock(string vendor, ulong seed) => [];
    public long Price(RolledItem item, PriceKind kind) => 1;
    public ItemChange OpenCrate(RolledItem crate, ItemRollContext ctx, ulong seed) => throw No();
    public ItemChange Craft(string recipe, IReadOnlyList<RolledItem> inputs, ItemRollContext ctx, ulong seed) => throw No();
    public ItemChange Identify(RolledItem item, ulong seed) => throw No();
    public ItemChange Repair(RolledItem item) => throw No();
    public ItemChange Salvage(RolledItem item, ItemRollContext ctx, ulong seed) => throw No();
    public ISheetCodec Sheets => throw No();
    public IItemCodec Items => throw No();
}
