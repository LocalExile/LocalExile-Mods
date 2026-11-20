using GameServer.Core.Protocol.Instance;

/// <summary>
/// Lets crafting currencies modify Phrecia Idols (<c>ItemClasses</c> <c>AtlasRelic</c>) on the server, bypassing the
/// <c>Unmodifiable</c> class flag checked by <c>ItemRolls</c>.
/// <para>The 3.29.3c client reads the red "Unmodifiable" tooltip line from its local <c>ItemClasses.dat</c> table and
/// does not block sending currency packets (<c>C→S 0x0071</c> / Shift <c>0x0072</c>) for Idols, so intercepting those
/// two packets is enough to make Idol crafting work across inventory, stash tabs and the Idol side panel without any
/// changes to LocalExile.</para>
/// </summary>
public sealed class CraftableIdolsMod : IMod
{
    public const string IdolClass = "AtlasRelic";

    public sealed record Choices(bool AllowFracturing, bool AllowVaal, bool AllowMirror);

    public enum IdolCurrency
    {
        None,
        Transmutation,
        Alteration,
        Augmentation,
        Regal,
        Alchemy,
        Chaos,
        Exalted,
        Annulment,
        Scouring,
        Divine,
        Blessed,
        Fracturing,
        Vaal,
        TaintedChaos,
        TaintedExalted,
        Mirror,
    }

    Choices _choices = new(false, false, false);
    IModContext _mod = null!;

    public void Load(IModContext mod)
    {
        _mod = mod;
        _choices = mod.Settings<Choices>();
        string id = mod.Manifest.Id;
        GameEvents.Packet(InstanceItems.UseItemOnId).Add(id, OnUseItemOn);
        GameEvents.Packet(InstanceItems.UseItemTypeOnId).Add(id, OnUseItemTypeOn);

        var extras = new List<string>();
        if (_choices.AllowFracturing) extras.Add("Fracturing");
        if (_choices.AllowVaal) extras.Add("Vaal/Tainted");
        if (_choices.AllowMirror) extras.Add("Mirror");
        mod.Log(extras.Count > 0
            ? $"Idol crafting enabled (extras: {string.Join(", ", extras)})"
            : "Idol crafting enabled (standard currencies)");
    }

    async Task<bool> OnUseItemOn(ClientState conn, byte[] bytes)
    {
        if (!_mod.Lifetime.IsActive || bytes.Length < InstanceItems.UseItemOnLength) return false;
        return await HandleUseItemOn(conn, InstanceItems.ReadUseItemOn(bytes));
    }

    async Task<bool> OnUseItemTypeOn(ClientState conn, byte[] bytes)
    {
        if (!_mod.Lifetime.IsActive || bytes.Length < InstanceItems.UseItemTypeOnLength) return false;
        var again = InstanceItems.ReadUseItemTypeOn(bytes);
        var items = Game.ItemsOf(conn);
        var stash = Game.StashOf(conn);

        // Check whether the target is an Idol first so non-Idol Shift-crafts skip any extra scanning.
        if (!IsIdolTarget(conn, items, stash, again.TargetInventoryId, again.TargetId)) return false;

        byte[] rows = [PlayerInventories.MainInventory, .. Enumerable.Range(0, InstanceInventories.PlayerDefaults.Length).Select(r => (byte)r)];
        var stack = rows.Select(r => (Row: r, Item: items.Items(r).FirstOrDefault(i => i.Item.TypeHash == again.TypeHash)))
            .FirstOrDefault(s => s.Item is not null);
        (int Tab, InventoryItem Item)? inStash = null;
        for (int t = 0; stack.Item is null && inStash is null && t < stash.Count; t++)
            if (stash.Inventory(t).Items.FirstOrDefault(i => i.Item.TypeHash == again.TypeHash) is { } found)
                inStash = (t, found);

        if (inStash is var (stashTab, stashItem))
            return await HandleUseItemOn(conn, new InstanceItems.UseItemOn(stash.InventoryOf(stashTab), stashItem.Id,
                again.TargetInventoryId, again.TargetId));
        if (stack.Item is null) return false;
        return await HandleUseItemOn(conn, new InstanceItems.UseItemOn(PlayerInventories.InventoryId(stack.Row), stack.Item.Id,
            again.TargetInventoryId, again.TargetId));
    }

