using System.Buffers.Binary;
using System.Text.Json;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace LocalExile.Mods.Mercenaries326;

/// <summary>
/// 3.26 Mercenaries of Trarthus for every class and ascendancy:
/// <list type="bullet">
/// <item>Grants permanent mercenary hiring and all 7 Luminary unique equipment slots via <see cref="GameEvents.PlayerStatSources"/>
/// and <see cref="GameEvents.CompanionRules"/>, plus Unique Body Armour via <see cref="GameEvents.CompanionEquip"/>.</item>
/// <item>Optional all-zone spawning from Act 1 Mud Flats onward (scaled to player level) via <see cref="GameEvents.EncounterSpawn"/>.</item>
/// <item>Configurable Luminary Ascendancy bonuses (Noble Blood penalty, Oath of Fealty, Golden Glory, Loyal Bodyguard,
/// Bestowed Knighthood, Legendary Arms damage bonus, and all 7 Luminary small nodes) without double-stacking on Luminary Scions.</item>
/// <item>Speaks NativeMods <c>HostRuleset 0xA0</c> (<c>Mercenaries326StateSync</c>) for native UI Hire &amp; Unique Body Armour support,
/// and degrades gracefully on vanilla clients with <c>/merc326 hire</c> and <c>/merc326 equip</c>.</item>
/// </list>
/// </summary>
public sealed class Mercenaries326Mod : IMod, IDisposable
{
    const string RosterSaveKey = "mercenary.roster";

    // Verified PassiveSkillGraphId values of the 7 real Luminary small nodes (excluding [DNT] small 34962).
    static readonly ushort[] LuminaryLifeSmallNodes = [61133, 27123];                 // AscendancyLuminary1, 3 (+15% Life each)
    static readonly ushort[] LuminaryDamageSmallNodes = [35877, 29308, 54193];        // AscendancyLuminary5, 6, 7 (+15% Damage each)
    static readonly ushort[] LuminaryLinkCastSmallNodes = [63954, 30675];             // AscendancyLuminary2, 4 (+10% Link Cast Speed each)
    static readonly ushort[] AllLuminarySmallNodes = [61133, 63954, 27123, 30675, 35877, 29308, 54193]; // +5% Light Radius each

    // Areas where mercenaries never spawn even with spawnInAllZones enabled (Twilight Strand & non-combat hubs/test zones).
    static readonly HashSet<string> AlwaysExcludedAreas = new(StringComparer.OrdinalIgnoreCase)
    {
        "1_1_1", // The Twilight Strand (opening tutorial beach)
        "1_Labyrinth_AirlockClean",
        "HeistHub",
        "HeistHubEndless",
        "Menagerie_Hub",
        "DeepwaterHub",
        "CharacterSelect",
        "PersonalHideout",
        "GuildHideout__",
        "VFXTest",
        "BlackTest",
        "PetFlats",
    };

    // Pre-Mud Flats Act 1 combat areas (blocked until character reaches Mud Flats 1_1_3 or beyond).
    static readonly HashSet<string> PreMudFlatsAreas = new(StringComparer.OrdinalIgnoreCase)
    {
        "1_1_2",  // The Coast
        "1_1_2a", // The Tidal Island
    };

    sealed record Settings(
        bool AllowUniqueBodyArmour = true,
        bool SpawnInAllZones = false,
        bool EnableNobleBloodDamagePenalty = false,
        bool EnableOathOfFealty = true,
        bool EnableGoldenGlory = true,
        bool EnableLoyalBodyguard = true,
        bool EnableBestowedKnighthood = true,
        bool EnableLegendaryArmsDamageBonus = true,
        bool EnableLuminarySmallNodes = true);

    readonly record struct AreaMeta(int Act, int AreaLevel, bool IsTown, bool IsHideout);

    sealed class RosterSnapshot
    {
        public Dictionary<byte, JsonElement>? Slots { get; set; }
        public byte? InvitedKey { get; set; }
    }

    readonly NativeBridgeProtocol _bridge = new();
    Dictionary<string, AreaMeta> _areas = new(StringComparer.OrdinalIgnoreCase);
    Settings _settings = new();
    bool _disposed;

