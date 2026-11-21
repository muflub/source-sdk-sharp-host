using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SourceSharp.Host.Contracts;

namespace SourceSharp.Host.Testing;

/// <summary>
/// A deterministic IGameRules for the unit tier (CLAUDE.md: tests use FakeGameRules; nothing
/// here compiles against a Descent assembly). Small, pure and readable, so a fact can predict
/// every roll: KillRoll and RollItem are hashes of their inputs.
/// </summary>
public sealed class FakeGameRules : IGameRules
{
    public static readonly string[] TierNames = ["Stock", "Vintage", "Strange", "Unusual", "Australium"];
    static readonly string[] Bases = ["scattergun", "rocket_launcher", "minigun", "wrench", "sniper_rifle", "knife", "medigun", "flamethrower"];

    /// <summary>Added to every kill roll's hash: two fakes with different salts replay differently (the D-H9 fixture pair).</summary>
    public ulong Salt { get; init; }
    public int Backpack { get; init; } = 20;
    public int Stash { get; init; } = 50;

    public IReadOnlyList<string> Tiers => TierNames;
    public ISheetCodec Sheets { get; } = new SheetCodec();
    public IItemCodec Items { get; } = new ItemCodec();

    public static ulong Hash(params object[] parts)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', parts)));
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    public RolledItem RollItem(ItemRollContext ctx, ulong seed)
    {
        var h = Hash("item", seed, ctx.CharacterClass, ctx.CharacterLevel, ctx.Depth, ctx.ItemLevel, ctx.Tier, ctx.LootTable);
        // A kit or container item names its base type in the source ("base:health_small"), so it replays.
        var baseType = ctx.Source.StartsWith(NamedBase) ? ctx.Source[NamedBase.Length..] : Bases[(int)(h % (ulong)Bases.Length)];
        var identified = ctx.Tier < 2; // Strange and above drop unidentified (D22)
        var item = new RolledItem(seed, baseType, ctx.Tier, ctx.ItemLevel, 1, identified, [], ItemCodec.Schema, ctx.Tier, ctx);
        return item with { Instance = ItemCodec.Encode(item) };
    }

    public bool Replays(RolledItem item)
    {
        var again = RollItem(item.Context, item.Seed);
        return again.BaseType == item.BaseType && again.Rarity == item.Rarity && again.ItemLevel == item.ItemLevel
               && again.Tier == item.Tier && again.Count == item.Count;
    }

    /// <summary>Two kills in three drop; tiers by the hash: 60 % Stock, 20 % Vintage, 10 % Strange, 10 % Unusual.</summary>
    public KillRoll KillRoll(ulong instanceSeed, uint killSeq, string robotTemplate, int depth, int partySize)
    {
        var h = Hash("kill", instanceSeed, killSeq, robotTemplate, depth, partySize) + Salt;
        var drop = h % 3 != 0;
        var t = (int)(h / 3 % 10);
        var tier = t < 6 ? 0 : t < 8 ? 1 : t < 9 ? 2 : 3;
        return new KillRoll(drop, drop ? tier : -1, depth);
    }

    public IReadOnlyList<RolledItem> BossDrops(ItemRollContext ctx, string robotTemplate, ulong seed) =>
        [RollItem(ctx with { Tier = 2, Source = "boss:" + robotTemplate }, seed), RollItem(ctx with { Tier = 3, Source = "boss:" + robotTemplate }, seed + 1)];

    public int BackpackSlots(CharacterSheet sheet) => Backpack;
    public int StashSlots(IReadOnlyList<CharacterSheet> living) => Stash;

    public const string NamedBase = "base:";

    /// <summary>Every class: two small health potions and a small mana potion on the belt.</summary>
    public IReadOnlyList<StartingItem> StartingKit(string className, CharacterSheet sheet, ulong seed)
    {
        RolledItem Roll(string baseType, int n) =>
            RollItem(new ItemRollContext(className, 1, 0, 1, 0, "kit", NamedBase + baseType), Hash("kit", seed, n));
        return
        [
            new(Roll("health_small", 0), ItemContainer.Belt, 0),
            new(Roll("health_small", 1), ItemContainer.Belt, 1),
            new(Roll("mana_small", 2), ItemContainer.Belt, 2),
        ];
    }

    /// <summary>The split alone: total / members, the remainder one apiece in order (the fake has no Scavenger).</summary>
    public IReadOnlyList<long> AustraliumShares(long total, IReadOnlyList<CharacterSheet> killerFirst)
    {
        var n = killerFirst.Count;
        return n == 0 ? [] : Enumerable.Range(0, n).Select(i => total / n + (i < total % n ? 1 : 0)).ToList();
    }