    static bool IsIdolTarget(ClientState conn, PlayerInventories items, AccountStash stash, uint targetInventoryId, uint targetId)
    {
        int? targetTab = stash.TabOf(targetInventoryId);
        InventoryItem? target = targetTab is int tab
            ? stash.Inventory(tab).Items.FirstOrDefault(i => i.Id == targetId)
            : items.RowOf(targetId) is byte row ? items.Items(row).FirstOrDefault(i => i.Id == targetId) : null;
        return target is not null && IsIdol(Game.ItemsFor(conn), target.Item, out _);
    }

    public static bool IsIdol(ItemCatalog catalog, Item item, out ItemCatalog.BaseType type)
    {
        type = null!;
        if (!item.Components.Contains("Mods")
            || catalog.ByHash(item.TypeHash) is not { Supported: true, Class: IdolClass } found
            || !found.Components.Contains("Mods"))
            return false;
        type = found;
        return true;
    }

    public static IdolCurrency Classify(ItemCatalog catalog, ItemCatalog.BaseType? currency, Choices choices)
    {
        if (currency is null) return IdolCurrency.None;
        return currency.Id switch
        {
            CurrencyEffects.OrbOfTransmutation => IdolCurrency.Transmutation,
            CurrencyEffects.OrbOfAlteration => IdolCurrency.Alteration,
            CurrencyEffects.OrbOfAugmentation => IdolCurrency.Augmentation,
            CurrencyEffects.RegalOrb => IdolCurrency.Regal,
            CurrencyEffects.OrbOfAlchemy => IdolCurrency.Alchemy,
            CurrencyEffects.ChaosOrb => IdolCurrency.Chaos,
            CurrencyEffects.ExaltedOrb => IdolCurrency.Exalted,
            CurrencyEffects.OrbOfAnnulment => IdolCurrency.Annulment,
            CurrencyEffects.OrbOfScouring => IdolCurrency.Scouring,
            CurrencyEffects.DivineOrb => IdolCurrency.Divine,
            CurrencyEffects.BlessedOrb => IdolCurrency.Blessed,
            CurrencyEffects.FracturingOrb when choices.AllowFracturing => IdolCurrency.Fracturing,
            CurrencyEffects.VaalOrb when choices.AllowVaal => IdolCurrency.Vaal,
            CurrencyEffects.TaintedChaosOrb when choices.AllowVaal => IdolCurrency.TaintedChaos,
            CurrencyEffects.TaintedExaltedOrb when choices.AllowVaal => IdolCurrency.TaintedExalted,
            CurrencyEffects.MirrorOfKalandra when choices.AllowMirror => IdolCurrency.Mirror,
            _ => catalog.CurrencyAction(currency) switch
            {
                "transmute_to_magic" => IdolCurrency.Transmutation,
                "reroll_magic" => IdolCurrency.Alteration,
                "add_mod_to_magic" => IdolCurrency.Augmentation,
                "upgrade_magic_to_rare" => IdolCurrency.Regal,
                "transmute_to_rare" => IdolCurrency.Alchemy,
                "reroll" => IdolCurrency.Chaos,
                "add_mod_to_rare" => IdolCurrency.Exalted,
                "remove_random_mod" => IdolCurrency.Annulment,
                "convert_to_normal" => IdolCurrency.Scouring,
                "reroll_mod_values" => IdolCurrency.Divine,
                "reroll_implicit_mod" => IdolCurrency.Blessed,
                "fracture_random_mod" when choices.AllowFracturing => IdolCurrency.Fracturing,
                "corrupt_item" or "special_corrupt_item" when choices.AllowVaal => IdolCurrency.Vaal,
                _ => IdolCurrency.None,
            },
        };
    }

