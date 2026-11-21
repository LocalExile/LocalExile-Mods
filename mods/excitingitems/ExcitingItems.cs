using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

/// <summary>
/// Rare chances on magic, rare and unique drops, each its own and all able to land on one item:
/// - a unique item's modifier as an extra implicit (one, two or three; which ones by the chosen mode);
/// - an empowered modifier: one implicit or explicit modifier's values times 1.1 to 2;
/// - an extra modifier, one more than the item could have, and every modifier of its side (prefixes or suffixes)
///   fractured.
/// There is no hook on a rolled drop, so the mod watches the instance's ground for new items on the player tick, takes
/// a chosen one off the ground and puts the changed item down in its place; the Gateway's ground sync removes the old
/// object from the client and creates the new one. The game's own refusals (no fracturing of influenced or synthesised
/// items) are its crafting rules, not a drop's: an item gets these whatever it is.
/// </summary>
public sealed class ExcitingItemsMod : IMod, IDisposable
{
    sealed record ImplicitChoices(int Chance, int ChanceTwo, int ChanceThree, string Mode, bool RarityWeights, string[] Rarities);
    sealed record EmpoweredChoices(int Chance, int[] Percent, string[] Rarities);
    sealed record ExtraChoices(int Chance, string[] Rarities, bool InfluencedMods = true);
    sealed record Choices(ImplicitChoices Implicits, EmpoweredChoices Empowered, ExtraChoices ExtraModifier, string[] Blacklist,
        bool Mark, bool ShowInFilters, bool Announce, string FilterFolder);

    const string FilterStart = "# >>> excitingitems (LocalExile mod): always show exciting items >>>";
    const string FilterEnd = "# <<< excitingitems <<<";
    const string OwnFilter = "LocalExile Exciting Items.filter";
    static readonly string[] FilterRule =
    [
        "Show",
        "    SynthesisedItem True",
        "    SetFontSize 45",
        "    SetTextColor 255 255 255 255",
        "    SetBorderColor 255 215 0 255",
        "    SetBackgroundColor 110 0 150 255",
        "    PlayAlertSound 6 300",
        "    PlayEffect Yellow",
        "    MinimapIcon 0 Yellow Star",
    ];

    // A modifier that names a slot or a kind of item, or changes the item it is on, belongs on the kinds of item the
    // uniques that have it are. The others can go on any gear.
    static readonly string[] SlotWords =
    [
        "local_", "socketed", "ring", "amulet", "belt", "quiver", "shield", "off_hand", "main_hand", "weapon", "boots",
        "gloves", "helmet", "body_armour", "flask", "jewel", "wand", "bow", "staff", "sword", "axe", "mace", "claw",
        "dagger", "sceptre", "one_hand", "two_hand", "dual_wield", "wielding", "while_holding", "unarmed", "this_item",
        "item_", "radius", "passive",
    ];
    static readonly HashSet<string> JewelClasses = new(StringComparer.OrdinalIgnoreCase) { "Jewel", "AbyssJewel" };
    static readonly HashSet<string> NotGear = new(StringComparer.OrdinalIgnoreCase)
        { "Jewel", "AbyssJewel", "AnimalCharm", "Map", "MapFragment", "Relic", "SanctumRelic", "Tincture" };

    [Flags] enum Theme { None = 0, Offence = 1, Defence = 2, Speed = 4, Ailment = 8, Scaling = 16, Utility = 32 }

    sealed class Source(ModCatalog.Mod mod, bool bound, Dictionary<int, (int Min, int Max)> ranges, string unique, Theme theme)
    {
        public ModCatalog.Mod Mod => mod;
        public bool Bound => bound;
        public Dictionary<int, (int Min, int Max)> Ranges => ranges;
        public string Unique => unique;
        public Theme Theme => theme;
        public HashSet<string> Classes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Jewel => Classes.Overlaps(JewelClasses);
        public bool Gear => Classes.Any(c => !NotGear.Contains(c));
        /// <summary>The drop weight of the most common unique that has the modifier (0 to 1; Mageblood about 0.001).</summary>
        public double Weight;
    }
    sealed class Watch { public uint Next; public bool Started; public readonly HashSet<uint> Ours = []; }
    sealed record Forced(int Implicits, bool Empowered, bool Extra, ClientState For);