    /// <summary>
    /// The outcome kind is keyed on (seed, seq, depth), so every hero agrees on it: nothing, Australium
    /// (depth + the hero's level), a small health potion, or an item rolled for the hero.
    /// </summary>
    public BreakableRoll BreakableDrops(ulong instanceSeed, uint breakableSeq, int depth, CharacterSheet opener)
    {
        var who = SheetCodec.Read(opener);
        var seed = Hash("breakable-item", instanceSeed, breakableSeq, who.Name);
        var row = LevelTable(Math.Max(1, depth));
        return (Hash("breakable", instanceSeed, breakableSeq, depth) % 4) switch
        {
            1 => new(depth + who.Level, []),
            2 => new(0, [RollItem(new ItemRollContext(who.ClassName, who.Level, depth, row.ItemLevel, 0, "breakable", NamedBase + "health_small"), seed)]),
            3 => new(0, [RollItem(new ItemRollContext(who.ClassName, who.Level, depth, row.ItemLevel, 0, row.LootTable, "breakable"), seed)]),
            _ => new(0, []),
        };
    }

    /// <summary>One container in four holds a small health potion for its opener.</summary>
    public IReadOnlyList<RolledItem> ContainerDrops(ulong instanceSeed, uint containerSeq, int depth, CharacterSheet opener)
    {
        var who = SheetCodec.Read(opener);
        if (Hash("container", instanceSeed, containerSeq, depth) % 4 != 0) return [];
        var ctx = new ItemRollContext(who.ClassName, who.Level, depth, Math.Max(1, depth), 0, "container", NamedBase + "health_small");
        return [RollItem(ctx, Hash("container-item", instanceSeed, containerSeq, who.Name))];
    }

    public static readonly string[] Slots = ["primary", "secondary", "melee", "hat", "body", "feet", "trinket1", "trinket2", "amulet"];
    public const int BeltSlots = 4;
    public IReadOnlyList<string> EquipSlots => Slots;

    public int ContainerSlots(CharacterSheet sheet, ItemContainer container) => container switch
    {
        ItemContainer.Backpack => Backpack,
        ItemContainer.Equipped => Slots.Length,
        ItemContainer.Belt => BeltSlots,
        _ => 0,
    };

    /// <summary>Potions (base types health_* and mana_*) only on the belt; potions and kits never equipped.</summary>
    public static bool IsPotion(RolledItem item) => item.BaseType.StartsWith("health_") || item.BaseType.StartsWith("mana_");

    public string? PlaceRefusal(CharacterSheet sheet, RolledItem item, ItemContainer container, int slot) => container switch
    {
        ItemContainer.Belt when !IsPotion(item) => "potions_only",
        ItemContainer.Equipped when IsPotion(item) || item.BaseType == RepairKit => "wrong_slot",
        _ => null,
    };

    public CharacterSheet NewCharacter(string className, string name, bool hardcore) =>
        SheetCodec.Write(new Sheet(className, name, 1, 0, 0, hardcore, false));

    public ValidationResult ValidateCheckpoint(CharacterSheet before, CharacterSheet after, CheckpointEvidence evidence)
    {
        var a = SheetCodec.Read(before);
        var b = SheetCodec.Read(after);
        if (b.ClassName != a.ClassName) return ValidationResult.Fail("class_changed");
        if (b.Level < a.Level) return ValidationResult.Fail("level_decreased");
        if (b.Xp < a.Xp) return ValidationResult.Fail("xp_decreased");
        if (b.Level - a.Level > 5) return ValidationResult.Fail("level_jump");
        if (a.Fallen && !b.Fallen) return ValidationResult.Fail("unfallen");
        return ValidationResult.Pass;
    }

    public TradeResult ApplyTrade(TradeState t) =>
        t.OfferB.Count - t.OfferA.Count > t.FreeSlotsA ? new TradeResult(false, "no_capacity_a")
        : t.OfferA.Count - t.OfferB.Count > t.FreeSlotsB ? new TradeResult(false, "no_capacity_b")
        : new TradeResult(true);

    public LevelPlan GenerateLayout(LayoutKey key)
    {
        var rooms = key.Rooms.Where(r => key.Depth >= r.MinDepth && key.Depth <= r.MaxDepth).ToList();
        if (rooms.Count == 0) rooms = [.. key.Rooms];
        var up = rooms.FirstOrDefault(r => r.Tags.Contains("start"))?.Name ?? rooms[0].Name;
        var down = rooms.FirstOrDefault(r => r.Tags.Contains("stairs"))?.Name ?? rooms[^1].Name;
        var placements = new List<Placement> { new(up, 0, 0, 0, "up") };
        var fill = rooms.Where(r => r.Name != up && r.Name != down).ToList();
        if (fill.Count > 0)
            placements.Add(new Placement(fill[(int)(Hash("layout", key.Seed, key.Depth) % (ulong)fill.Count)].Name, 0, 1, 90, "fill"));
        placements.Add(new Placement(down, 0, placements.Count, 0, "down"));
        return new LevelPlan(1, placements.Count, placements, up, down);
    }