    async Task<bool> HandleUseItemOn(ClientState conn, InstanceItems.UseItemOn use)
    {
        var items = Game.ItemsOf(conn);
        var stash = Game.StashOf(conn);

        int? stashTab = stash.TabOf(use.InventoryId);
        byte usedRow = stashTab is null && items.RowOf(use.ItemId) is byte r ? r : (byte)0;
        var used = stashTab is int fromTab
            ? stash.Inventory(fromTab).Items.FirstOrDefault(i => i.Id == use.ItemId)
            : items.RowOf(use.ItemId) is not null ? items.Items(usedRow).FirstOrDefault(i => i.Id == use.ItemId) : null;

        int? targetTab = stash.TabOf(use.TargetInventoryId);
        byte targetRow = targetTab is null && items.RowOf(use.TargetId) is byte tRow ? tRow : (byte)0;
        var target = targetTab is int toTab
            ? stash.Inventory(toTab).Items.FirstOrDefault(i => i.Id == use.TargetId)
            : items.RowOf(use.TargetId) is not null ? items.Items(targetRow).FirstOrDefault(i => i.Id == use.TargetId) : null;

        if (used is null || target is null) return false;

        var catalog = Game.ItemsFor(conn);
        if (!IsIdol(catalog, target.Item, out var targetType)) return false;

        var currencyType = catalog.ByHash(used.Item.TypeHash);
        var kind = Classify(catalog, currencyType, _choices);
        if (kind == IdolCurrency.None) return false;

        var (remote, stream, ciphers) = conn.Wire;

        if (kind == IdolCurrency.Mirror)
        {
            await MirrorIdol(remote, stream, ciphers, conn, items, stash, stashTab, usedRow, used, target);
            return true;
        }

        var rolls = Game.RollsFor(conn);
        var uniques = Game.UniquesFor(conn);
        var random = target.Item.Foreseeing ? ItemRolls.ForeseeRandom(target.Item, used.Item.TypeHash) : Random.Shared;
        Item? result = ApplyCurrency(kind, catalog, rolls, uniques, targetType, target.Item, random);
        if (result is not null && target.Item.Foreseeing)
        {
            conn.ForesightRequest = null;
            result = ItemRolls.ClearForesee(result);
        }

        if (result is null)
        {
            Console.WriteLine($"[craftableidols] {remote} <= 0x0071 use {currencyType!.Name} on Idol {target.Id} ({targetType.Name}): refused");
            return true;
        }

        InventoryItem changed;
        uint targetInventory = PlayerInventories.InventoryId(targetRow);
        if (stashTab is not null || targetTab is not null)
        {
            if (ItemMoves.UseAcross(items, stash, new(stashTab, usedRow, used.Id), new(targetTab, targetRow, target.Id), _ => result)
                is not var (left, changedItem)) return true;
            changed = changedItem;
            async Task SendPlace(int? tab, byte row, List<uint> gone, List<InventoryItem> now, string what)
            {
                if (tab is int t)
                    await Game.SendTabChange(remote, stream, ciphers, conn, stash, t, gone, now, $"0x00bf stash tab {t}: {what}");
                else
                    await Program.Send(remote, stream, ciphers,
                        InstanceItems.InventoryChange(PlayerInventories.InventoryId(row), items.NextRevision(row), gone, now),
                        $"0x00bf inventory {PlayerInventories.InventoryId(row)}: {what}");
            }
            List<InventoryItem> usedNow = left is { } l ? [l] : [];
            if (stashTab == targetTab && (stashTab is not null || usedRow == targetRow))
                await SendPlace(targetTab, targetRow, [used.Id, target.Id], [.. usedNow, changed],
                    $"Idol {target.Id} changed, {currencyType!.Name} used");
            else
            {
                await SendPlace(stashTab, usedRow, [used.Id], usedNow,
                    $"{currencyType!.Name} used" + (left is null ? ", stack gone" : $", {left.Item.StackSize} left"));
                await SendPlace(targetTab, targetRow, [target.Id], [changed], $"Idol {target.Id} changed");
            }
        }
        else
        {
            if (ItemMoves.UseOn(items, usedRow, used.Id, targetRow, target.Id, _ => result) is not var (taken, changedItem))
                return true;
            changed = changedItem;
            uint usedInventory = PlayerInventories.InventoryId(usedRow);
            if (usedRow == targetRow)
                await Program.Send(remote, stream, ciphers,
                    InstanceItems.InventoryChange(targetInventory, items.NextRevision(targetRow), [used.Id, target.Id],
                        taken.Left is { } left ? [left, changed] : [changed]),
                    $"0x00bf inventory {targetInventory}: Idol {target.Id} changed, {currencyType!.Name} used");
            else
            {
                await Program.Send(remote, stream, ciphers,
                    InstanceItems.InventoryChange(usedInventory, items.NextRevision(usedRow), [used.Id],
                        taken.Left is { } left ? [left] : []),
                    $"0x00bf inventory {usedInventory}: {currencyType!.Name} used");
                await Program.Send(remote, stream, ciphers,
                    InstanceItems.InventoryChange(targetInventory, items.NextRevision(targetRow), [target.Id], [changed]),
                    $"0x00bf inventory {targetInventory}: Idol {target.Id} changed");
            }
        }

        Console.WriteLine($"[craftableidols] {remote} <= 0x0071 {currencyType!.Name} ({used.Id}) on Idol {target.Id} ({targetType.Name}): "
            + string.Join("; ", changed.Item.Mods.Select(catalog.Mods.Describe)));
        return true;
    }