    public void Load(IModContext mod)
    {
        _settings = mod.Settings<Settings>();
        _areas = LoadAreas();

        string id = mod.Manifest.Id;

        GameEvents.PlayerStatSources.Add(id, ApplyPlayerStats);
        GameEvents.CompanionRules.Add(id, ApplyCompanionRules);
        GameEvents.CompanionEquip.Add(id, ApplyCompanionEquip);
        GameEvents.EncounterSpawn.Add(id, ApplyEncounterSpawn);
        GameEvents.Packet(InstanceRulesets.ActionId).Add(id, HandleRulesetAction);
        GameEvents.PlayerEntered.Add(id, OnPlayerEntered);
        GameEvents.ClientReady.Add(id, OnClientReady);
        GameEvents.PlayerLeaving.Add(id, OnPlayerLeaving);

        GameEvents.Command(id, "merc326",
            "/merc326 [status | hire [join|reserve] | equip [1-3]] (3.26 Mercenaries status & vanilla client fallbacks)",
            HandleCommand);

        // Immediately apply updated stats and push state to already-connected players (e.g. on hot reload / setting change).
        NotifyOnlinePlayers(enabled: true);

        mod.Log($"loaded (uniqueBodyArmour={_settings.AllowUniqueBodyArmour}, spawnAllZones={_settings.SpawnInAllZones}, " +
                $"nobleBloodPenalty={_settings.EnableNobleBloodDamagePenalty}, oath={_settings.EnableOathOfFealty}, " +
                $"goldenGlory={_settings.EnableGoldenGlory}, bodyguard={_settings.EnableLoyalBodyguard}, " +
                $"knighthood={_settings.EnableBestowedKnighthood}, legendaryArms={_settings.EnableLegendaryArmsDamageBonus}, " +
                $"smallNodes={_settings.EnableLuminarySmallNodes})");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        NotifyOnlinePlayers(enabled: false);
        _bridge.Clear();
    }

    // ---- Player Stats (always-on 3.26 core + configurable Luminary toggles) ----------------------------------------------------

    Dictionary<uint, int> ApplyPlayerStats(Player player, Dictionary<uint, int> sources)
    {
        var s = _settings;

        // 1. Core 3.26 Mercenary unlock (always active for all classes & ascendancies, from level 1)
        EnsureAtLeast(sources, Stat.CanHaveActiveMercenaryFollower, 1);
        EnsureAtLeast(sources, Stat.MercenaryCanUseUniqueRing, 1);
        EnsureAtLeast(sources, Stat.MercenaryCanUseUniqueAmulet, 1);
        EnsureAtLeast(sources, Stat.MercenaryCanUseUniqueBelt, 1);
        EnsureAtLeast(sources, Stat.MercenaryCanUseUniqueHelmet, 1);
        EnsureAtLeast(sources, Stat.MercenaryCanUseUniqueBoots, 1);
        EnsureAtLeast(sources, Stat.MercenaryCanUseUniqueGloves, 1);
        EnsureAtLeast(sources, Stat.MercenaryCanUseUniqueWeaponsShieldsAndQuivers, 1);

        // 2. Noble Blood -30% less Mercenary & Minion Damage penalty (off by default for 3.26 parity)
        if (s.EnableNobleBloodDamagePenalty)
        {
            EnsureAtMost(sources, Stat.PermanenetMercenaryDamagePlusPctFinalAndMinionDamagePlusPctFinalFromLuminary, -30);
        }
        else if (sources.TryGetValue(Stat.PermanenetMercenaryDamagePlusPctFinalAndMinionDamagePlusPctFinalFromLuminary, out int pen) && pen < 0)
        {
            sources[Stat.PermanenetMercenaryDamagePlusPctFinalAndMinionDamagePlusPctFinalFromLuminary] = 0;
        }

        // 3. Oath of Fealty: 50% less Link cost, infinite attachment duration, no owner death on linked mercenary death
        if (s.EnableOathOfFealty)
        {
            EnsureAtMost(sources, Stat.LinkSkillCostPlusPctFinal, -50);
            EnsureAtLeast(sources, Stat.LinkSkillHaveInfiniteAttachmentDuration, 1);
            EnsureAtLeast(sources, Stat.ObjectsLinkedToYourPermanenetMercenariesDoNotDieWhenTheyDie, 1);
        }

        // 4. Golden Glory: Additive Light Radius modifiers apply to Link Skill buff effect on permanent mercenary
        if (s.EnableGoldenGlory)
        {
            EnsureAtLeast(sources, Stat.LightRadiusAdditiveModifiersApplyToBuffEffectOfLinkSkillsIfLinkTargetIsYourPermanentMercenary, 1);
        }

        // 5. Loyal Bodyguard: 20% hit damage taken from nearest permanent mercenary if higher life; 40% recoup if lower life
        if (s.EnableLoyalBodyguard)
        {
            EnsureAtLeast(sources, Stat.DamageRemovedFromYourNearestPermanentMercenaryBeforeLifeOrEsPctIfTheirCurrentLifeIsHigherThanYours, 20);
            EnsureAtLeast(sources, Stat.PermanenetMercenaryDamageTakenRecoupedAsLifePctIfLifeLowerThanParentsLife, 40);
        }

        // 6. Bestowed Knighthood: +50% non-curse aura effect & 100% chance to taunt on hit
        if (s.EnableBestowedKnighthood)
        {
            EnsureAtLeast(sources, Stat.PermanenetMercenaryNonCurseAuraEffectPlusPct, 50);
            EnsureAtLeast(sources, Stat.PermanenetMercenaryChanceToTauntOnHitPct, 100);
        }

        // 7. Legendary Arms: +8% more Mercenary & Minion damage per equipped unique item
        if (s.EnableLegendaryArmsDamageBonus)
        {
            EnsureAtLeast(sources, Stat.PermanenetMercenaryDamagePlusPctFinalAndMinionDamagePlusPctFinalPerEquippedUnique, 8);
        }

        // 8. All 7 Luminary small nodes (+30% Life, +45% Damage, +20% Link Cast Speed, +35% Light Radius).
        // Subtract any already-allocated Luminary small nodes so Luminary Scions do not double-stack while preserving gear/tree bonuses.
        if (s.EnableLuminarySmallNodes)
        {
            var allocated = player.Character.MainAscendancy.Allocated;
            int missingLife = CountMissing(allocated, LuminaryLifeSmallNodes);
            int missingDamage = CountMissing(allocated, LuminaryDamageSmallNodes);
            int missingLinkCast = CountMissing(allocated, LuminaryLinkCastSmallNodes);
            int missingLight = CountMissing(allocated, AllLuminarySmallNodes);

            if (missingLife > 0)
                AddStat(sources, Stat.PermanenetMercenaryMaximumLifePlusPctAndMinionMaximumLifePlusPct, missingLife * 15);
            if (missingDamage > 0)
                AddStat(sources, Stat.PermanenetMercenaryDamagePlusPctAndMinionDamagePlusPct, missingDamage * 15);
            if (missingLinkCast > 0)
                AddStat(sources, Stat.LinkSkillCastSpeedPlusPct, missingLinkCast * 10);
            if (missingLight > 0)
                AddStat(sources, Stat.LightRadiusPlusPct, missingLight * 5);
        }

        return sources;
    }