    readonly ConditionalWeakTable<ItemCatalog, List<Source>> _pools = new();
    readonly ConditionalWeakTable<InstanceGround, Watch> _watches = new();
    Choices _choices = new(new(1000, 100000, 1000000, "wild", true, ["magic", "rare", "unique"]), new(500, [10, 100], ["magic", "rare", "unique"]),
        new(1000, ["magic", "rare", "unique"]), [], true, false, true, "");
    Forced? _forced;
    Action<string> _log = _ => { };

    public void Load(IModContext mod)
    {
        string id = mod.Manifest.Id;
        _choices = mod.Settings<Choices>();
        _log = mod.Log;
        try { mod.Log(Filters(_choices)); }
        catch (Exception e) { mod.Log($"loot filters not updated: {e.Message}"); }
        GameEvents.Tick.Add(id, (conn, _) => Scan(conn));
        GameEvents.Command(id, "exciting",
            "/exciting (Exciting Items: the chances) | /exciting next implicit [1-3] | empowered | extra | all (the next magic, rare or unique drop gets it)",
            async (conn, args) =>
            {
                var player = Program.PlayerOf(conn);
                if (args.Length > 0 && args[0].Equals("next", StringComparison.OrdinalIgnoreCase))
                {
                    string what = args.Length > 1 ? args[1].ToLowerInvariant() : "all";
                    int count = args.Length > 2 && int.TryParse(args[2], out int n) ? Math.Clamp(n, 1, 3) : 1;
                    var forced = what switch
                    {
                        "implicit" or "implicits" => new Forced(count, false, false, conn),
                        "empowered" or "boost" => new Forced(0, true, false, conn),
                        "extra" or "fractured" => new Forced(0, false, true, conn),
                        _ => new Forced(count, true, true, conn),
                    };
                    Volatile.Write(ref _forced, forced);
                    await player.Tell($"Exciting Items: your next magic, rare or unique drop gets {Effects(forced.Implicits, forced.Empowered, forced.Extra)}.");
                    return;
                }
                var c = _choices;
                int known = Pool(Game.ItemsFor(conn), Game.UniquesFor(conn), Game.DropsFor(conn)).Count;
                await player.Tell($"Exciting Items: unique implicits 1 in {c.Implicits.Chance} / {c.Implicits.ChanceTwo} / {c.Implicits.ChanceThree} " +
                    $"({c.Implicits.Mode}, {known} modifiers known); empowered 1 in {c.Empowered.Chance} (+{Low(c)}-{High(c)}%); " +
                    $"extra fractured modifier 1 in {c.ExtraModifier.Chance}. /exciting next implicit|empowered|extra|all forces one.");
            });
    }

    // A reload makes a new instance: the tables are emptied so they hold nothing of the old assembly.
    public void Dispose()
    {
        _pools.Clear(); _watches.Clear(); _placeable.Clear(); _slots.Clear();
        _forced = null;
    }

    static int Low(Choices c) => Math.Max(1, c.Empowered.Percent.ElementAtOrDefault(0));
    static int High(Choices c) => Math.Max(Low(c), c.Empowered.Percent.ElementAtOrDefault(1));

    static string Effects(int implicits, bool empowered, bool extra) => string.Join(", ", new[]
    {
        implicits > 0 ? (implicits == 1 ? "a unique implicit" : $"{implicits} unique implicits") : null,
        empowered ? "an empowered modifier" : null,
        extra ? "an extra fractured modifier" : null,
    }.Where(s => s is not null));

    static bool Takes(string[] rarities, ItemRarity rarity) => rarities.Contains(rarity.ToString(), StringComparer.OrdinalIgnoreCase);

    // ---- which items and which modifiers ----

    /// <summary>Gear and jewels with modifiers; not flasks, maps, cluster jewels (their modifiers come from their
    /// enchantments) or Timeless Jewels.</summary>
    static bool Target(ItemCatalog.BaseType type) => type.Components.Contains("Mods") && !type.Components.Contains("Flask")
        && !type.Components.Contains("Map") && !type.Components.Contains("MapKey")
        && (JewelClasses.Contains(type.Class) ? !type.Id.Contains("PassiveTreeExpansion") && !type.Id.Contains("Timeless") : !NotGear.Contains(type.Class));