    static async Task MirrorIdol(string remote, System.Net.Sockets.NetworkStream stream,
        GameServer.Core.Protocol.Login.ConnectionCiphers ciphers, ClientState conn,
        PlayerInventories items, AccountStash stash, int? stashTab, byte usedRow, InventoryItem used, InventoryItem target)
    {
        if (target.Item.SkinTransferSource is not null || target.Item.Rarity == ItemRarity.Unique
            || !target.Item.Identified || target.Item.Corrupted || target.Item.Mirrored || target.Item.Vestigial
            || items.Items(PlayerInventories.Cursor).Count > 0)
        {
            Console.WriteLine($"[craftableidols] {remote} <= 0x0071 Mirror of Kalandra on Idol {target.Id}: refused");
            return;
        }

        Item copy = target.Item with { Mirrored = true };
        var done = Saves.Atomically<(InventoryItem? Left, InventoryItem Held)?>(() =>
        {
            if (items.Items(PlayerInventories.Cursor).Count > 0) return null;
            InventoryItem? left = null;
            if (stashTab is int tab)
            {
                if (tab < 0 || tab >= stash.Count) return null;
                var stack = stash.Inventory(tab).Items.FirstOrDefault(i => i.Id == used.Id);
                if (stack is null) return null;
                if (stack.Item.StackSize <= 1) { if (stash.Take(tab, used.Id) is null) return null; }
                else if ((left = stash.Shrink(tab, used.Id, 1)) is null) return null;
            }
            else if (items.ConsumeOne(usedRow, used.Id) is { } consumed) left = consumed.Left;
            else return null;
            return (left, items.Add(PlayerInventories.Cursor, 0, 0, copy));
        });
        if (done is not var (leftItem, held)) return;

        if (stashTab is int fromTab)
            await Game.SendTabChange(remote, stream, ciphers, conn, stash, fromTab, [used.Id],
                leftItem is { } l ? [l] : [], $"0x00bf stash tab {fromTab}: Mirror of Kalandra used");
        else
        {
            uint usedInventory = PlayerInventories.InventoryId(usedRow);
            await Program.Send(remote, stream, ciphers,
                InstanceItems.InventoryChange(usedInventory, items.NextRevision(usedRow), [used.Id],
                    leftItem is { } l ? [l] : []),
                $"0x00bf inventory {usedInventory}: Mirror of Kalandra used");
        }
        uint cursor = PlayerInventories.InventoryId(PlayerInventories.Cursor);
        await Program.Send(remote, stream, ciphers,
            InstanceItems.InventoryChange(cursor, items.NextRevision(PlayerInventories.Cursor), [], [held]),
            $"0x00bf cursor: mirrored Idol copy {held.Id}");
        Console.WriteLine($"[craftableidols] {remote} <= 0x0071 Mirror of Kalandra ({used.Id}) on Idol {target.Id}: copy {held.Id} on the cursor");
    }