    // ---- Companion Rules & Unique Body Armour Equip Hooks ----------------------------------------------------------------------

    void ApplyCompanionRules(CompanionRuleCheck rules)
    {
        rules.CanHavePermanentFollower = true;
        if (_settings.AllowUniqueBodyArmour)
            rules.AllowAllUniqueSlots = true;
    }

    void ApplyCompanionEquip(CompanionEquipCheck check)
    {
        if (!check.IsPermanent) return;
        if (check.EquipmentRow == PlayerInventories.BodyArmour)
        {
            if (_settings.AllowUniqueBodyArmour)
                check.AllowUnique = true;
        }
        else
        {
            check.AllowUnique = true;
        }
    }

    // ---- All-Zone Spawning (from Act 1 Mud Flats onward, scaled to player level) -----------------------------------------------

    void ApplyEncounterSpawn(EncounterSpawnRequest request)
    {
        if (!_settings.SpawnInAllZones) return;
        if (!string.Equals(request.Kind, "mercenary", StringComparison.OrdinalIgnoreCase)) return;

        var player = request.Player;
        string areaId = player.Layout.Id;
        if (!IsCombatZoneEligibleForAllZoneSpawn(player, areaId)) return;

        request.Allowed = true;
        request.Force = true;
        request.Chance = 100;
        request.Level = (byte)Math.Clamp(player.Pools.Level, 1, 100);
    }

    bool IsCombatZoneEligibleForAllZoneSpawn(Player player, string areaId)
    {
        if (string.IsNullOrEmpty(areaId) || AlwaysExcludedAreas.Contains(areaId))
            return false;

        if (_areas.TryGetValue(areaId, out var meta) && (meta.IsTown || meta.IsHideout))
            return false;

        if (Game.MapDevices.IsMapArea(player.Layout.Hash))
            return true;

        // Entering The Mud Flats (1_1_3) or any zone beyond The Coast/Tidal Island unlocks all-zone spawning immediately.
        if (IsMudFlatsOrBeyond(areaId))
            return true;

        // Backtracking into The Coast (1_1_2), The Tidal Island (1_1_2a), or Act 1 side areas is allowed once Mud Flats+ was reached.
        return HasCharacterReachedMudFlats(player);
    }

