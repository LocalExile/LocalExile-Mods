using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

static partial class GmCommandsRules
{
    /// <summary>The item as well rolled as it can be (/item … max, /item_max). Every implicit and explicit value is at
    /// the top of its range: a unique's own range where it has one, else the mod's, which are the ranges a Blessed and a
    /// Divine Orb roll in. A magic or rare item's affixes first become their best tier for its item level. Armour gets
    /// the top base defence roll. A value that picks something stays (<see cref="PicksSomething"/>), and so do
    /// enchantments and hidden mods, whose values are not rolls. The top is the highest number, or the lowest for a
    /// stat where less is better (<see cref="LessIsBetter"/>).</summary>
    internal static Item MaxRolled(ClientState conn, Item item)
    {
        var mods = ItemsFor(conn).Mods;
        var own = UniquesFor(conn).RollsOf(item);
        var fractured = item.FracturedIndexes();
        IReadOnlyList<(ModCatalog.Mod Mod, int Weight)> pool = item.Rarity is ItemRarity.Magic or ItemRarity.Rare
            && ItemsFor(conn).ByHash(item.TypeHash) is { } type ? RollsFor(conn).Pool(type, item) : [];
        // An affix's best tier: the highest-level mod of its series that the base rolls at this item level. A bench
        // craft is of another domain and an essence's mod is not in the pool, so both keep their tier.
        ModCatalog.Mod Best(ModCatalog.Mod mod) => mod.EssenceOnly ? mod : pool.Select(p => p.Mod)
            .Where(m => m.Domain == mod.Domain && m.GenerationType == mod.GenerationType && m.ModType == mod.ModType
                && m.Families.SequenceEqual(mod.Families) && m.Stats.Select(s => s.Id).SequenceEqual(mod.Stats.Select(s => s.Id)))
            .Append(mod).MaxBy(m => m.Level)!;
        ItemMod Top(ItemMod on, int index)
        {
            if (on.Section is not (ModSection.Implicit or ModSection.Explicit) || mods.ByHash(on.Hash) is not { } mod) return on;
            var best = on.Section == ModSection.Explicit && !fractured.Contains(index) ? Best(mod) : mod;
            return new(on.Section, best.Hash, [.. Enumerable.Range(0, best.Stats.Count).Select(stat =>
            {
                if (PicksSomething(best.Stats[stat].Id) && stat < on.Values.Count) return on.Values[stat];
                var (min, max) = own?.Invoke(best, on.Section, stat) ?? best.RangeAt(stat);
                return LessIsBetter(best.Stats[stat].Id) ? Math.Min(min, max) : Math.Max(min, max);
            })]);
        }
        return item with
        {
            DefenceBaseRoll = item.Components.Contains("Armour") ? Item.MaximumDefenceBaseRoll : item.DefenceBaseRoll,
            Mods = [.. item.Mods.Select(Top)],
        };
    }

    /// <summary>A stat whose value says which one and not how much: the support gem a Forbidden Shako or a Pearl of
    /// Tsoatha grants and its socket, a Timeless Jewel's seed, conqueror and version, Thread of Hope's ring, a veiled
    /// mod's seed. They have a range in Mods.dat as rolls do, and its top would make another item.</summary>
    internal static bool PicksSomething(string stat) => stat.Contains("_index", StringComparison.Ordinal)
        || stat.StartsWith("local_unique_jewel_alternate_tree_", StringComparison.Ordinal)
        || stat is "local_jewel_variable_ring_radius_value" or "veiled_mod_seed";

    /// <summary>A stat where less is better, so its best roll is the bottom of its range: damage the character takes
    /// (Ashrend's -60 to -30 physical damage from attacks), what a skill costs, flask charges used, requirements, and
    /// ailments, curses and self-damage on the character. Told by the stat's name, so it covers these families and no
    /// more: every other stat counts as "more is better", and /item_mod set gives it another value.</summary>
    internal static bool LessIsBetter(string stat) => LowerIsBetter.IsMatch(stat) && !OnEnemies.IsMatch(stat);