    bool Blacklisted(ModCatalog.Mod mod) => _choices.Blacklist.Any(entry => entry.StartsWith("stat:", StringComparison.OrdinalIgnoreCase)
        ? mod.Stats.Any(s => s.Id.Contains(entry[5..], StringComparison.OrdinalIgnoreCase))
        : !entry.StartsWith("class:", StringComparison.OrdinalIgnoreCase) && mod.Id.StartsWith(entry, StringComparison.OrdinalIgnoreCase));

    /// <summary>A modifier only uniques of blacklisted kinds of item have ("class:UtilityFlask").</summary>
    bool BlacklistedClasses(IEnumerable<string> classes)
    {
        var listed = _choices.Blacklist.Where(e => e.StartsWith("class:", StringComparison.OrdinalIgnoreCase)).Select(e => e[6..].Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return listed.Count > 0 && classes.All(listed.Contains);
    }

    static readonly (Theme Theme, Regex Words)[] Themes =
    [
        (Theme.Speed, new(@"movement_velocity|movement_speed|onslaught|phasing|flask_effect|action_speed")),
        (Theme.Ailment, new(@"ailment|ignite|chill|freez|shock|poison|bleed|stun|curse|elusive|blind|maim|hinder|avoid_|immun|_on_self")),
        (Theme.Scaling, new(@"_per_|per_\d|for_each|_charge|charges|aura|reservation|gem_level|_gems_|socketed|minion|totem|brand")),
        (Theme.Defence, new(@"life|energy_shield|armour|evasion|resist|block|suppress|damage_taken|damage_reduction|regenerat|recoup|fortif|dodge|recover|leech_from")),
        (Theme.Offence, new(@"damage|attack_speed|cast_speed|critical|penetrat|accuracy|projectile|area_of_effect|chain|pierce|impale|skill_effect_duration|added_")),
    ];

    /// <summary>What a modifier is for, told by its stats' names; a modifier of none of these is utility.</summary>
    static Theme ThemeOf(ModCatalog.Mod mod)
    {
        string words = string.Join(' ', mod.Stats.Select(s => s.Id)).ToLowerInvariant();
        var theme = Theme.None;
        foreach (var (t, regex) in Themes) if (regex.IsMatch(words)) theme |= t;
        // "damage taken" and "leech" are defence and sustain, not offence.
        if (theme.HasFlag(Theme.Offence) && Regex.IsMatch(words, "damage_taken|damage_reduction") && !Regex.IsMatch(words, @"damage_\+%|added_|_damage_%")) theme &= ~Theme.Offence;
        return theme == Theme.None ? Theme.Utility : theme;
    }

    /// <summary>The themes a slot takes in thematic mode.</summary>
    static Theme Wants(ItemCatalog.BaseType type) => type.Class switch
    {
        "Gloves" or "Quiver" => Theme.Offence,
        "Body Armour" or "Shield" => Theme.Defence,
        "Boots" => Theme.Speed | Theme.Ailment,
        "Helmet" => Theme.Defence | Theme.Scaling,
        "Amulet" or "Ring" or "Belt" => Theme.Utility,
        _ => type.Tags.Contains("weapon") ? Theme.Offence : Theme.Utility,
    };

    // Every unique's modifiers, once per data set: which kinds of item have each, and how it rolls there.
    List<Source> Pool(ItemCatalog catalog, UniqueCatalog uniques, ItemDrops drops) => _pools.GetValue(catalog, _ =>
    {
        var byId = new Dictionary<string, Source>(StringComparer.Ordinal);
        foreach (var unique in uniques.All)
        {
            string? baseClass = catalog.Find(unique.BaseTypeId)?.Class;
            var ids = unique.Mods.Concat(unique.Variants?.SelectMany(v => v.Mods) ?? []);
            foreach (string modId in ids.Distinct())
            {
                if (catalog.Mods.ById(modId) is not { Stats.Count: > 0 } mod || Blacklisted(mod)) continue;
                if (!byId.TryGetValue(modId, out var source))
                {
                    string words = (mod.Id + " " + string.Join(' ', mod.Stats.Select(s => s.Id))).ToLowerInvariant();
                    bool bound = mod.Stats.Any(s => s.Local) || SlotWords.Any(words.Contains);
                    var ranges = (unique.Ranges ?? []).Where(r => r.Mod == modId && !r.Implicit)
                        .GroupBy(r => r.Stat).ToDictionary(g => g.Key, g => (g.First().Min, g.First().Max));
                    byId[modId] = source = new(mod, bound, ranges, unique.Name, ThemeOf(mod));
                }
                if (baseClass is not null) source.Classes.Add(baseClass);
                source.Weight = Math.Max(source.Weight, drops.UniqueWeight(unique.Name));
            }
        }
        // A modifier only flasks, maps or the like have stays with them, so it never reaches gear or a jewel.
        var pool = byId.Values.Where(s => s.Classes.Count > 0 && !BlacklistedClasses(s.Classes)).ToList();
        // A unique with no drop weight (a boss's, a league's) counts as the rarest there is.
        double rarest = pool.Where(s => s.Weight > 0).Select(s => s.Weight).DefaultIfEmpty(1).Min();
        foreach (var source in pool) if (source.Weight <= 0) source.Weight = rarest;
        return pool;
    });

    /// <summary>Whether a modifier may go on this kind of item. Jewels and gear never mix, and a modifier is always
    /// welcome on its own kind of item. Every modifier not blacklisted has somewhere to go: one the chosen mode leaves
    /// without a slot that drops follows Wild's rules, and one tied to kinds of item that never drop (a fishing rod's)
    /// goes on any gear.</summary>
    bool Fits(ItemCatalog catalog, Source source, ItemCatalog.BaseType type)
    {
        string mode = _choices.Implicits.Mode.ToLowerInvariant();
        if (Rule(source, type, mode)) return true;
        if (Placeable(catalog, source, mode)) return false;
        if (mode != "wild" && Rule(source, type, "wild")) return true;
        return !Placeable(catalog, source, "wild") && JewelClasses.Contains(type.Class) == source.Jewel;
    }

    static bool Rule(Source source, ItemCatalog.BaseType type, string mode)
    {
        bool jewel = JewelClasses.Contains(type.Class);
        if (jewel ? !source.Jewel : !source.Gear) return false;
        if (source.Classes.Contains(type.Class)) return true;
        if (source.Bound) return false;
        return mode switch
        {
            "sanity" => false,
            "thematic" => jewel || (source.Theme & Wants(type)) != 0,
            _ => true,
        };
    }

    readonly ConditionalWeakTable<ItemCatalog, Dictionary<(Source, string), bool>> _placeable = new();
    readonly ConditionalWeakTable<ItemCatalog, List<ItemCatalog.BaseType>> _slots = new();

    /// <summary>Whether some kind of item that drops (and the jewels) takes the modifier by the mode's rule.</summary>
    bool Placeable(ItemCatalog catalog, Source source, string mode)
    {
        var known = _placeable.GetValue(catalog, _ => new());
        lock (known)
            if (!known.TryGetValue((source, mode), out bool placeable))
                known[(source, mode)] = placeable = Slots(catalog).Any(t => Rule(source, t, mode));
            else return placeable;
        return known[(source, mode)];
    }

    /// <summary>One base of each kind of item a monster drops, and of each jewel.</summary>
    List<ItemCatalog.BaseType> Slots(ItemCatalog catalog) => _slots.GetValue(catalog, c =>
    {
        var slots = new List<ItemCatalog.BaseType>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int row = 0; c.Row(row) is { } t; row++)
            if (Target(t) && (JewelClasses.Contains(t.Class) || ItemDrops.EquipmentClasses.Contains(t.Class) && ItemDrops.Droppable(t)) && seen.Add(t.Class))
                slots.Add(t);
        return slots;
    });