    bool IsMudFlatsOrBeyond(string areaId)
    {
        if (AlwaysExcludedAreas.Contains(areaId) || PreMudFlatsAreas.Contains(areaId))
            return false;
        if (string.Equals(areaId, "1_1_3", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!_areas.TryGetValue(areaId, out var meta))
            return false;
        if (meta.IsTown || meta.IsHideout)
            return false;
        if (meta.Act >= 2)
            return true;
        // Act 1 combat areas from Mud Flats (level 4) onward, excluding Twilight Strand / Coast / Tidal Island.
        return meta.Act == 1 && meta.AreaLevel >= 4;
    }

    bool HasCharacterReachedMudFlats(Player player)
    {
        lock (player.Character.Waypoints)
        {
            foreach (string id in player.Character.Visited)
                if (IsMudFlatsOrBeyond(id)) return true;
            foreach (string id in player.Character.Waypoints)
                if (IsMudFlatsOrBeyond(id)) return true;
        }
        return false;
    }

    // ---- NativeMods Bridge Lifecycle -------------------------------------------------------------------------------------------

    NativeBridgeProtocol.StateSync CurrentSyncState(bool enabled) =>
        new(enabled, enabled && _settings.AllowUniqueBodyArmour);

    Task<bool> HandleRulesetAction(Player player, byte[] message) =>
        _bridge.TryHandleRulesetActionAsync(player, message, () => CurrentSyncState(enabled: true));

    Task OnPlayerEntered(Player player) =>
        _bridge.SendStateAsync(player, CurrentSyncState(enabled: true));

    Task OnClientReady(Player player) =>
        _bridge.StatusOf(player) is { HasNativeHost: true }
            ? Task.CompletedTask
            : _bridge.SendStateAsync(player, CurrentSyncState(enabled: true));

    Task OnPlayerLeaving(Player player, string why)
    {
        _bridge.Remove(player);
        return Task.CompletedTask;
    }

    void NotifyOnlinePlayers(bool enabled)
    {
        var state = CurrentSyncState(enabled);
        foreach (var player in Program.InGame.Keys)
        {
            if (player.Closed || player.LeavingArea) continue;
            Game.MarkPlayerStats(player, PlayerStatStep.Sources);
            _ = _bridge.SendStateAsync(player, state);
        }
    }

    // ---- Chat Commands (/merc326 status | hire | equip — graceful vanilla client fallbacks) ------------------------------------

    async Task HandleCommand(Player player, string[] args)
    {
        string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
        switch (sub)
        {
            case "status":
            case "help":
                await ReplyStatus(player);
                return;
            case "hire":
                await HandleHireFallback(player, args);
                return;
            case "equip":
                await HandleEquipUniqueBodyArmourFallback(player, args);
                return;
            default:
                await player.Commands!.Reply("Usage: /merc326 [status | hire [join|reserve] | equip [1-3]]");
                return;
        }
    }

    Task ReplyStatus(Player player)
    {
        var s = _settings;
        var native = _bridge.StatusOf(player);
        string clientMode = native is { HasMercenaries326Plugin: true }
            ? "NativeMods plugin active (native Hire button + Unique Body Armour drag/Ctrl+click)"
            : native is { HasNativeHost: true }
                ? "NativeMods host connected (mercenaries326.Client.dll not installed; use /merc326 hire & /merc326 equip)"
                : "Vanilla client (use /merc326 hire [join|reserve] & /merc326 equip [1-3])";

        string text = string.Join('\n',
            $"3.26 Mercenaries active — {clientMode}",
            $"Settings: uniqueBodyArmour={OnOff(s.AllowUniqueBodyArmour)}, spawnInAllZones={OnOff(s.SpawnInAllZones)}, " +
            $"nobleBloodPenalty={OnOff(s.EnableNobleBloodDamagePenalty)}, oathOfFealty={OnOff(s.EnableOathOfFealty)}, " +
            $"goldenGlory={OnOff(s.EnableGoldenGlory)}, loyalBodyguard={OnOff(s.EnableLoyalBodyguard)}, " +
            $"bestowedKnighthood={OnOff(s.EnableBestowedKnighthood)}, legendaryArms={OnOff(s.EnableLegendaryArmsDamageBonus)}, " +
            $"luminarySmallNodes={OnOff(s.EnableLuminarySmallNodes)}",
            "Commands: /merc326 status | /merc326 hire [join|reserve] | /merc326 equip [1-3]");
        return player.Commands!.Reply(text);
    }

    async Task HandleHireFallback(Player player, string[] args)
    {
        bool join = args.Length < 2 || !args[1].Equals("reserve", StringComparison.OrdinalIgnoreCase);
        // Synthesize C->S 0x02ca ruleset 0xa1 Hire action [0x02, joinFlag] so MercenaryModule runs the full retail duel & hire flow.
        byte[] packet =
        [
            (byte)(InstanceRulesets.ActionId >> 8),
            (byte)(InstanceRulesets.ActionId & 0xFF),
            InstanceMercenaries.EncounterRulesetRow,
            2,
            0x02,
            (byte)(join ? 1 : 0),
        ];

        bool handled = await GameEvents.AnyPacket(player, InstanceRulesets.ActionId, packet);
        if (!handled)
        {
            await player.Commands!.Reply("No open mercenary encounter found here. Click the area's mercenary to open their window, then run /merc326 hire [join|reserve].");
            return;
        }
        await player.Commands!.Reply(join
            ? "Hire duel requested (join party). Defeat the mercenary to hire them (requires wager gold and an open permanent slot)."
            : "Hire duel requested (send to reserve). Defeat the mercenary to hire them (requires wager gold and an open permanent slot).");
    }

    async Task HandleEquipUniqueBodyArmourFallback(Player player, string[] args)
    {
        if (!_settings.AllowUniqueBodyArmour)
        {
            await player.Commands!.Reply("Unique Body Armour equipping is disabled in mercenaries326 settings (allowUniqueBodyArmour = false).");
            return;
        }

        var roster = LoadRosterSnapshot(player);
        byte? targetKey = null;
        if (args.Length >= 2)
        {
            if (!int.TryParse(args[1], out int slotNum) || slotNum is < 1 or > 3)
            {
                await player.Commands!.Reply("Usage: /merc326 equip [1-3] (optional permanent mercenary slot 1..3).");
                return;
            }
            targetKey = (byte)(slotNum - 1);
        }
        else if (roster?.InvitedKey is byte invited && invited <= 2)
        {
            targetKey = invited;
        }
        else if (roster?.Slots is { Count: > 0 })
        {
            for (byte k = 0; k <= 2; k++)
                if (roster.Slots.ContainsKey(k)) { targetKey = k; break; }
        }

        if (targetKey is not byte key || roster?.Slots is null || !roster.Slots.ContainsKey(key)
            || PlayerInventories.MercenaryRowForKey(key) is not byte mercRow)
        {
            await player.Commands!.Reply("No permanent mercenary found in your roster (slots 1-3). Hire a mercenary first.");
            return;
        }

        var items = Game.ItemsOf(player);
        var catalog = Game.ItemsFor(player);
        bool IsUniqueBodyArmour(Item item) =>
            item.Rarity == ItemRarity.Unique
            && item.Identified
            && catalog.ByHash(item.TypeHash) is { } bt
            && string.Equals(bt.Class, "Body Armour", StringComparison.OrdinalIgnoreCase);

        // 1. Prefer a Unique Body Armour currently held on the cursor.
        if (items.Items(PlayerInventories.Cursor).FirstOrDefault() is { } held && IsUniqueBodyArmour(held.Item))
        {
            Func<Item, PlayerInventories.Shape?> shapeOf = item => catalog.ByHash(item.TypeHash) is { } bt
                ? new PlayerInventories.Shape(bt.Width, bt.Height, bt.Class, bt.MaxStack)
                : null;

            var displaced = items.DisplacedByPlace(mercRow, 0, 0, shapeOf);
            if (GameEvents.FirstInventoryRefusal(player, mercRow, held.Item, displaced, 0, 0) is { } refusal)
            {
                await player.Commands!.Reply($"Cannot equip onto mercenary slot {key + 1}: {refusal}.");
                return;
            }

            var placed = items.Place(mercRow, 0, 0, shapeOf, out string why, player: player);
            if (placed is null)
            {
                await player.Commands!.Reply($"Cannot equip onto mercenary slot {key + 1}: {why}.");
                return;
            }

            await Program.Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(mercRow), items.NextRevision(mercRow),
                placed.Displaced is { } swapped ? [swapped.Id] : [], [placed.Placed]),
                $"0x0041 merc326 equip body armour into row {mercRow}");
            await Program.Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
                items.NextRevision(PlayerInventories.Cursor), [held.Id],
                placed.Displaced is { } outItem ? [outItem with { X = 0, Y = 0 }] : []),
                "0x0041 merc326 cursor after body armour equip");
            await GameEvents.RunInventoryChanged(player, mercRow);
            await player.Commands!.Reply($"Equipped Unique Body Armour onto permanent mercenary slot {key + 1}.");
            return;
        }

        // 2. Otherwise find the first identified Unique Body Armour in Main or Expanded inventory and dispatch C->S 0x010c quick-move.
        InventoryItem? bagItem = items.Items(PlayerInventories.MainInventory).FirstOrDefault(i => IsUniqueBodyArmour(i.Item));
        bool fromExpanded = false;
        if (bagItem is null)
        {
            bagItem = items.Items(PlayerInventories.ExpandedInventory).FirstOrDefault(i => IsUniqueBodyArmour(i.Item));
            fromExpanded = bagItem is not null;
        }
        if (bagItem is null)
        {
            await player.Commands!.Reply("Hold an identified Unique Body Armour on your cursor or keep one in your inventory, then run /merc326 equip [1-3].");
            return;
        }

        var existingBody = items.Items(mercRow).FirstOrDefault(i => i.X == 0);
        if (GameEvents.FirstInventoryRefusal(player, mercRow, bagItem.Item, existingBody, 0, 0) is { } bagRefusal)
        {
            await player.Commands!.Reply($"Cannot equip onto mercenary slot {key + 1}: {bagRefusal}.");
            return;
        }

        byte[] quickMove = new byte[InstanceItems.MercenaryQuickMoveLength];
        BinaryPrimitives.WriteUInt16BigEndian(quickMove.AsSpan(0, 2), InstanceItems.MercenaryQuickMoveId);
        BinaryPrimitives.WriteUInt32BigEndian(quickMove.AsSpan(2, 4), bagItem.Id);
        quickMove[6] = (byte)(fromExpanded ? 1 : 0);
        quickMove[7] = 1; // toMercenary = true
        quickMove[8] = key;

        await GameEvents.AnyPacket(player, InstanceItems.MercenaryQuickMoveId, quickMove);
        bool moved = items.Items(mercRow).Any(i => i.X == 0 && i.Item.TypeHash == bagItem.Item.TypeHash && i.Item.Rarity == ItemRarity.Unique);
        await player.Commands!.Reply(moved
            ? $"Equipped Unique Body Armour onto permanent mercenary slot {key + 1}."
            : $"Could not move Unique Body Armour onto mercenary slot {key + 1} (check inventory space, level, and armour type).");
    }

    static RosterSnapshot? LoadRosterSnapshot(Player player)
    {
        string? json = CharacterSaves.ModuleData(Program.CharacterName(player), RosterSaveKey);
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<RosterSnapshot>(json); }
        catch (JsonException) { return null; }
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------------------------

    static Dictionary<string, AreaMeta> LoadAreas()
    {
        var map = new Dictionary<string, AreaMeta>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in ClientTables.TableOrEmpty("WorldAreas"))
        {
            if (r.TextOrNull("Id") is not { Length: > 0 } id) continue;
            map[id] = new AreaMeta(
                Act: r.IntOrNull("Act") ?? 0,
                AreaLevel: r.IntOrNull("AreaLevel") ?? 1,
                IsTown: r.IntOrNull("IsTown") is > 0,
                IsHideout: r.IntOrNull("IsHideout") is > 0);
        }
        return map;
    }

    static int CountMissing(IReadOnlyList<ushort> allocated, ushort[] nodes)
    {
        int missing = 0;
        for (int i = 0; i < nodes.Length; i++)
            if (!allocated.Contains(nodes[i])) missing++;
        return missing;
    }

    static void EnsureAtLeast(Dictionary<uint, int> sources, uint stat, int minimum)
    {
        if (sources.GetValueOrDefault(stat) < minimum)
            sources[stat] = minimum;
    }

    static void EnsureAtMost(Dictionary<uint, int> sources, uint stat, int maximum)
    {
        if (sources.GetValueOrDefault(stat) > maximum)
            sources[stat] = maximum;
    }

    static void AddStat(Dictionary<uint, int> sources, uint stat, int delta)
    {
        long next = (long)sources.GetValueOrDefault(stat) + delta;
        sources[stat] = (int)Math.Clamp(next, int.MinValue, int.MaxValue);
    }

    static string OnOff(bool value) => value ? "on" : "off";
}