    static readonly System.Text.RegularExpressions.Regex LowerIsBetter = new(
        @"damage_taken_(from_hits_|over_time_|from_crit_)?\+|_cost_\+|charges_used_\+|requirements?_\+"
        + @"|^(local_)?self_.*(duration|effect)_\+%|(curse|chill|shocked)_effect(iveness)?_on_self_\+%|ailment_on_self_duration_\+%"
        + @"|against_self|^self_.*damage_on_.*skill_use|life_taken_per_minute|reservation_efficiency_-2%_per_1|^-%_of_your",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // "Enemies take increased damage" is damage taken too, and there more is better.
    static readonly System.Text.RegularExpressions.Regex OnEnemies = new(
        @"enem(y|ies).*damage_taken|ailment_types|chilling_areas", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>A non-unique cluster jewel as a drop rolls it: the size's base type with the enchantments that carry
    /// how many passives it has, what its small passives grant (its theme) and its jewel sockets (two or one for a
    /// large, one for a medium, none for a small). Null when the base type or a mod is missing from the game data.</summary>
    internal static Item? CreateClusterJewel(int size, int passives, int sockets, string? theme = null,
        IEnumerable<string>? notables = null)
    {
        string name = size switch { 2 => "Large Cluster Jewel", 1 => "Medium Cluster Jewel", 0 => "Small Cluster Jewel", _ => "" };
        if (name.Length == 0 || Items.Find(name) is not { } baseType) return null;
        var asked = notables?.ToList() ?? [];
        var mods = new List<ItemMod>();
        // "Adds N Passive Skills": one enchantment mod whose two stats are the node index and the count.
        if (Items.Mods.ById("JewelExpansionPassiveNodes") is { } count && count.Stats.Count >= 2)
            mods.Add(new ItemMod(ModSection.Enchant, count.Hash, [0, passives]));
        string socketMod = size switch
        {
            2 => sockets >= 2 ? "JewelExpansionJewelNodesLarge2___" : "JewelExpansionJewelNodesLarge1",
            1 => "JewelExpansionJewelNodesMedium",
            _ => "",
        };
        if (sockets > 0 && socketMod.Length > 0 && Items.Mods.ById(socketMod) is { } socket && socket.Stats.Count > 0)
            mods.Add(new ItemMod(ModSection.Enchant, socket.Hash, [.. socket.MaxValues()]));
        // The theme: what the cluster's small passives grant ("Added Small Passive Skills grant: ..."). It is an
        // enchantment (like the count and the sockets); a smaller jewel in a larger socket is padded by the client
        // with empty nodes instead, see JewelFitsCell.
        if (Items.Mods.ById(theme ?? "AfflictionJewelSmallPassivesGrantLife_") is { } themed && themed.Stats.Count > 0)
            mods.Add(new ItemMod(ModSection.Enchant, themed.Hash, [.. themed.MaxValues()]));
        // "1 Added Passive Skill is <notable>": an explicit prefix, one per notable the jewel has.
        foreach (string notable in asked)
            if (Items.Mods.ById(notable) is { } found && found.Stats.Count > 0)
                mods.Add(new ItemMod(ModSection.Explicit, found.Hash, [.. found.MaxValues()]));
        return ItemCatalog.Create(baseType) with
        {
            // The notables are the item's only affixes. A jewel without one is Normal, one or two are Magic (a magic
            // item may not have zero affixes: the client then renders the whole cluster as empty "Nothingness" nodes)
            // and three (a large cluster can carry three) Rare.
            Rarity = asked.Count switch { 0 => ItemRarity.Normal, <= 2 => ItemRarity.Magic, _ => ItemRarity.Rare },
            ItemLevel = 68,
            Mods = mods,
        };
    }

    /// <summary>Voices, the unique Large Cluster Jewel: three jewel sockets plus 1, 3, 5 or 7 empty passives (the four
    /// variants, the rarer the fewer). Null when the base type, the unique art or a mod is missing from the game data.
    /// The art is item visual <c>UniqueJewel147</c> (Art/2DItems/Jewels/UniqueJewelBase3.dds, the same row
    /// UniqueStashLayout gives Voices); without it the client draws the plain Large Cluster Jewel.</summary>
    internal static Item? CreateVoices(int emptyPassives)
    {
        string? variant = emptyPassives switch
        {
            1 => "ExpansionJewelEmptyPassiveUnique__1",
            3 => "ExpansionJewelEmptyPassiveUnique__2",
            5 => "ExpansionJewelEmptyPassiveUnique_3_",
            7 => "ExpansionJewelEmptyPassiveUnique__4",
            _ => null,
        };
        if (variant is null || Items.Find("Large Cluster Jewel") is not { } baseType) return null;
        if (UniqueJewelVisuals.Of("Voices") is not ushort visual) return null;
        if (Items.Mods.ById("ExpansionJewel3JewelSockets") is not { } sockets) return null;
        if (Items.Mods.ById(variant) is not { } empty || empty.Stats.Count == 0) return null;
        return ItemCatalog.Create(baseType) with
        {
            Rarity = ItemRarity.Unique,
            Corrupted = true,
            ItemLevel = 1,
            VisualIdentity = visual,
            Mods =
            [
                new ItemMod(ModSection.Explicit, sockets.Hash, [.. sockets.MaxValues()]),
                new ItemMod(ModSection.Explicit, empty.Hash, [.. empty.MaxValues()]),
            ],
        };
    }
}