    // One by weight: a modifier of a rarer unique comes up that much less often. Without the setting, every one alike.
    Source Pick(List<Source> pool, Random rng, out int index)
    {
        index = rng.Next(pool.Count);
        if (!_choices.Implicits.RarityWeights) return pool[index];
        double at = rng.NextDouble() * pool.Sum(s => s.Weight);
        for (int i = 0; i < pool.Count; i++)
            if ((at -= pool[i].Weight) < 0) { index = i; break; }
        return pool[index];
    }

    // ---- the three effects ----

    // How many implicits a drop gets: the rarest roll that succeeds, three before two before one.
    int ImplicitsRolled(Random rng) =>
        rng.Next(Math.Max(1, _choices.Implicits.ChanceThree)) == 0 ? 3
        : rng.Next(Math.Max(1, _choices.Implicits.ChanceTwo)) == 0 ? 2
        : rng.Next(Math.Max(1, _choices.Implicits.Chance)) == 0 ? 1 : 0;

    Item? WithUniqueImplicits(ClientState conn, Item item, ItemCatalog.BaseType type, int count, Random rng, List<string> log)
    {
        var catalog = Game.ItemsFor(conn);
        var have = item.Mods.Select(m => m.Hash).ToHashSet();
        var pool = Pool(catalog, Game.UniquesFor(conn), Game.DropsFor(conn)).Where(s => !have.Contains(s.Mod.Hash) && Fits(catalog, s, type)).ToList();
        var mods = item.Mods.ToList();
        var families = new HashSet<int>(item.Mods.SelectMany(m => catalog.Mods.ByHash(m.Hash)?.Families ?? []));
        int added = 0;
        while (added < count && pool.Count > 0)
        {
            var source = Pick(pool, rng, out int pick);
            pool.RemoveAt(pick);
            // Two modifiers of one family would be the same line twice.
            if (source.Mod.Families.Any(families.Contains)) continue;
            foreach (int family in source.Mod.Families) families.Add(family);
            var rolled = source.Mod.Roll(ModSection.Implicit, rng, stat => source.Ranges.TryGetValue(stat, out var range) ? range : null);
            int at = mods.FindLastIndex(m => m.Section == ModSection.Implicit) + 1;
            if (at == 0) at = mods.FindIndex(m => m.Section == ModSection.Explicit) is int first and >= 0 ? first : mods.Count;
            mods.Insert(at, rolled);
            log.Add($"implicit \"{catalog.Mods.Describe(rolled)}\" (from {source.Unique})");
            added++;
        }
        return added == 0 ? null : item with { Mods = mods };
    }