    public LevelRow LevelTable(int depth) => new(depth, Math.Max(1, depth * 5), $"depth{depth}", depth * 3);

    public IReadOnlyList<RolledItem> VendorStock(string vendor, ulong seed) =>
        Enumerable.Range(0, 6).Select(i => RollItem(new ItemRollContext("any", 1, 0, 5, i % 2, "vendor:" + vendor, "vendor"), seed + (ulong)i)).ToList();

    public long Price(RolledItem item, PriceKind kind)
    {
        var buy = 10L * (item.Rarity + 1) * Math.Max(1, item.ItemLevel) * item.Count;
        return kind switch { PriceKind.Buy => buy, PriceKind.Sell => buy / 4, _ => buy / 2 };
    }

    public ItemChange OpenCrate(RolledItem crate, ItemRollContext ctx, ulong seed) =>
        new([0], [Re(RollItem(ctx with { Tier = Math.Min(3, crate.Rarity + 1), Source = "crate" }, seed) with { Identified = false })], 0);

    public ItemChange Craft(string recipe, IReadOnlyList<RolledItem> inputs, ItemRollContext ctx, ulong seed) =>
        recipe != "combine3" ? new([], [], 0, "unknown_recipe")
        : inputs.Count != 3 ? new([], [], 0, "needs_three")
        : new([0, 1, 2], [RollItem(ctx with { Tier = Math.Min(3, inputs.Max(i => i.Rarity) + 1), Source = "craft" }, seed)], 5);

    public ItemChange Identify(RolledItem item, ulong seed) =>
        item.Identified ? new([], [], 0, "already_identified") : new([0], [Re(item with { Identified = true })], 2);

    /// <summary>The fake's stack limit.</summary>
    public const int MaxStack = 5;

    /// <summary>Same base type only, up to <see cref="MaxStack"/>; the rest stays in <paramref name="source"/>.</summary>
    public ItemChange Merge(RolledItem source, RolledItem target)
    {
        if (source.BaseType != target.BaseType) return new([], [], 0, "not_same_kind");
        if (target.Count >= MaxStack) return new([], [], 0, "stack_full");
        var total = source.Count + target.Count;
        var kept = Math.Min(MaxStack, total);
        var merged = Re(target with { Count = kept });
        return total == kept ? new([0], [merged], 0) : new([], [merged, Re(source with { Count = total - kept })], 0);
    }

    /// <summary>A stack loses <paramref name="count"/>; at 0 the item is gone.</summary>
    public ItemChange Consume(CharacterSheet sheet, RolledItem item, int count) =>
        count < 1 || count > item.Count ? new([], [], 0, "bad_count")
        : count == item.Count ? new([0], [], 0)
        : new([], [Re(item with { Count = item.Count - count })], 0);

    /// <summary>A town repair: back to full durability, maximum unchanged, cost 1.</summary>
    public ItemChange Repair(RolledItem item)
    {
        var (_, max) = ItemCodec.DurabilityOf(item);
        return new([0], [ItemCodec.WithDurability(item, max, max)], 1);
    }

    public const string RepairKit = "repair_kit";

    /// <summary>
    /// The game's field-repair shape (plan_sdk_2_7 §2): restore level + (seed % level), lower the
    /// maximum by max(max / (level + 9), 1), consume the kit. Free.
    /// </summary>
    public ItemChange Repair(RolledItem item, RepairKind kind, RolledItem? kit, ulong seed)
    {
        if (kind == RepairKind.Town) return Repair(item);
        if (kit is null || kit.BaseType != RepairKit) return new([], [], 0, "not_a_kit");
        var (d, max) = ItemCodec.DurabilityOf(item);
        var level = Math.Max(1, item.ItemLevel);
        var newMax = Math.Max(0, max - Math.Max(max / (level + 9), 1));
        var restored = Math.Min(newMax, d + level + (int)(seed % (ulong)level));
        return new([1], [ItemCodec.WithDurability(item, restored, newMax)], 0);
    }

    /// <summary>A hero at this level or above repairs in town for free (the fake's stand-in for a perk like town_free).</summary>
    public const int TownFreeLevel = 50;

