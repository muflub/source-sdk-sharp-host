using SourceSharp.Host.Contracts;

namespace SourceSharp.Host.Testing;

/// <summary>Exactly the 1.0 surface, delegated to FakeGameRules; no later member is implemented, so
/// every later member runs its default body. Subclass it to implement one member of a later contract.</summary>
public class Contract10Rules(FakeGameRules f) : IGameRules
{
    /// <summary>The rules delegated to, for a subclass that implements a 1.1 member as well.</summary>
    protected FakeGameRules Fake => f;

    public RolledItem RollItem(ItemRollContext ctx, ulong seed) => f.RollItem(ctx, seed);
    public bool Replays(RolledItem item) => f.Replays(item);
    public KillRoll KillRoll(ulong instanceSeed, uint killSeq, string robotTemplate, int depth, int partySize) => f.KillRoll(instanceSeed, killSeq, robotTemplate, depth, partySize);
    public IReadOnlyList<RolledItem> BossDrops(ItemRollContext ctx, string robotTemplate, ulong seed) => f.BossDrops(ctx, robotTemplate, seed);
    public int BackpackSlots(CharacterSheet sheet) => f.BackpackSlots(sheet);
    public int StashSlots(IReadOnlyList<CharacterSheet> living) => f.StashSlots(living);
    public CharacterSheet NewCharacter(string className, string name, bool hardcore) => f.NewCharacter(className, name, hardcore);
    public ValidationResult ValidateCheckpoint(CharacterSheet before, CharacterSheet after, CheckpointEvidence evidence) => f.ValidateCheckpoint(before, after, evidence);
    public TradeResult ApplyTrade(TradeState trade) => f.ApplyTrade(trade);
    public LevelPlan GenerateLayout(LayoutKey key) => f.GenerateLayout(key);
    public LevelRow LevelTable(int depth) => f.LevelTable(depth);
    public IReadOnlyList<string> Tiers => f.Tiers;
    public IReadOnlyList<RolledItem> VendorStock(string vendor, ulong seed) => f.VendorStock(vendor, seed);
    public long Price(RolledItem item, PriceKind kind) => f.Price(item, kind);
    public ItemChange OpenCrate(RolledItem crate, ItemRollContext ctx, ulong seed) => f.OpenCrate(crate, ctx, seed);
    public ItemChange Craft(string recipe, IReadOnlyList<RolledItem> inputs, ItemRollContext ctx, ulong seed) => f.Craft(recipe, inputs, ctx, seed);
    public ItemChange Identify(RolledItem item, ulong seed) => f.Identify(item, seed);
    public ItemChange Repair(RolledItem item) => f.Repair(item);
    public ItemChange Salvage(RolledItem item, ItemRollContext ctx, ulong seed) => f.Salvage(item, ctx, seed);
    public ISheetCodec Sheets => f.Sheets;
    public IItemCodec Items => f.Items;
}