    /// <summary>Computes the crafted Idol result for a handled currency, or <c>null</c> when the currency refuses the target.</summary>
    public static Item? ApplyCurrency(IdolCurrency kind, ItemCatalog catalog, ItemRolls rolls, UniqueCatalog uniques,
        ItemCatalog.BaseType targetType, Item item, Random random)
    {
        if (item.Mirrored || item.Vestigial) return null;

        if (kind is IdolCurrency.TaintedChaos or IdolCurrency.TaintedExalted)
            return ApplyTainted(kind, catalog, rolls, targetType, item, random);

        if (item.Corrupted) return null;

        if (kind == IdolCurrency.Vaal)
            return Vaal(targetType, item, rolls, random);

        if (!item.Identified) return null;

        return kind switch
        {
            IdolCurrency.Transmutation => item.Rarity == ItemRarity.Normal && rolls.Pool(targetType, item).Count > 0
                ? rolls.Roll(item, ItemRarity.Magic, random, respectMetamods: true) with { Identified = true }
                : null,

            IdolCurrency.Alteration => item.Rarity == ItemRarity.Magic && rolls.Pool(targetType, item).Count > 0
                ? rolls.Roll(item, ItemRarity.Magic, random, respectMetamods: true) with { Identified = true }
                : null,

            IdolCurrency.Augmentation => item.Rarity == ItemRarity.Magic
                ? rolls.AddAffix(item, 1, random)
                : null,

            IdolCurrency.Regal => item.Rarity == ItemRarity.Magic
                && rolls.AddAffix(item, rolls.RareMaxAffixes(item), random) is { } regaled
                    ? regaled with { Rarity = ItemRarity.Rare, Identified = true, NameWords = rolls.RareName(targetType, random) }
                    : null,

            IdolCurrency.Alchemy => item.Rarity == ItemRarity.Normal && rolls.Pool(targetType, item).Count > 0
                ? rolls.Roll(item, ItemRarity.Rare, random) with { Identified = true }
                : null,

            IdolCurrency.Chaos => item.Rarity == ItemRarity.Rare && rolls.Pool(targetType, item).Count > 0
                ? rolls.Roll(item, ItemRarity.Rare, random, respectMetamods: true) with { Identified = true }
                : null,

            IdolCurrency.Exalted => item.Rarity == ItemRarity.Rare
                ? rolls.AddAffix(item, rolls.RareMaxAffixes(item), random)
                : null,

            IdolCurrency.Annulment => Annul(catalog, item, random),
            IdolCurrency.Scouring => rolls.Scour(item),
            IdolCurrency.Divine => rolls.Reforge(item, random, uniques.RollsOf(item)),
            IdolCurrency.Blessed => rolls.Bless(item, random, uniques.RollsOf(item)),
            IdolCurrency.Fracturing => Fracture(item, random),
            _ => null,
        };
    }

    static Item? Annul(ItemCatalog catalog, Item item, Random random)
    {
        if (item.Rarity is not (ItemRarity.Magic or ItemRarity.Rare)) return null;
        var rules = new MetacraftRules(item, catalog.Mods);
        var candidates = Enumerable.Range(0, item.Mods.Count)
            .Where(i => item.Mods[i].Section == ModSection.Explicit && !rules.Protected(i)
                && (catalog.Mods.ByHash(item.Mods[i].Hash) is not { } mod || rules.Allows(mod)))
            .ToArray();
        if (candidates.Length == 0) return null;
        int removed = candidates[random.Next(candidates.Length)];
        return item with { Mods = [.. item.Mods.Where((_, i) => i != removed)] };
    }