    /// <summary>The 1.1 repair, with the hero: a field repair restores the hero's level more; a town repair is free from <see cref="TownFreeLevel"/>.</summary>
    public ItemChange Repair(RolledItem item, RepairKind kind, RolledItem? kit, CharacterSheet sheet, ulong seed)
    {
        var hero = SheetCodec.Read(sheet);
        var change = Repair(item, kind, kit, seed);
        if (!change.Ok) return change;
        if (kind == RepairKind.Town)
            return hero.Level >= TownFreeLevel ? change with { Cost = 0 } : change;
        var (d, max) = ItemCodec.DurabilityOf(change.Produced[0]);
        return change with { Produced = [ItemCodec.WithDurability(change.Produced[0], Math.Min(max, d + hero.Level), max)] };
    }

    /// <summary>Durability goes down by the amount, never below 0; the item is never destroyed.</summary>
    public ItemChange Wear(RolledItem item, int amount)
    {
        if (amount <= 0) return new([], [], 0, "bad_amount");
        var (d, max) = ItemCodec.DurabilityOf(item);
        return new([], [ItemCodec.WithDurability(item, Math.Max(0, d - amount), max)], 0);
    }

    public ItemChange Salvage(RolledItem item, ItemRollContext ctx, ulong seed) =>
        new([0], [Re(RollItem(ctx with { Tier = 0, Source = "salvage" }, seed) with { BaseType = "metal", Count = item.Rarity + 1 })], 0);

    /// <summary>An item changed with `with` carries its old bytes until it is encoded again.</summary>
    static RolledItem Re(RolledItem i) => i with { Instance = ItemCodec.Encode(i) };

    public sealed record Sheet(string ClassName, string Name, int Level, long Xp, int ReachedDepth, bool Hardcore, bool Fallen);

    public sealed class SheetCodec : ISheetCodec
    {
        public const int Schema = 1;
        public int SchemaVersion => Schema;
        public static CharacterSheet Write(Sheet s) => new(JsonSerializer.SerializeToUtf8Bytes(s), Schema);
        public static Sheet Read(CharacterSheet s) => JsonSerializer.Deserialize<Sheet>(s.Data) ?? throw new InvalidDataException("empty sheet");
        public SheetSummary Summarize(CharacterSheet sheet)
        {
            var s = Read(sheet);
            return new SheetSummary(s.ClassName, s.Name, s.Level, s.Xp, s.ReachedDepth, s.Hardcore, s.Fallen);
        }
        public string ToJson(CharacterSheet sheet) => Encoding.UTF8.GetString(sheet.Data);
        public CharacterSheet FromJson(string json) => Write(JsonSerializer.Deserialize<Sheet>(json) ?? throw new InvalidDataException("empty sheet"));
    }

    public sealed class ItemCodec : IItemCodec
    {
        public const int Schema = 1;
        public int SchemaVersion => Schema;

        public const int FullDurability = 100;

        sealed record Wire(ulong Seed, string BaseType, int Rarity, int ItemLevel, int Count, bool Identified, int Tier, ItemRollContext Context,
            int Durability = FullDurability, int MaxDurability = FullDurability);

        /// <summary>Encodes the item; durability is carried over from its current bytes (full for a new roll).</summary>
        public static byte[] Encode(RolledItem i)
        {
            var (d, max) = DurabilityOf(i);
            return Write(i, d, max);
        }

        static byte[] Write(RolledItem i, int durability, int max) =>
            JsonSerializer.SerializeToUtf8Bytes(new Wire(i.Seed, i.BaseType, i.Rarity, i.ItemLevel, i.Count, i.Identified, i.Tier, i.Context, durability, max));

        /// <summary>(durability, maximum) from the item's bytes; full for an item without them.</summary>
        public static (int Durability, int Max) DurabilityOf(RolledItem i)
        {
            if (i.Instance.Length == 0) return (FullDurability, FullDurability);
            var w = JsonSerializer.Deserialize<Wire>(i.Instance) ?? throw new InvalidDataException("empty item");
            return (w.Durability, w.MaxDurability);
        }

        public static RolledItem WithDurability(RolledItem i, int durability, int max) => i with { Instance = Write(i, durability, max) };

        public RolledItem Decode(byte[] instance, int schemaVersion)
        {
            var w = JsonSerializer.Deserialize<Wire>(instance) ?? throw new InvalidDataException("empty item");
            return new RolledItem(w.Seed, w.BaseType, w.Rarity, w.ItemLevel, w.Count, w.Identified, instance, schemaVersion, w.Tier, w.Context);
        }

        public string ToJson(RolledItem item) => Encoding.UTF8.GetString(Encode(item));
        public RolledItem FromJson(string json) => Decode(Encoding.UTF8.GetBytes(json), Schema);
    }
}