    /// <summary>One implicit or explicit modifier (a unique implicit this mod added included), every value of it better
    /// than it is by the chosen percent. A modifier with a downside or a value that picks something (a granted gem, a
    /// seed) is not chosen.</summary>
    Item? WithEmpowered(ClientState conn, Item item, Random rng, List<string> log)
    {
        var mods = Game.ItemsFor(conn).Mods;
        var own = Game.UniquesFor(conn).RollsOf(item);
        // A value its modifier cannot roll otherwise (a fixed 1) is a switch: "has no sockets", "cannot leech".
        bool Switch(ModCatalog.Mod mod, ModSection section, int stat, int value) =>
            Math.Abs(value) == 1 && (own?.Invoke(mod, section, stat) ?? mod.RangeAt(stat)) is var (min, max) && min == max;
        var candidates = item.Mods.Select((m, i) => (m, i)).Where(p => p.m.Section is ModSection.Explicit or ModSection.Implicit && mods.ByHash(p.m.Hash) is { } mod
            && p.m.Values.Any(v => v != 0)
            && p.m.Values.Select((v, s) => s < mod.Stats.Count && !PicksSomething(mod.Stats[s].Id) && !Switch(mod, p.m.Section, s, v)
                && (v == 0 || (v > 0) != LessIsBetter(mod.Stats[s].Id))).All(ok => ok)).ToList();
        if (candidates.Count == 0) return null;
        var (chosen, index) = candidates[rng.Next(candidates.Count)];
        int percent = rng.Next(Low(_choices), High(_choices) + 1);
        var values = chosen.Values.Select(v => v == 0 ? 0 : (int)Math.Clamp(Math.Round(v * (1 + percent / 100.0), MidpointRounding.AwayFromZero) is var x && x == v ? v + Math.Sign(v) : x, int.MinValue, int.MaxValue)).ToList();
        var changed = item.Mods.ToList();
        changed[index] = chosen with { Values = values };
        log.Add($"empowered +{percent}%: \"{mods.Describe(chosen)}\" -> {string.Join(", ", values)}");
        return item with { Mods = changed };
    }

