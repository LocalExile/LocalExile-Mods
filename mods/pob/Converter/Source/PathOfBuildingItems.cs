// Adapted from LocalExile feature/pob-import, 5394de0a. See NOTICE.md.
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameServer.Core.Imports;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// Builds real, serializable items from the selected PoB set. Every modifier is resolved to this client's Mods.dat
/// and exact values. Unsupported features are omitted with warnings; invalid native records are never saved.
/// </summary>
static class PathOfBuildingItems
{
    public sealed record Result(Dictionary<byte, List<CharacterSaves.SavedPlacement>> Inventories,
        IReadOnlyList<string> Warnings, Dictionary<int, string> SkillBar);

    sealed record Equipped(byte Row, byte X, ItemCatalog.BaseType Type, Item Item, string Slot);
    sealed record ParsedMod(string Text, ModSection Section, bool Fractured, bool Crafted, bool Derived, double Range,
        bool Enabled = true, bool HasInlineRange = false);
    sealed record StatValue(string Id, int Value);
    static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    static readonly Regex Tags = new(@"\{([^}:]+)(?::([^}]+))?\}", RegexOptions.CultureInvariant);
    static readonly Regex Ranges = new(@"\((-?\d+(?:\.\d+)?)-(-?\d+(?:\.\d+)?)\)", RegexOptions.CultureInvariant);
    static readonly Dictionary<string, byte> Equipment = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Body Armour"] = 1, ["Weapon 1"] = 2, ["Weapon 2"] = 3, ["Helmet"] = 4,
        ["Amulet"] = 5, ["Ring 1"] = 6, ["Ring 2"] = 7, ["Ring 3"] = 102, ["Gloves"] = 8, ["Boots"] = 9,
        ["Belt"] = 10, ["Weapon 1 Swap"] = 14, ["Weapon 2 Swap"] = 15,
    };

    /// <param name="finish">Completes a parsed item from outside the build, such as the server's unique catalogue. It gets
    /// that item's own warnings, to replace any it makes obsolete.</param>
    public static Result Materialize(PathOfBuildingBuild build, ItemCatalog catalog, string folder = "data",
        Func<PathOfBuildingItem, ItemCatalog.BaseType, Item, List<string>, Item>? finish = null)
    {
        if (catalog.Error is { } unavailable) throw new ArgumentException(unavailable);
        var errors = new List<string>();
        var warnings = new List<string>();
        var equipped = new Dictionary<string, Equipped>(StringComparer.OrdinalIgnoreCase);
        var definitions = build.Items.ToDictionary(i => i.Id);
        foreach (var slot in build.Slots.Where(s => s.ItemId != 0))
        {
            try
            {
                if (!definitions.TryGetValue(slot.ItemId, out var definition))
                    throw new ArgumentException($"item id {slot.ItemId} is missing");
                byte row, x = 0;
                if (Equipment.TryGetValue(slot.Name, out byte equipment)) row = equipment;
                else if (Regex.Match(slot.Name, @"^Flask ([1-5])$", RegexOptions.IgnoreCase) is { Success: true } flask)
                { row = PlayerInventories.Flask; x = (byte)(flask.Groups[1].Value[0] - '1'); }
                else throw new PathOfBuildingUnsupportedException("this equipment slot is not implemented by the server");
                var own = new List<string>();
                var (type, item) = ParseItem(definition, catalog, folder, own);
                if (finish is not null) item = finish(definition, type, item, own);
                warnings.AddRange(own);
                if (!PlayerInventories.Accepts(row, type.Class))
                    throw new ArgumentException($"{type.Name} ({type.Class}) cannot be equipped in this slot");
                if (!equipped.TryAdd(slot.Name, new(row, x, type, item, slot.Name)))
                    throw new ArgumentException("the selected item set repeats this slot");
            }
            catch (PathOfBuildingUnsupportedException e) { warnings.Add($"Skipped item in {slot.Name}: {e.Message}."); }
            catch (ArgumentException e) { errors.Add($"{slot.Name}: {e.Message}"); }
        }
        ValidateHands(equipped, errors);

        var skillBar = new Dictionary<int, string> { [0] = "Action/Move" };
        var effectIds = LoadEffectIds(folder);
        var offsets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var occupiedLinks = new HashSet<(string Slot, int Start)>();
        int key = 1;
        foreach (var group in build.Skills)
        {
            if (group.Slot is { } physicalSlot && equipped.TryGetValue(physicalSlot, out var physical) && physical.Item.NativeSockets)
            {
                bool swapped = build.UseSecondWeaponSet ? physical.Row is 2 or 3 : physical.Row is 14 or 15;
                if (!swapped && group.Enabled && string.IsNullOrWhiteSpace(group.Source))
                    foreach (var gem in physical.Item.Sockets.Select(s => s.Gem).OfType<Item>())
                        foreach (int effect in catalog.GemEffects(gem))
                            if (key <= 12 && effect >= 0 && effect < effectIds.Length && effectIds[effect] is { Length: > 0 } id
                                && !skillBar.ContainsValue(id)) skillBar[key++] = id;
                continue;
            }
            if (NativeSource.Available && (group.Slot is null || !equipped.ContainsKey(group.Slot)))
            {
                if (string.IsNullOrWhiteSpace(group.Source) && group.Gems.Count > 0)
                    warnings.Add($"PoB-only skill group in {group.Slot ?? "unspecified slot"}: the matching native snapshot contains no equipped host.");
                continue;
            }
            int offset = 0;
            if (group.Slot is { } slot)
            {
                offset = offsets.GetValueOrDefault(slot);
                offsets[slot] = offset + group.Gems.Count;
            }
            // PoB's enabled flag controls its calculations, not whether a physical gem is socketed.
            var selected = group.Gems.ToArray();
            if (selected.Length == 0) continue;
            try
            {
                if (!string.IsNullOrWhiteSpace(group.Source))
                    throw new PathOfBuildingUnsupportedException($"item-granted skill source '{group.Source}' cannot be imported as socketed gems");
                if (!string.IsNullOrWhiteSpace(group.ImbuedSupport))
                    throw new PathOfBuildingUnsupportedException("imbued support gems are not implemented");
                if (group.Slot is not { Length: > 0 } slotName || !equipped.TryGetValue(slotName, out var host))
                    throw new PathOfBuildingUnsupportedException("its equipment slot is unspecified, unsupported or was omitted");
                var gems = group.Gems.Select(g =>
                {
                    try { return CreateGem(g, catalog, folder); }
                    catch (PathOfBuildingUnsupportedException e)
                    { warnings.Add($"Skipped gem '{g.Name}' in {slotName}: {e.Message}."); return null; }
                }).ToArray();
                if (gems.All(g => g is null)) continue;
                var sockets = host.Item.Sockets.ToArray();
                var links = host.Item.Links ?? [.. Enumerable.Repeat((byte)1, sockets.Length)];
                int at = 0, linkStart = -1;
                foreach (int size in links)
                {
                    if (offset >= at && offset + gems.Length <= at + size) { linkStart = at; break; }
                    at += size;
                }
                if (offset >= sockets.Length)
                    throw new PathOfBuildingUnsupportedException($"{host.Type.Name} has no free socket for this group");
                if (offset + gems.Length > sockets.Length)
                    throw new PathOfBuildingUnsupportedException($"{host.Type.Name} has insufficient sockets for all {gems.Length} gems in this group");
                for (int i = 0; i < gems.Length; i++)
                {
                    if (gems[i] is not null && sockets[offset + i].Colour == SocketColour.Abyss)
                        throw new ArgumentException("a skill gem cannot be placed in an Abyss socket");
                    sockets[offset + i] = sockets[offset + i] with { Gem = gems[i] };
                }
                equipped[slotName] = host with { Item = host.Item with { Sockets = sockets } };
                bool inactiveWeapon = host.Row is 14 or 15;
                if (build.UseSecondWeaponSet) inactiveWeapon = host.Row is 2 or 3;
                if (!inactiveWeapon && group.Enabled)
                    foreach (var gem in gems.OfType<Item>())
                        foreach (int effect in catalog.GemEffects(gem))
                            if (key <= 12 && effect >= 0 && effect < effectIds.Length && effectIds[effect] is { Length: > 0 } id
                                && !skillBar.ContainsValue(id)) skillBar[key++] = id;
            }
            catch (PathOfBuildingUnsupportedException e) { warnings.Add($"Skipped skill group in {group.Slot ?? "unspecified slot"}: {e.Message}."); }
            catch (ArgumentException e) { errors.Add($"Skills ({group.Slot ?? "unspecified slot"}): {e.Message}"); }
        }
        if (errors.Count > 0)
            throw new ArgumentException("PoB import contains invalid item or gem data:\n" + string.Join("\n", errors.Select(e => "- " + e)));

        var inventories = new Dictionary<byte, List<CharacterSaves.SavedPlacement>>();
        uint next = 1;
        foreach (var entry in equipped.Values)
        {
            byte row = entry.Row;
            if (build.UseSecondWeaponSet) row = row switch { 2 => 14, 3 => 15, 14 => 2, 15 => 3, _ => row };
            if (!inventories.TryGetValue(row, out var placements)) inventories[row] = placements = [];
            placements.Add(new(next++, entry.X, 0, CharacterSaves.ToSaved(entry.Item, catalog.Mods)));
        }
        return new(inventories, warnings.Distinct().ToArray(), skillBar);
    }

    static readonly Dictionary<string, string[]> EffectCache = new(StringComparer.OrdinalIgnoreCase);
    static string[] LoadEffectIds(string folder)
    {
        if (EffectCache.TryGetValue(folder, out var cached)) return cached;
        string path = Path.Combine(folder, "tables", "GrantedEffects.json");
        if (!NativeData.Exists(path)) return [];
        using var doc = NativeData.Read(path);
        return EffectCache[folder] = [.. doc.RootElement.EnumerateArray().Select(r => r.GetProperty("Id").GetString() ?? "")];
    }

    static void ValidateHands(Dictionary<string, Equipped> items, List<string> errors)
    {
        foreach (string suffix in new[] { "", " Swap" })
        {
            items.TryGetValue("Weapon 1" + suffix, out var main);
            items.TryGetValue("Weapon 2" + suffix, out var off);
            if (off is null) continue;
            bool twoHand = main?.Type.Class is "Staff" or "Warstaff" or "Two Hand Sword" or "Two Hand Axe" or "Two Hand Mace" or "FishingRod";
            if (twoHand || main?.Type.Class == "Bow" && off.Type.Class != "Quiver"
                || off.Type.Class == "Quiver" && main is not null && main.Type.Class != "Bow")
                errors.Add($"Weapon set{suffix}: {main?.Type.Name ?? "empty main hand"} and {off.Type.Name} cannot be equipped together");
        }
    }

    internal static Item CreateGem(PathOfBuildingGem gem, ItemCatalog catalog, string folder = "data") =>
        NativeGemCache.Create(gem, catalog, folder, () => CreateGemCore(gem, catalog, folder));

    static Item CreateGemCore(PathOfBuildingGem gem, ItemCatalog catalog, string folder)
    {
        var matches = NativeGemCache.Obtainable(catalog)
            .Where(x => string.IsNullOrWhiteSpace(gem.Name) || string.Equals(x.Variant.Name, gem.Name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(x.Variant.Name, gem.Name + " Support", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (gem.GemId is { Length: > 0 } id)
        {
            var byId = matches.Where(x => string.Equals(x.Type.Id, id, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (byId.Length == 0) throw new PathOfBuildingUnsupportedException($"gem '{gem.Name}' does not match '{id}' in this client's obtainable gem catalogue");
            matches = byId;
        }
        if (gem.VariantId is { Length: > 0 } variantId) matches = matches.Where(x => x.Variant.Id == variantId).ToArray();
        var effects = LoadEffectIds(folder);
        if (gem.SkillId is { Length: > 0 } requiredSkill)
            matches = matches.Where(x => new[] { x.Variant.Effect, x.Variant.Effect2 }
                .Any(e => e >= 0 && e < effects.Length && effects[e] == requiredSkill)).ToArray();
        if (matches.Length == 0)
            throw new PathOfBuildingUnsupportedException($"gem '{gem.Name}' is unavailable in this client's obtainable gem catalogue");
        if (matches.Length > 1)
            matches = [matches[0]];
        var match = matches[0];
        var type = catalog.Gem(match.Type)!;
        if (gem.QualityId is { Length: > 0 } qualityId && qualityId != "Default")
            throw new PathOfBuildingUnsupportedException($"{gem.Name} uses unsupported historical quality '{qualityId}'");
        if (gem.Level < 1 || gem.Level > type.MaxLevel + 1)
            throw new ArgumentException($"{gem.Name} level {gem.Level} is outside the representable range 1..{type.MaxLevel + 1}");
        if (gem.Quality is < 0 or > 23)
            throw new ArgumentException($"{gem.Name} quality {gem.Quality} is outside the representable range 0..23");
        return ItemCatalog.Create(match.Type, defenceBaseRoll: 0) with
        {
            GemVariant = match.Variant.Index, GemExperience = gem.Level > type.MaxLevel ? -1 : type.ExperienceFor(gem.Level),
            Quality = (byte)gem.Quality, Corrupted = type.IsVaal || gem.Level > type.MaxLevel || gem.Quality > 20,
        };
    }

    internal static (ItemCatalog.BaseType Type, Item Item) ParseItem(PathOfBuildingItem definition,
        ItemCatalog catalog, string folder, List<string> warnings)
    {
        if (definition.HasAlternateVariants) throw new PathOfBuildingUnsupportedException("alternate item variants are not implemented");
        var lines = definition.Text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l != "--------").ToArray();
        int pos = lines.FirstOrDefault()?.StartsWith("Item Class:", StringComparison.Ordinal) == true ? 1 : 0;
        bool relicItem = pos < lines.Length && lines[pos] == "Rarity: RELIC";
        if (pos >= lines.Length || !lines[pos].StartsWith("Rarity: ", StringComparison.Ordinal)
            || !Enum.TryParse<ItemRarity>(relicItem ? "Unique" : lines[pos][8..], true, out var rarity) || !Enum.IsDefined(rarity))
            throw new ArgumentException("item text must start with a supported Rarity: NORMAL, MAGIC, RARE or UNIQUE header");
        pos++;
        if (pos >= lines.Length) throw new ArgumentException("the item has no base name");
        string? title = rarity is ItemRarity.Rare or ItemRarity.Unique ? lines[pos++] : null;
        if (pos >= lines.Length) throw new ArgumentException("the item has no base name");
        string baseName = lines[pos++];
        var ordinary = NativeBaseIndex.All(catalog);
        // Vestigial is a display prefix, not a different base. Keep PoB's disambiguated
        // transformed weapon names: the API calls both an Energy Blade weapon and its gem "Energy Blade".
        if (NativeSource.BaseName(definition) is { } nativeBase
            && (rarity == ItemRarity.Magic || baseName == "Vestigial " + nativeBase)) baseName = nativeBase;
        var bases = NativeBaseIndex.Named(catalog, baseName);
        if (bases.Length == 0 && System.Text.RegularExpressions.Regex.Match(baseName, @"^(.+?) \([^)]*\)$") is { Success: true } plainBase)
            bases = ordinary.Where(t => string.Equals(t.Name, plainBase.Groups[1].Value, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (baseName is "Energy Blade One Handed" or "Energy Blade Two Handed")
            bases = ordinary.Where(t => t.Id.EndsWith(baseName == "Energy Blade One Handed" ? "/StormBladeOneHand" : "/StormBladeTwoHand", StringComparison.Ordinal)).ToArray();
        if (bases.Length == 0) bases = ordinary.Where(t => string.Equals(Plain(t.Name), Plain(baseName), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (bases.Length == 0 && rarity == ItemRarity.Magic)
        {
            bases = ordinary.Where(t => baseName.Equals(t.Name, StringComparison.OrdinalIgnoreCase)
                || baseName.Contains(" " + t.Name + " ", StringComparison.OrdinalIgnoreCase)
                || baseName.StartsWith(t.Name + " ", StringComparison.OrdinalIgnoreCase)
                || baseName.EndsWith(" " + t.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (bases.Length > 0) bases = bases.Where(t => t.Name.Length == bases.Max(b => b.Name.Length)).ToArray();
        }
        if (bases.Length == 0)
            throw new PathOfBuildingUnsupportedException($"base '{baseName}' is unknown or unsupported in this client's data");
        if (bases.Length > 1)
        {
            bases = NativeSource.IdentifyBases(definition, bases, folder);
            // Two-Toned Boots have three distinct native bases with the same display name.
            // Their actual defence families identify the base even when corruption replaced its implicits.
            var defenceKinds = NativeSource.Defences(definition);
            if (defenceKinds is not null)
            {
                var matching = bases.Where(b => catalog.Defences(b) is { } d
                    && (!defenceKinds.ContainsKey("Armour") || d.ArmourMax > 0)
                    && (!defenceKinds.ContainsKey("Evasion") || d.EvasionMax > 0)
                    && (!defenceKinds.ContainsKey("EnergyShield") || d.EnergyShieldMax > 0)).ToArray();
                if (matching.Length > 0) bases = matching;
            }
            if (bases.Length > 1) throw new ArgumentException($"The native base '{baseName}' is ambiguous.");
        }
        var type = bases[0];
        var item = ItemCatalog.Create(type, defenceBaseRoll: Item.MaximumDefenceBaseRoll) with
            { Rarity = rarity, Mods = [], RelicStyle = relicItem ? (byte)0 : null };
        var rawMods = new List<ParsedMod>();
        var directMods = new List<(ItemMod Mod, bool Fractured)>();
        int implicitLines = 0, selectedVariant = definition.Variant ?? 1;
        string? catalyst = null; int? catalystQuality = null;
        bool hasImplicitMarker = false;
        int? exportedLevelRequirement = null;
        var percentile = new List<double>();
        var defenceDisplay = new Dictionary<string, int>();
        foreach (string raw in lines.Skip(pos))
        {
            if (definition.Variant is null && raw.StartsWith("Selected Variant: ", StringComparison.Ordinal))
            { selectedVariant = Number(raw[18..], "selected variant", 1, 10000); continue; }
        }
        foreach (string raw in lines.Skip(pos))
        {
            if (raw == "Corrupted") { item = item with { Corrupted = true }; continue; }
            if (raw == "Mirrored") { item = item with { Mirrored = true }; continue; }
            if (raw.StartsWith("Requires Class ", StringComparison.Ordinal))
            {
                // Exact native modifier IDs already encode this derived restriction.
                if (NativeSource.HasExactMods(definition)) continue;
                // Forbidden jewels derive this tooltip line from the selected native ascendancy notable.
                var allocated = Regex.Match(definition.Text, @"Allocates (.+?) if you have the matching modifier on Forbidden (?:Flame|Flesh)");
                if (title is not ("Forbidden Flame" or "Forbidden Flesh") || !allocated.Success)
                    throw new ArgumentException("A class restriction needs a native Forbidden jewel allocation.");
                var classes = NativeData.Shared("forbidden-class-restrictions", () =>
                {
                    using var characters = NativeData.Read(Path.Combine(folder, "tables", "Characters.json"));
                    using var ascendancies = NativeData.Read(Path.Combine(folder, "tables", "Ascendancy.json"));
                    using var passives = NativeData.Read(Path.Combine(folder, "tables", "PassiveSkills.json"));
                    var names = characters.RootElement.EnumerateArray().Select(r => r.GetProperty("Name").GetString()!).ToArray();
                    var asc = ascendancies.RootElement.EnumerateArray().Select(r => names[r.GetProperty("Character").GetInt32()]).ToArray();
                    return passives.RootElement.EnumerateArray()
                        .Where(r => r.GetProperty("AscendancyKey").ValueKind == JsonValueKind.Number)
                        .GroupBy(r => r.GetProperty("Name").GetString()!)
                        .ToDictionary(g => g.Key, g => g.Select(r => asc[r.GetProperty("AscendancyKey").GetInt32()]).ToHashSet());
                });
                if (!classes.TryGetValue(allocated.Groups[1].Value, out var allowed) || !allowed.Contains(raw[15..]))
                    throw new ArgumentException("The exported class restriction disagrees with its native ascendancy notable.");
                continue;
            }
            if (raw.StartsWith("{vestigial}", StringComparison.Ordinal) && NativeSource.HasExactMods(definition))
            { item = item with { Vestigial = true }; continue; }
            if (raw is "Split" or "Fractured Item") continue;
            if (type.Components.Contains("Transformed") && Regex.Match(raw, @"^Has (\d+) Abyssal Sockets?$") is { Success: true } abyssCount)
            {
                if (item.Sockets.Count(s => s.Colour == SocketColour.Abyss) != int.Parse(abyssCount.Groups[1].Value, Invariant))
                    throw new ArgumentException("The Energy Blade abyss-socket label disagrees with its sockets.");
                continue;
            }
            if (raw.StartsWith("Rarity:", StringComparison.Ordinal)) throw new ArgumentException("multiple item records in one item definition");
            int colon = raw.IndexOf(':');
            string field = colon < 0 ? "" : raw[..colon], value = colon < 0 ? "" : raw[(colon + 1)..].Trim();
            if (field == "Item Level") { item = item with { ItemLevel = (byte)Number(value, field, 1, 100) }; continue; }
            if (field == "Quality") { item = item with { Quality = (byte)Number(value.TrimEnd('%'), field, 0, 255) }; continue; }
            if (field is "LevelReq" or "Requires Level")
            { exportedLevelRequirement = Number(value, field, 0, 100); continue; }
            if (field == "Sockets")
            {
                if (!Regex.IsMatch(value, @"^[RGBWA](?:-[RGBWA])*(?: +[RGBWA](?:-[RGBWA])*)*$", RegexOptions.CultureInvariant))
                    throw new ArgumentException("socket groups must be colour letters separated by single links, for example R-G-B W-W");
                var colours = new List<SocketColour>(); var links = new List<byte>();
                foreach (string word in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    int n = 0;
                    foreach (char c in word)
                        if (c != '-')
                        {
                            colours.Add(c switch { 'R' => SocketColour.Red, 'G' => SocketColour.Green, 'B' => SocketColour.Blue,
                                'W' => SocketColour.White, 'A' => SocketColour.Abyss, _ => throw new ArgumentException($"unknown socket colour '{c}'") });
                            n++;
                        }
                    if (n > 0) links.Add((byte)n);
                }
                if (colours.Count > CurrencyEffects.SocketCapacity(item, type) && !NativeSource.HasExactMods(definition) && !(type.Class is "Quiver" or "Ring" or "Amulet" or "Belt" && type.Components.Contains("Sockets") && colours.Count <= 3))
                {
                    if (type.Class is "Quiver" or "Ring" or "Amulet" or "Belt")
                        throw new PathOfBuildingUnsupportedException($"special sockets on {type.Name} are not implemented");
                    throw new ArgumentException($"{type.Name} cannot hold the exported {colours.Count} sockets");
                }
                item = item with { Sockets = [.. colours.Select(c => new ItemSocket(c))], Links = links };
                continue;
            }
            if (field is "ArmourBasePercentile" or "EvasionBasePercentile" or "EnergyShieldBasePercentile" or "WardBasePercentile")
            {
                if (!double.TryParse(value, NumberStyles.Float, Invariant, out double p) || !double.IsFinite(p) || p < 0 || p > 1)
                    throw new ArgumentException($"invalid {field}");
                percentile.Add(p); continue;
            }
            if (field == "Implicits") { implicitLines = Number(value, field, 0, 255); hasImplicitMarker = true; continue; }
            if (field is "Prefix" or "Suffix")
            {
                var (text, flags, ranges) = ReadTags(value, selectedVariant);
                if (flags.Contains("excluded")) continue;
                if (flags.Any(f => f != "fractured"))
                    throw new PathOfBuildingUnsupportedException($"unsupported affix flags in '{raw}'");
                if (text == "None") continue;
                var mod = catalog.Mods.ById(text) ?? throw new PathOfBuildingUnsupportedException($"modifier '{text}' is absent from this client");
                if (mod.GenerationType != (field == "Prefix" ? 1 : 2)) throw new ArgumentException($"{text} is not a {field.ToLowerInvariant()}");
                if (mod.Domain != type.ModDomain && mod.Domain != 9)
                    throw new ArgumentException($"{text} is not an item modifier for {type.Name}");
                if (ranges.Length > 1 && ranges.Any(r => r != ranges[0]))
                    throw new ArgumentException($"{text} has independent rolls which cannot be mapped safely to native stat order");
                double range = ranges.Length == 0 ? 0.5 : ranges[0];
                directMods.Add((new(ModSection.Explicit, mod.Hash,
                    [.. mod.Stats.Select(s => checked((int)Math.Floor(s.Min + (s.Max - (double)s.Min) * range + 0.5)))]), flags.Contains("fractured")));
                continue;
            }
            // Radius and Limited to restate a jewel's modifiers, which the client reads for itself.
            if (field is "Unique ID" or "League" or "Crafted" or "Variant" or "Selected Variant" or "Source" or "Upgrade"
                or "Requirements" or "Item Class" or "Radius" or "Limited to") continue;
            if (field is "Intangibility" or "Memory Strands")
            {
                byte amount = checked((byte)Number(value.TrimEnd('%'), field, 0, field == "Intangibility" ? 100 : 255));
                item = field == "Intangibility" ? item with { Intangibility = amount } : item with { MemoryStrands = amount };
                continue;
            }
            if (raw.StartsWith("Foil Unique (", StringComparison.Ordinal) && raw.EndsWith(')'))
            {
                string style = raw[13..^1];
                var styles = NativeData.Shared("relic-styles", () =>
                {
                    using var rows = NativeData.Read(Path.Combine(folder, "tables", "RelicItemEffectVariations.json"));
                    var result = rows.RootElement.EnumerateArray().Select((r, i) => (r, i))
                        .SelectMany(p => new[] { p.r.GetProperty("Id").GetString(), p.r.GetProperty("Description").GetString() }
                            .Where(s => !string.IsNullOrEmpty(s)).Select(s => (Name: s!, Value: checked((byte)p.i))))
                        .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);
                    // PoB calls the native Default_Foil variation "Rainbow" (ImportTab.lua).
                    result.Add("Rainbow", result["Default_Foil"]);
                    return result;
                });
                if (!styles.TryGetValue(style, out byte variant)) throw new ArgumentException($"Unknown native foil style '{style}'.");
                item = item with { RelicStyle = variant };
                continue;
            }
            if (raw.EndsWith(" Item", StringComparison.Ordinal) && Enum.TryParse<ItemInfluence>(raw[..^5], out var influence) && influence != ItemInfluence.None)
            { item = item with { Influences = item.Influences | influence }; continue; }
            // Eldritch influence is encoded by the actual implicit modifiers, not the six conqueror flags.
            if (raw is "Searing Exarch Item" or "Eater of Worlds Item") continue;
            if (field == "Catalyst") { catalyst = value; continue; }
            if (field == "CatalystQuality") { catalystQuality = Number(value, field, 0, 255); continue; }
            // Numeric armour lines are final display values. Native base percentiles, when exported, carry their state.
            if (field is "Armour" or "Evasion" or "Energy Shield" or "EnergyShield" or "Ward")
            { defenceDisplay[field.Replace(" ", "")] = Number(value, field, 0, int.MaxValue); continue; }
            if (field is "Version" or "Selected Version"
                or "Cluster Jewel Skill" or "Cluster Jewel Node Count" or "Unreleased"
                || raw.Contains(" Alt Variant", StringComparison.Ordinal) || raw.StartsWith("Selected Variant Group", StringComparison.Ordinal))
                throw new PathOfBuildingUnsupportedException($"unsupported item property '{raw}'");

            if (NativeSource.HasExactMods(definition)) { implicitLines--; continue; }
            var (line, tags, rangeValues) = ReadTags(raw, selectedVariant);
            // Implicits counts physical text lines, including lines from inactive variants.
            bool implicitSection = implicitLines-- > 0;
            if (tags.Contains("excluded"))
            {
                var excludedSection = tags.Contains("enchant") ? ModSection.Enchant : tags.Contains("scourge") ? ModSection.Scourge
                    : implicitSection || tags.Contains("implicit") ? ModSection.Implicit : ModSection.Explicit;
                rawMods.Add(new("", excludedSection, false, false, false, 1, false));
                continue;
            }
            if (tags.Any(t => t is not ("crafted" or "fractured" or "implicit" or "enchant" or "scourge" or "prefix" or "suffix"
                or "exarch" or "eater" or "mutated" or "crucible")))
                throw new PathOfBuildingUnsupportedException($"unsupported modifier flags in '{raw}'");
            if (rangeValues.Length > 1) throw new ArgumentException($"multiple independent ranges in '{raw}'");
            double lineRange = rangeValues.Length == 0 ? 1 : rangeValues[0];
            ModSection section = tags.Contains("enchant") ? ModSection.Enchant : tags.Contains("scourge") ? ModSection.Scourge
                : implicitSection || tags.Contains("implicit") ? ModSection.Implicit : ModSection.Explicit;
            rawMods.Add(new(line, section, tags.Contains("fractured"), tags.Contains("crafted"),
                tags.Contains("prefix") || tags.Contains("suffix"), lineRange, HasInlineRange: rangeValues.Length > 0));
        }
        if (implicitLines > 0 && !NativeSource.HasExactMods(definition)) throw new ArgumentException("the Implicits count exceeds the modifier lines");
        var mods = new List<(ItemMod Mod, bool Fractured)>(directMods);
        bool omittedModifiers = NativeSource.HasExactMods(definition);
        var unresolved = new List<(ParsedMod Line, string Reason)>();
        var translated = new List<(ParsedMod Line, ItemMod Mod)>();
        int modIndex = 0;
        // PoB ItemsTab:Load numbers the separate modifier lists, including inactive variant lines.
        foreach (var rawMod in rawMods.OrderBy(m => m.Section switch
            { ModSection.Enchant => 0, ModSection.Scourge => 1, ModSection.Implicit => 2, _ => 3 }))
        {
            double roll = definition.ModRanges.GetValueOrDefault(++modIndex, rawMod.Range);
            if (!rawMod.Enabled) continue;
            // Utility flasks prepend PoB-only buff lines to ModRange indices. Current exports carry explicit rolls;
            // legacy ranged text without those tags cannot be mapped safely without that separate PoB buff catalog.
            if (type.Components.Contains("Flask") && type.Class is not ("LifeFlask" or "ManaFlask" or "HybridFlask")
                && definition.ModRanges.Count > 0 && Ranges.IsMatch(rawMod.Text))
            {
                if (!rawMod.HasInlineRange)
                {
                    omittedModifiers = true;
                    warnings.Add($"Skipped modifier on {type.Name}: legacy utility-flask roll needs an explicit range tag in its export.");
                    continue;
                }
                roll = rawMod.Range;
            }
            var line = rawMod with { Text = Ranges.Replace(rawMod.Text, m => Math.Floor(double.Parse(m.Groups[1].Value, Invariant)
                + (double.Parse(m.Groups[2].Value, Invariant) - double.Parse(m.Groups[1].Value, Invariant)) * roll + 0.5).ToString(Invariant)) };
            if (line.Derived && directMods.Count > 0) continue; // These lines are generated from the exact Prefix/Suffix ids.
            try { translated.Add((line, ResolveMod(line, type, item, catalog))); }
            catch (PathOfBuildingUnsupportedException e) { unresolved.Add((line, e.Message)); }
        }
        if (unresolved.Count > 0)
        {
            // Translated lines are offered too: one of them can be a line of a hybrid whose other lines are unresolved.
            var texts = unresolved.Select(u => u.Line).Concat(translated.Select(t => t.Line))
                .Select(l => new ModText.Line(l.Section, l.Text)).ToArray();
            var found = ModText.Embedded.Resolve(texts, type, item, catalog.Mods).Where(f => f.Lines.Any(i => i < unresolved.Count)).ToArray();
            foreach (var match in found)
            {
                mods.Add((match.Mod, match.Lines.Any(i => i < unresolved.Count ? unresolved[i].Line.Fractured : translated[i - unresolved.Count].Line.Fractured)));
                if (match.OutsideRoll)
                    warnings.Add($"{type.Name}: kept a value beyond the native roll of '{string.Join(", ", match.Lines.Select(i => texts[i].Text))}'.");
            }
            var named = found.SelectMany(f => f.Lines).ToHashSet();
            for (int i = 0; i < unresolved.Count; i++)
                if (!named.Contains(i)) warnings.Add($"Skipped modifier on {type.Name}: {unresolved[i].Reason}.");
            translated = [.. translated.Where((_, i) => !named.Contains(i + unresolved.Count))];
            // The strict checks below compare this client's own numbers with PoB's. Text matches can differ slightly.
            omittedModifiers = true;
        }
        mods.AddRange(translated.Select(t => (t.Mod, t.Line.Fractured)));
        if (!hasImplicitMarker && type.Implicits is { Count: > 0 })
            throw new ArgumentException("the export omits its implicit modifier values; re-save the item in PoB");
        item = item with
        {
            Mods = [.. mods.Where(m => m.Fractured).Concat(mods.Where(m => !m.Fractured)).Select(m => m.Mod)],
            FracturedCount = checked((byte)mods.Count(m => m.Fractured && m.Mod.Section == ModSection.Explicit)),
        };
        item = NativeSource.Apply(definition, type, item, catalog, folder, warnings);
        if (catalyst is not null || catalystQuality is not null)
        {
            var catalysts = NativeData.Shared("catalyst-quality-types", () =>
            {
                using var qualities = NativeData.Read(Path.Combine(folder, "tables", "AlternateQualityTypes.json"));
                using var bases = NativeData.Read(Path.Combine(folder, "tables", "BaseItemTypes.json"));
                var names = bases.RootElement.EnumerateArray().Select(r => r.GetProperty("Name").GetString()!).ToArray();
                var map = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
                foreach (var (r, i) in qualities.RootElement.EnumerateArray().Select((r, i) => (r, i)).Where(x => x.r.GetProperty("IsCatalyst").GetBoolean()))
                {
                    map.Add(names[r.GetProperty("Item").GetInt32()], checked((byte)i));
                    map.Add(r.GetProperty("Description").GetString()!, checked((byte)i));
                }
                return map;
            });
            byte? typeFromName = catalyst is not null && catalysts.TryGetValue(catalyst + " Catalyst", out byte named) ? named : null;
            if (catalyst is not null && typeFromName is null) throw new PathOfBuildingUnsupportedException($"Unknown native catalyst '{catalyst}'.");
            if (NativeSource.Quality(definition) is { } native && catalysts.TryGetValue(native.Name, out byte fromProperty))
            {
                if (typeFromName is byte exported && exported != fromProperty || catalystQuality is int quality && quality != native.Value)
                    throw new PathOfBuildingUnsupportedException($"{type.Name}: native catalyst property disagrees with its PoB export");
                typeFromName = fromProperty;
                catalystQuality ??= native.Value;
            }
            if (typeFromName is not byte qualityType || catalystQuality is null)
                throw new PathOfBuildingUnsupportedException($"{type.Name}: catalyst identity and quality must both map to this client's quality table");
            item = item with { Quality = checked((byte)catalystQuality.Value), AlternateQualityType = qualityType };
        }
        if (!item.NativeMods && directMods.Count == 0 && item.Mods.Any(m => catalog.Mods.ByHash(m.Hash) is { } row
            && row.Stats.Select((s, i) => (s.Id, Value: m.Values[i])).Any(s => s.Value != 0
                && s.Id is "local_explicit_mod_effect_+%" or "local_prefix_mod_effect_+%" or "local_suffix_mod_effect_+%")))
            throw new PathOfBuildingUnsupportedException($"{type.Name}: the export shows already-scaled affixes; an exact native item snapshot is required to recover its original rolls without applying modifier magnitudes twice");
        item = WithHiddenDefenceMod(item, type, title, catalog.Mods);
        if (percentile.Count > 0)
        {
            // PoB reconstructs each percentile from rounded tooltip values. Their small differences
            // do not prove independent native rolls; the client has one shared wire byte.
            item = item with { DefenceBaseRoll = (byte)Math.Floor(percentile.Average() * Item.MaximumDefenceBaseRoll + 0.5) };
        }
        if (defenceDisplay.Count > 0)
        {
            // Ninja/PoB can put global defence increases into the exported tooltip. The fingerprint-verified
            // native item properties describe the physical item, not the character's calculated totals.
            var expectedDefences = new Dictionary<string, int>(NativeSource.Defences(definition) ?? defenceDisplay);
            var ownDefences = ItemModStats.Local(item, catalog.Mods);
            if (ownDefences.GetValueOrDefault("local_no_energy_shield") != 0
                || ownDefences.GetValueOrDefault("local_unique_tabula_rasa_no_requirement_or_energy_shield") != 0)
                expectedDefences["EnergyShield"] = 0; // Native prohibition wins over API/PoB's calculated base total.
            if (catalog.Defences(type) is not { } defences)
                throw new ArgumentException($"{type.Name} exports defences but its native armour data is unavailable");
            bool Matches(byte roll)
            {
                var values = ItemDefences.Calculate(item, defences, catalog.Mods, roll);
                return expectedDefences.All(kv => kv.Value == (kv.Key == "Armour" ? values.Armour
                    : kv.Key == "Evasion" ? values.Evasion : kv.Key == "Ward" ? ItemDefences.Ward(item, defences, catalog.Mods, roll) : values.EnergyShield));
            }
            var possible = Enumerable.Range(0, Item.MaximumDefenceBaseRoll + 1).Select(n => (byte)n).Where(Matches)
                .OrderBy(n => Math.Abs(n - item.DefenceBaseRoll)).ToArray();
            if (possible.Length == 0)
            {
                throw new ArgumentException("the exported defence values cannot be represented by this client's native base roll and modifiers");
            }
            else item = item with { DefenceBaseRoll = possible[0] };
        }
        if (title is not null) item = WithName(item, title, folder, warnings);
        if (exportedLevelRequirement is int required) item = item with { RequiredLevel = required };
        return (type, item);
    }

    static (string Text, HashSet<string> Flags, double[] Ranges) ReadTags(string raw, int selectedVariant)
    {
        var flags = new HashSet<string>(StringComparer.Ordinal);
        double[] ranges = [];
        foreach (Match tag in Tags.Matches(raw))
        {
            string name = tag.Groups[1].Value, value = tag.Groups[2].Value;
            if (name == "range")
            {
                ranges = value.Split(',').Select(v => double.TryParse(v, NumberStyles.Float, Invariant, out double r) && double.IsFinite(r) && r >= 0 && r <= 1
                    ? r : throw new ArgumentException($"invalid roll range '{value}'")).ToArray();
            }
            else if (name == "variant")
            {
                if (!value.Split(',').Any(v => int.TryParse(v, out int variant) && variant == selectedVariant)) flags.Add("excluded");
            }
            else if (name == "disabled") flags.Add("excluded");
            else if (name != "tags") flags.Add(name);
        }
        return (Tags.Replace(raw, "").Trim(), flags, ranges);
    }

    static int Number(string value, string field, int min, int max) =>
        int.TryParse(value, NumberStyles.Integer, Invariant, out int n) && n >= min && n <= max
            ? n : throw new ArgumentException($"{field} must be {min}..{max}, got '{value}'");

    // PoB omits hidden zero-ES modifiers and zero-valued defence lines from these unique exports.
    // Restore only verified native identities, before matching displayed defences or level requirements.
    static Item WithHiddenDefenceMod(Item item, ItemCatalog.BaseType type, string? title, ModCatalog mods)
    {
        if (item.Rarity != ItemRarity.Unique) return item;
        var identity = title switch
        {
            "Tabula Rasa" => (Base: "BodyInt1", Mod: "RemoveStatsFromTabulaRasa"),
            "Skin of the Loyal" or "Skin of the Lords" => (Base: "BodyInt1", Mod: "RemoveStatsUnique__1"),
            "The Apostate" => (Base: "BodyInt7", Mod: "RemoveStatsUnique__2"),
            _ => default,
        };
        if (identity.Mod is null) return item;
        if (type.Id != "Metadata/Items/Armours/BodyArmours/" + identity.Base)
            throw new ArgumentException($"unique '{title}' does not match this native base");
        var mod = mods.ById(identity.Mod);
        if (mod is null || mod.Stats.Count != 1
            || mod.Stats[0].Id != "local_unique_tabula_rasa_no_requirement_or_energy_shield"
            || mod.Stats[0].Min != 1 || mod.Stats[0].Max != 1)
            throw new ArgumentException($"the hidden defence modifier for '{title}' is unavailable in this client's data");
        return item.Mods.Any(m => m.Hash == mod.Hash) ? item
            : item with { Mods = [.. item.Mods, new(ModSection.Hidden, mod.Hash, [1])] };
    }

    // InvariantGlobalization has no Unicode decomposition, so the accents PoE names use are mapped by hand.
    static string Plain(string text) => string.Concat(text.Select(c => "àáâãäåèéêëìíîïòóôõöùúûüñçýÀÁÂÃÄÅÈÉÊËÌÍÎÏÒÓÔÕÖÙÚÛÜÑÇÝ".IndexOf(c) is int i and >= 0
        ? "aaaaaaeeeeiiiiooooouuuuncyAAAAAAEEEEIIIIOOOOOUUUUNCY"[i] : c));

    static Item WithName(Item item, string title, string folder, List<string> warnings) => NativeNames.WithName(item, title, folder, warnings);

    static ItemMod ResolveMod(ParsedMod line, ItemCatalog.BaseType type, Item item, ItemCatalog catalog)
    {
        var values = Translate(line.Text, type);
        if (values is null) throw new PathOfBuildingUnsupportedException($"unsupported {line.Section.ToString().ToLowerInvariant()} modifier '{line.Text}'");
        IEnumerable<ModCatalog.Mod> pool;
        if (line.Section == ModSection.Implicit && type.Implicits is { Count: > 0 }) pool = type.Implicits;
        else pool = catalog.Mods.All.Where(m =>
            line.Section == ModSection.Enchant ? m.GenerationType == 10
            : line.Crafted ? m.Domain == 9 && m.GenerationType is 1 or 2
            : item.Rarity == ItemRarity.Unique ? m.Domain == type.ModDomain && m.GenerationType == 3
            : line.Section == ModSection.Implicit ? m.Domain == type.ModDomain && m.GenerationType == 3
            : m.Domain == type.ModDomain && m.GenerationType is 1 or 2 && m.Level <= item.ItemLevel && m.WeightFor(type.Tags) > 0);
        var candidates = pool.Where(m => m.Stats.Count == values.Count
            && m.Stats.All(s => values.Any(v => v.Id == s.Id && v.Value >= Math.Min(s.Min, s.Max) && v.Value <= Math.Max(s.Min, s.Max))))
            .OrderBy(m => m.Stats.Sum(s => (long)Math.Abs(s.Max - s.Min))).ThenByDescending(m => m.Level).ThenBy(m => m.Id, StringComparer.Ordinal).ToArray();
        if (candidates.Length == 0)
            throw new PathOfBuildingUnsupportedException($"no native modifier with matching stats and rolls for '{line.Text}' on {type.Name} at item level {item.ItemLevel}");
        var chosen = candidates[0];
        return new(line.Section, chosen.Hash, [.. chosen.Stats.Select(s => values.Single(v => v.Id == s.Id).Value)]);
    }

    // Deliberately bounded: these unambiguous English descriptions map to native Stats.dat keys. Complex/conditional
    // descriptions fail closed. Mod IDs, tiers and allowed rolled values still come from the installed client's tables.
    static IReadOnlyList<StatValue>? Translate(string text, ItemCatalog.BaseType type)
    {
        bool weapon = type.Components.Contains("Weapon");
        bool armour = type.Components.Contains("Armour");
        bool flask = type.Components.Contains("Flask");
        Match Match(string pattern) => Regex.Match(text, "^" + pattern + "$", RegexOptions.CultureInvariant);
        IReadOnlyList<StatValue> One(string key, Match m) => [new(key, int.Parse(m.Groups[1].Value, Invariant))];
        if (armour && text.Equals("Has no Energy Shield", StringComparison.OrdinalIgnoreCase))
            return [new("local_no_energy_shield", 1)];
        if (Match(@"(?i:(\d+)% increased maximum Energy Shield if Corrupted)") is { Success: true } conditionalES)
            return One("local_maximum_energy_shield_+%_if_item_corrupted", conditionalES);
        if (text.Equals("Gain Maximum Life instead of Maximum Energy Shield from Equipped Armour Items", StringComparison.OrdinalIgnoreCase))
            return [new("gain_maximum_life_instead_of_maximum_es_from_armour", 1)];
        if (Match(@"(?i:Gain Maximum Life instead of Maximum Energy Shield from Equipped Armour Items at (\d+)% of (?:the|its) value)") is { Success: true } conversion)
            return One("gain_maximum_life_instead_of_maximum_es_from_armour_at_x%_of_the_value", conversion);
        var flat = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["maximum Life"] = "base_maximum_life", ["maximum Mana"] = "base_maximum_mana",
            ["Strength"] = "additional_strength", ["Dexterity"] = "additional_dexterity", ["Intelligence"] = "additional_intelligence",
            ["all Attributes"] = "additional_all_attributes", ["Accuracy Rating"] = weapon ? "local_accuracy_rating" : "accuracy_rating",
            ["Armour"] = armour ? "local_base_physical_damage_reduction_rating" : "base_physical_damage_reduction_rating",
            ["Evasion Rating"] = armour ? "local_base_evasion_rating" : "base_evasion_rating",
            ["maximum Energy Shield"] = armour ? "local_energy_shield" : "base_maximum_energy_shield",
        };
        foreach (var (label, stat) in flat)
            if (Match(@"\+?(-?\d+) to " + Regex.Escape(label)) is { Success: true } m) return One(stat, m);
        var increased = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["maximum Life"] = "maximum_life_+%", ["maximum Mana"] = "maximum_mana_+%",
            ["Spell Damage"] = "spell_damage_+%", ["Physical Damage"] = weapon ? "local_physical_damage_+%" : "physical_damage_+%",
            ["Fire Damage"] = "fire_damage_+%", ["Cold Damage"] = "cold_damage_+%", ["Lightning Damage"] = "lightning_damage_+%",
            ["Chaos Damage"] = "chaos_damage_+%", ["Elemental Damage"] = "elemental_damage_+%",
            ["Attack Speed"] = weapon ? "local_attack_speed_+%" : "attack_speed_+%", ["Cast Speed"] = "base_cast_speed_+%",
            ["Movement Speed"] = "base_movement_velocity_+%", ["Critical Strike Chance"] = weapon ? "local_critical_strike_chance_+%" : "critical_strike_chance_+%",
            ["Global Critical Strike Chance"] = "critical_strike_chance_+%", ["Critical Strike Chance for Spells"] = "spell_critical_strike_chance_+%",
            ["Armour"] = armour ? "local_physical_damage_reduction_rating_+%" : "physical_damage_reduction_rating_+%",
            ["Evasion Rating"] = armour ? "local_evasion_rating_+%" : "evasion_rating_+%",
            ["Energy Shield"] = armour ? "local_energy_shield_+%" : "maximum_energy_shield_+%",
            ["Armour and Evasion"] = armour ? "local_armour_and_evasion_+%" : "armour_and_evasion_+%",
            ["Armour and Energy Shield"] = armour ? "local_armour_and_energy_shield_+%" : "armour_and_energy_shield_+%",
            ["Evasion and Energy Shield"] = armour ? "local_evasion_and_energy_shield_+%" : "evasion_and_energy_shield_+%",
            ["Flask Charges gained"] = "flask_charges_gained_+%", ["Flask Effect Duration"] = "flask_duration_+%",
        };
        foreach (var (label, stat) in increased)
            if (Match(@"(-?\d+)% increased " + Regex.Escape(label)) is { Success: true } m) return One(stat, m);
        if (flask)
        {
            var flaskStats = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Charge Recovery"] = "local_charges_added_+%", ["Charges per use"] = "local_charges_used_+%",
                ["Duration"] = "local_flask_duration_+%", ["Amount Recovered"] = "local_flask_amount_to_recover_+%",
                ["Recovery rate"] = "local_flask_recovery_speed_+%", ["effect"] = "local_flask_effect_+%",
            };
            foreach (var (label, stat) in flaskStats)
            {
                if (Match(@"(\d+)% increased " + Regex.Escape(label)) is { Success: true } inc) return One(stat, inc);
                if (Match(@"(\d+)% reduced " + Regex.Escape(label)) is { Success: true } dec)
                    return [new(stat, -int.Parse(dec.Groups[1].Value, Invariant))];
            }
            if (Match(@"\+(\d+) to Maximum Charges") is { Success: true } charges) return One("local_extra_max_charges", charges);
            if (text == "Instant Recovery") return [new("local_flask_recovers_instantly", 1)];
            if (Match(@"(\d+)% of Recovery applied Instantly") is { Success: true } instant)
                return One("local_flask_recovery_amount_%_to_recover_instantly", instant);
        }
        foreach (string element in new[] { "Fire", "Cold", "Lightning", "Chaos" })
            if (Match(@"\+?(-?\d+)% to " + element + " Resistance") is { Success: true } m)
                return One("base_" + element.ToLowerInvariant() + "_damage_resistance_%", m);
        if (Match(@"\+?(-?\d+)% to all Elemental Resistances") is { Success: true } all) return One("base_resist_all_elements_%", all);
        if (Match(@"\+?(-?\d+)% to Global Critical Strike Multiplier") is { Success: true } crit) return One("base_critical_strike_multiplier_+", crit);
        foreach (string element in new[] { "Physical", "Fire", "Cold", "Lightning", "Chaos" })
        {
            bool spells = text.EndsWith(" to Spells", StringComparison.Ordinal);
            if (Match(@"Adds (\d+) to (\d+) " + element + " Damage" + (spells ? " to Spells" : "")) is not { Success: true } m) continue;
            string prefix = spells ? "spell_" : weapon ? "local_" : "";
            string damage = element.ToLowerInvariant();
            return [new(prefix + "minimum_added_" + damage + "_damage", int.Parse(m.Groups[1].Value, Invariant)),
                new(prefix + "maximum_added_" + damage + "_damage", int.Parse(m.Groups[2].Value, Invariant))];
        }
        return null;
    }
}