    static Item? Fracture(Item item, Random random)
    {
        if (item.Rarity != ItemRarity.Rare || item.FracturedCount > 0
            || item.Influences != ItemInfluence.None || item.Synthesised)
            return null;
        var explicits = item.Mods.Select((m, i) => (Mod: m, Index: i))
            .Where(p => p.Mod.Section == ModSection.Explicit).ToList();
        if (explicits.Count < 4) return null;
        var (chosen, index) = explicits[random.Next(explicits.Count)];
        var mods = item.Mods.Where((_, i) => i != index).ToList();
        mods.Insert(explicits[0].Index, chosen);
        return item with { Mods = mods, FracturedCount = 1 };
    }

    static Item? Vaal(ItemCatalog.BaseType targetType, Item item, ItemRolls rolls, Random random)
    {
        bool hasPool = rolls.Pool(targetType, item).Count > 0;
        Item result;
        if (item.Rarity == ItemRarity.Unique)
        {
            // 75% corrupt as-is, 25% brick to a rare Idol with its normal base art.
            result = hasPool && random.Next(4) == 0
                ? rolls.Roll(item with { VisualIdentity = targetType.VisualIdentity }, ItemRarity.Rare, random)
                : item;
        }
        else if (item.Rarity != ItemRarity.Rare)
        {
            // Normal / Magic: 50% corrupt as-is, 50% upgrade to a rare Idol.
            result = hasPool && random.Next(2) == 0
                ? rolls.Roll(item, ItemRarity.Rare, random)
                : item;
        }
        else
        {
            // Rare: 50% unchanged, 25% replace 1-2 explicit affixes, 25% full rare reroll.
            int roll = random.Next(4);
            result = !hasPool || roll < 2 ? item
                : roll == 2 ? rolls.ReplaceAffixes(item, random.Next(1, 3), random)
                : rolls.Roll(item, ItemRarity.Rare, random);
        }
        return result with { Corrupted = true, Identified = true };
    }

    static Item? ApplyTainted(IdolCurrency kind, ItemCatalog catalog, ItemRolls rolls,
        ItemCatalog.BaseType targetType, Item item, Random random)
    {
        if (!item.Corrupted || !item.Identified || item.Rarity != ItemRarity.Rare) return null;
        var plain = item with { Corrupted = false };
        Item? result;
        if (kind == IdolCurrency.TaintedChaos)
        {
            if (rolls.Pool(targetType, plain).Count == 0) return null;
            result = random.Next(2) == 0
                ? rolls.Roll(plain, ItemRarity.Rare, random, respectMetamods: true) with { Identified = true }
                : StripExplicits(catalog, plain);
        }
        else
        {
            Item? added = rolls.AddAffix(plain, rolls.RareMaxAffixes(plain), random);
            Item? removed = Annul(catalog, plain, random);
            result = added is null ? removed
                : removed is null ? added
                : random.Next(2) == 0 ? added : removed;
        }
        return result is { } changed ? changed with { Corrupted = true } : null;
    }

    static Item? StripExplicits(ItemCatalog catalog, Item item)
    {
        var fractured = item.FracturedIndexes();
        var kept = item.Mods.Where((m, i) => m.Section != ModSection.Explicit || fractured.Contains(i)).ToArray();
        var sides = fractured.Select(i => catalog.Mods.ByHash(item.Mods[i].Hash)?.GenerationType).ToArray();
        var rarity = sides.Length == 0 ? ItemRarity.Normal
            : sides.Count(g => g == ItemRolls.Prefix) <= 1 && sides.Count(g => g == ItemRolls.Suffix) <= 1 ? ItemRarity.Magic
            : ItemRarity.Rare;
        return item with { Mods = kept, Rarity = rarity, Identified = true, NameWords = rarity == ItemRarity.Rare ? item.NameWords : [] };
    }
}