    /// <summary>One modifier more than the item could have, and its side fractured: a rare (or magic item) is first
    /// filled to its most prefixes and suffixes, then gets one more of a side; a unique gets one rare-style prefix or
    /// suffix. The fractured modifiers go to the front of the explicits, where the client reads them.</summary>
    Item? WithExtraModifier(ClientState conn, Item item, ItemCatalog.BaseType type, Random rng, List<string> log)
    {
        if (item.FracturedCount > 0) return null;
        var catalog = Game.ItemsFor(conn);
        var rolls = Game.RollsFor(conn);
        const int Prefix = ItemRolls.Prefix, Suffix = ItemRolls.Suffix;
        int SideOf(ItemMod m) => catalog.Mods.ByHash(m.Hash)?.GenerationType ?? 0;
        int OnSide(Item it, int side) => it.Mods.Count(m => m.Section == ModSection.Explicit && SideOf(m) == side);
        // The pool may take in one influence's modifiers (not the item's own), as an item with that influence would roll
        // them; the item does not get the influence.
        var influences = Enum.GetValues<ItemInfluence>().Where(i => i != ItemInfluence.None && !item.Influences.HasFlag(i)).ToList();
        var influence = _choices.ExtraModifier.InfluencedMods && influences.Count > 0 ? influences[rng.Next(influences.Count)] : ItemInfluence.None;
        // One more affix as the server adds one (ItemRolls.AffixesToAdd: the base's pool at the item level, its families
        // and metamods), but drawn without the blacklist and on the side asked for.
        Item? AddOne(Item it, (int Prefix, int Suffix) max, int? side)
        {
            var probe = influence == ItemInfluence.None ? it : it with { Influences = it.Influences | influence };
            var open = rolls.AffixesToAdd(probe, max).Where(p => p.Weight > 0 && !Blacklisted(p.Mod) && (side is null || p.Mod.GenerationType == side)).ToList();
            if (open.Count == 0) return null;
            int at = rng.Next(open.Sum(p => p.Weight));
            var mod = open.First(p => (at -= p.Weight) < 0).Mod;
            return it with { Mods = [.. it.Mods, mod.Roll(ModSection.Explicit, rng)] };
        }
        var now = item;
        int before = now.Mods.Count;
        // A rare (or magic item) is first filled to its most prefixes and suffixes.
        if (now.Rarity is ItemRarity.Rare or ItemRarity.Magic)
        {
            var most = now.Rarity == ItemRarity.Rare ? rolls.RareMaxAffixes(now) : (1, 1);
            while (AddOne(now, most, null) is { } filled) now = filled;
        }
        int filledBy = now.Mods.Count - before;
        int first = rng.Next(2) == 0 ? Prefix : Suffix;
        Item? Beyond(int side) => AddOne(now, (OnSide(now, Prefix) + (side == Prefix ? 1 : 0), OnSide(now, Suffix) + (side == Suffix ? 1 : 0)), side);
        if ((Beyond(first) ?? Beyond(first == Prefix ? Suffix : Prefix)) is not { } extra) return null;
        var added = extra.Mods[^1];
        int chosenSide = SideOf(added);
        // A unique's own modifiers have no side: only the one added is fractured there.
        bool Fractured(ItemMod m) => m.Section == ModSection.Explicit && (item.Rarity == ItemRarity.Unique ? ReferenceEquals(m, added) : SideOf(m) == chosenSide);
        // The fractured modifiers go to the front of the explicits, where the client reads them.
        var ordered = extra.Mods.Where(m => m.Section != ModSection.Explicit)
            .Concat(extra.Mods.Where(Fractured)).Concat(extra.Mods.Where(m => m.Section == ModSection.Explicit && !Fractured(m))).ToList();
        log.Add($"extra {(chosenSide == Prefix ? "prefix" : "suffix")} \"{catalog.Mods.Describe(added)}\"" +
            (influence == ItemInfluence.None ? "" : $" ({influence} modifiers in the pool)") +
            (filledBy > 0 ? $" after {filledBy} to fill the item" : "") + $", {ordered.Count(Fractured)} fractured");
        return extra with { Mods = ordered, FracturedCount = (byte)ordered.Count(Fractured), RequiredLevel = null };
    }

    /// <summary>A stat whose value says which one and not how much: a granted support gem and its socket, a Timeless
    /// Jewel's seed, conqueror and version, Thread of Hope's ring, a veiled modifier's seed.</summary>
    static bool PicksSomething(string stat) => stat.Contains("_index", StringComparison.Ordinal)
        || stat.StartsWith("local_unique_jewel_alternate_tree_", StringComparison.Ordinal)
        || stat is "local_jewel_variable_ring_radius_value" or "veiled_mod_seed";

    /// <summary>A stat where less is better: damage the character takes, what a skill costs, flask charges used,
    /// requirements, and ailments, curses and self-damage on the character. Told by the stat's name.</summary>
    static bool LessIsBetter(string stat) => LowerIsBetter.IsMatch(stat) && !OnEnemies.IsMatch(stat);

    static readonly Regex LowerIsBetter = new(
        @"damage_taken_(from_hits_|over_time_|from_crit_)?\+|_cost_\+|charges_used_\+|requirements?_\+"
        + @"|^(local_)?self_.*(duration|effect)_\+%|(curse|chill|shocked)_effect(iveness)?_on_self_\+%|ailment_on_self_duration_\+%"
        + @"|against_self|^self_.*damage_on_.*skill_use|life_taken_per_minute|reservation_efficiency_-2%_per_1|^-%_of_your",
        RegexOptions.CultureInvariant);

    // "Enemies take increased damage" is damage taken too, and there more is better.
    static readonly Regex OnEnemies = new(@"enem(y|ies).*damage_taken|ailment_types|chilling_areas", RegexOptions.CultureInvariant);

    /// <summary>The drop with whatever effects come up (or were forced), or null with none.</summary>
    Item? Excite(ClientState conn, Item item, Random rng, Forced? forced, out string effects, List<string> log)
    {
        effects = "";
        var c = _choices;
        if (item.Rarity is not (ItemRarity.Magic or ItemRarity.Rare or ItemRarity.Unique) || item.Mirrored || item.Corrupted
            || Game.ItemsFor(conn).ByHash(item.TypeHash) is not { } type || !Target(type)) return null;
        bool extra = forced?.Extra ?? (Takes(c.ExtraModifier.Rarities, item.Rarity) && rng.Next(Math.Max(1, c.ExtraModifier.Chance)) == 0);
        bool empowered = forced?.Empowered ?? (Takes(c.Empowered.Rarities, item.Rarity) && rng.Next(Math.Max(1, c.Empowered.Chance)) == 0);
        int implicits = forced?.Implicits ?? (Takes(c.Implicits.Rarities, item.Rarity) ? ImplicitsRolled(rng) : 0);
        if (!extra && !empowered && implicits == 0) return null;
        var now = item;
        bool gotExtra = false, gotEmpowered = false; int gotImplicits = 0;
        if (extra && WithExtraModifier(conn, now, type, rng, log) is { } withExtra) { now = withExtra; gotExtra = true; }
        if (implicits > 0 && WithUniqueImplicits(conn, now, type, implicits, rng, log) is { } withImplicits)
        {
            gotImplicits = withImplicits.Mods.Count(m => m.Section == ModSection.Implicit) - now.Mods.Count(m => m.Section == ModSection.Implicit);
            now = withImplicits;
        }
        // Last, so it can land on any of them, a unique implicit just added included.
        if (empowered && WithEmpowered(conn, now, rng, log) is { } withEmpowered) { now = withEmpowered; gotEmpowered = true; }
        if (ReferenceEquals(now, item)) return null;
        effects = Effects(gotImplicits, gotEmpowered, gotExtra);
        return now with { Synthesised = c.Mark || now.Synthesised };
    }

    async Task Scan(ClientState conn)
    {
        var ground = conn.Instance.Ground;
        var watch = _watches.GetValue(ground, _ => new Watch());
        var found = new List<string>();
        lock (watch)
        {
            // What already lay there when the mod first saw this ground is left alone.
            if (!watch.Started)
            {
                var all = ground.All();
                watch.Started = true; watch.Next = all.Count == 0 ? InstanceGround.FirstItemId : all.Max(e => e.Id) + 1;
                return;
            }
            // Ground ids count up: the new ones are those from Next until the first id never given out (neither on the
            // ground nor taken), so a tick looks only at what dropped since the last one.
            var fresh = new List<(uint Id, WorldItemObject Item)>();
            uint end = watch.Next;
            for (; ; end++)
            {
                var lying = ground.Get(end);
                if (lying is null && !ground.WasTaken(end)) break;
                if (lying is not null) fresh.Add((end, lying));
            }
            watch.Next = end;
            foreach (var (id, lying) in fresh)
            {
                // An item a player put down was a drop once already: dropping it again must not roll it again.
                if (watch.Ours.Remove(id) || lying.PlayerDropped
                    || lying.Item.Rarity is not (ItemRarity.Magic or ItemRarity.Rare or ItemRarity.Unique)) continue;
                var forced = Volatile.Read(ref _forced) is { } f && ReferenceEquals(f.For, conn) ? f : null;
                var log = new List<string>();
                if (Excite(conn, lying.Item, Random.Shared, forced, out string effects, log) is not { } changed) continue;
                var (by, shared) = ground.Origin(id);
                // Only one caller gets the item, so a pickup in the same moment cannot duplicate it.
                if (ground.Take(id, lying) is null) continue;
                watch.Ours.Add(ground.Add(lying with { Item = changed }, by, shared));
                if (forced is not null) Interlocked.CompareExchange(ref _forced, null, forced);
                string name = Game.ItemsFor(conn).ByHash(changed.TypeHash)?.Name ?? "item";
                found.Add($"{changed.Rarity} {name} with {effects}");
                _log($"{changed.Rarity} {name} at ({lying.GridX}, {lying.GridY}): {string.Join("; ", log)}");
            }
        }
        if (!_choices.Announce) return;
        foreach (string text in found) await Program.PlayerOf(conn).Tell("* Exciting item dropped: " + text);
    }

    // ---- loot filters ----

    // The rule goes at the top of every loot filter in the folder, between its two marker lines, or comes out again.
    // Off by default: it edits the player's own filters, so only when the player turns it on.
    static string Filters(Choices choices)
    {
        string folder = choices.FilterFolder.Trim().Length > 0 ? choices.FilterFolder.Trim()
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Path of Exile", "LocalExile");
        if (!Directory.Exists(folder)) return $"no loot filter folder at {folder}";
        bool wanted = choices.ShowInFilters && choices.Mark;
        string own = Path.Combine(folder, OwnFilter);
        var filters = Directory.GetFiles(folder, "*.filter").Where(f => !f.Equals(own, StringComparison.OrdinalIgnoreCase)).ToList();
        int changed = 0;
        string[] block = [FilterStart, .. FilterRule, FilterEnd];
        static bool Cut(List<string> lines, string start, string end)
        {
            int from = lines.FindIndex(l => l.Trim() == start), to = lines.FindIndex(l => l.Trim() == end);
            if (from < 0 || to < from) return false;
            lines.RemoveRange(from, to - from + 1);
            if (from < lines.Count && lines[from].Trim().Length == 0) lines.RemoveAt(from);
            return true;
        }
        foreach (string file in filters)
        {
            var lines = File.ReadAllLines(file).ToList();
            var before = string.Join('\n', lines);
            Cut(lines, FilterStart, FilterEnd);
            if (wanted) lines.InsertRange(0, block.Append(""));
            if (string.Join('\n', lines) == before) continue;
            File.WriteAllLines(file, lines);
            changed++;
        }
        // With no filter of the player's own, one that holds only the rule: it hides nothing.
        if (wanted && filters.Count == 0 && (!File.Exists(own) || !File.ReadAllLines(own).SequenceEqual(block))) { File.WriteAllLines(own, block); changed++; }
        if (!wanted && File.Exists(own)) { File.Delete(own); changed++; }
        return $"loot filters in {folder}: {filters.Count} found, {changed} changed (rule {(wanted ? "on" : "off")})";
    }
}
