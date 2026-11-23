using System.Net.Sockets;
using System.Text.Json.Nodes;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core;
using Progression = GameServer.Core.World.Progression;

static partial class GmCommandsRules
{
    // The campaign driver's dev commands (Agents/docs/dev/campaign-automation.md, CampaignState.cs): /state for what the
    // driver sees, /kill for the bosses on the normal death path, /quest_preset to start a quick check at a later act.
    internal static ChatCommands AddCampaignCommands(ChatCommands commands, Player player) =>
        commands
        .Add("state", "/state [brief | all] (dev: one STATE {json} reply for the campaign driver: area, character, quest flags, waypoints and this area's monsters, NPCs, objects, ground items and exits; long replies go on in STATE+ lines; brief leaves out the area's things, all lists every monster)", async args =>
        {
            bool brief = args.Any(a => a.Equals("brief", StringComparison.OrdinalIgnoreCase));
            bool all = args.Any(a => a.Equals("all", StringComparison.OrdinalIgnoreCase));
            foreach (string line in CampaignState.Lines(StateJson(player, brief, all).ToJsonString()))
                await player.Commands!.Reply(line);
        })
        .Add("kill", "/kill <id> | <name> [n] | boss | near [radius] | all (dev: kill monsters here through the normal death path: experience, drops, quest progress, boss stages skipped)", async args =>
        {
            double now = player.Clock.Elapsed.TotalSeconds;
            var targets = CampaignState.KillTargets(player.Area.Monsters, player.Walk.Position(now), now, args, out string? error);
            if (error is not null) { await player.Commands!.Reply(error); return; }
            var killed = new List<string>();
            foreach (var (id, m) in targets)
                if (await KillMonster(player, id, m)) killed.Add($"{m.Label} 0x{id:x}");
            await player.Commands!.Reply(killed.Count == 0 ? "Nothing killed." : $"Killed {killed.Count}: {string.Join(", ", killed)}");
        }, "monster")
        .Add("quest_preset", "/quest_preset <CharacterStartQuestState Id> (dev: add that preset's quest flags and the earlier acts', e.g. /quest_preset Act1Complete)", async args =>
        {
            if (args.Length != 1) throw new ArgumentException("a CharacterStartQuestState Id, e.g. Act1Complete");
            if (CampaignState.PresetFlags(args[0], out string? error) is not { } flags) { await player.Commands!.Reply(error!); return; }
            int before = player.Character.QuestFlagsSnapshot().Count;
            await SetQuestFlags(player, flags);
            int added = player.Character.QuestFlagsSnapshot().Count - before;
            int waypoints = await UnlockPresetWaypoints(player, args[0]);
            var held = await GivePresetQuestItems(player, flags);
            await player.Commands!.Reply($"Preset {args[0]}: {added} of {flags.Count} quest flag(s) added, {waypoints} waypoint(s) unlocked" +
                (held.Count > 0 ? $", quest item(s) held: {string.Join(", ", held)}." : "."));
        });

    // A character that finished an act knows its waypoints; a preset stands in for having played it: the waypoints of every
    // campaign area of the acts the preset's quests belong to (and the earlier ones). Their areas' gates need no check: the
    // presets list story flags only (Act1Complete has no a1q1-KilledHillock), and an activated waypoint brings its area's
    // access flags with it (16-waypoints.md §4). Without them a preset character starts on The Twilight Strand with no way to
    // travel but Act 1's gated exits.
    static async Task<int> UnlockPresetWaypoints(Player player, string presetId)
    {
        var states = QuestStates.Default;
        var preset = states.FindPreset(presetId)
            ?? states.Presets.FirstOrDefault(p => p.Id.Equals(presetId, StringComparison.OrdinalIgnoreCase));
        int act = preset is null ? 0 : Enumerable.Range(1, 11).Where(a => states.QuestsOfAct(a).Intersect(preset.Quests).Any()).DefaultIfEmpty(0).Max();
        // The acts it completes, and the preset's own waypoints (CharacterStartQuestState Areas: where a demo start begins,
        // E3StartOfAct7's The Brine King's Reef, which lists no quests).
        var unlock = PresetWaypointAreas.Value.Where(w => w.Act <= act).Select(w => w.Id).Concat(CampaignState.PresetWaypoints(presetId))
            .Distinct().Where(id => WaypointCatalog.Areas.Entries.Any(e => e.Id == id)).ToList();
        List<string> added;
        if (HardcoreProgressBlocked(player)) return 0;
        lock (player.Character.Waypoints) added = [.. unlock.Where(player.Character.Waypoints.Add)];
        if (added.Count > 0) CharacterSaves.AddWaypoints(CharacterName(player), added);
        if (added.Count > 0) await SendWaypointList(player, $"/quest_preset {presetId}: {added.Count} waypoint(s)");
        return added.Count;
    }

    // The campaign areas with a waypoint (WorldAreas: Id 1_<act>_… or 2_<act>_…, HasWaypoint) and their act.
    static readonly Lazy<List<(string Id, int Act)>> PresetWaypointAreas = new(() =>
    {
        var list = new List<(string, int)>();
        try
        {
            foreach (var r in ClientTables.Table("WorldAreas"))
            {
                string id = r.Text("Id") ?? "";
                if (System.Text.RegularExpressions.Regex.IsMatch(id, @"^[12]_\d+_") && r.Bool("HasWaypoint"))
                    list.Add((id, r.Int("Act")));
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or KeyNotFoundException) { }
        return list;
    });

    // The quest items a preset's character holds: those whose grant_flag the preset sets and whose remove_flag it does not
    // (E3StartOfAct7 has a6q4-HaveRewardBook without a6q4-UsedRewardBook: Lilly's Book of Regrets, not used yet). Given into
    // the main inventory unless one is carried already.
    static async Task<List<string>> GivePresetQuestItems(Player player,
        IReadOnlyCollection<string> presetFlags)
    {
        // A Hardcore character whose death is being recorded gains no items (as a ground-item pickup).
        if (HardcoreProgressBlocked(player)) return [];
        var catalog = ItemsFor(player);
        var items = ItemsOf(player);
        var carried = QuestItems.Carried(items).Select(c => catalog.ByHash(c.Item.Item.TypeHash)?.Id).OfType<string>().ToHashSet();
        var given = new List<string>();
        var added = new List<InventoryItem>();
        foreach (var type in catalog.QuestItemTypes)
        {
            var q = type.Quest!;
            if (q.Grant is not { } grant || !presetFlags.Contains(grant) || q.Remove is { } remove && presetFlags.Contains(remove)
                || carried.Contains(type.Id) || !type.Supported) continue;
            if (items.FreeCell(PlayerInventories.MainInventory, type.Width, type.Height, ShapeOf) is not { } cell) break;
            added.Add(items.Put(PlayerInventories.MainInventory, new InventoryItem(items.NewItemId(), cell.X, cell.Y, ItemCatalog.Create(type))));
            given.Add(type.Name);
        }
        if (added.Count > 0)
            await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
                items.NextRevision(PlayerInventories.MainInventory), [], added), $"0x00bf inventory 1: preset quest items {string.Join(", ", given)}");
        return given;
    }

    // A lethal hit of exactly the monster's life, through the same path a skill hit takes (ApplyMonsterHitLocked,
    // NotifyMonsterHit → MonsterKilled): the death animation, experience within range, drops, quest flags and quest item
    // drops. A boss's stage (Hillock's sword) is skipped: it would leave the boss alive at full life.
    static async Task<bool> KillMonster(Player player, uint id, SpawnedMonster m)
    {
        if (!ReferenceEquals(player.Area.Monsters.GetValueOrDefault(id), m) || m.Dead || m.Life == 0) return false;
        m.StageTaken = true;
        m.StageRunning = false;
        int damage = (int)Math.Min(int.MaxValue, m.Life);
        if (ApplyMonsterHitLocked(player, m, damage) is not { } h) return false;
        await NotifyMonsterHit(player, id, m, h, damage, "/kill");
        return h.Life == 0 || m.ThresholdReached;
    }

    // /god [on | off | immune]: the toggle as before; immune also takes no damage (hits and ailments). Both are kept per
    // character for the Gateway's lifetime (GodCharacters, ImmuneCharacters), so area changes and relogs keep them.
    internal static async Task GodCommand(Player player, string[] args)
    {
        string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        bool on = mode switch
        {
            "" => !player.God, "on" or "immune" => true, "off" => false,
            _ => throw new ArgumentException("on, off or immune"),
        };
        bool immune = on && (mode == "immune" || (mode == "on" && player.Immune));
        // The Righteous Fire burning now, before this command's own sends: the relight below is for that one only.
        player.Immune = immune;
        RighteousFireState? burning = player.RighteousFire;
        if (immune) ImmuneCharacters[CharacterName(player)] = true; else ImmuneCharacters.TryRemove(CharacterName(player), out _);
        await SetGod(player, on);
        // A burning Righteous Fire takes its radius when lit: light it again with the new area.
        await RelightRighteousFire(player, "/god", burning);
        await player.Commands!.Reply(on ? immune ? "God mode on, immune to damage." : "God mode on." : "God mode off.");
    }

    // /state's JSON (the schema is in Agents/docs/dev/campaign-automation.md).
    internal static JsonObject StateJson(Player player, bool brief, bool allMonsters = false)
    {
        double now = player.Clock.Elapsed.TotalSeconds;
        var pos = player.Walk.Position(now);
        static JsonArray Cell(int x, int y) => [x, y];
        // The flags as the client has them: the character's own and the World panel's visit and access flags it is sent.
        var flags = player.Character.QuestFlagsSnapshot();
        flags.UnionWith(Quests.Names(PlayerQuestFlags(player)));
        var state = new JsonObject
        {
            ["v"] = 1,
            ["area"] = player.Layout.Id,
            ["areaName"] = AreaTransitionCatalog.NameOf(player.Layout.Hash),
            ["town"] = InTown(player),
            ["seed"] = player.Seed,
            ["instance"] = player.Ticket,
            ["character"] = CharacterName(player),
            ["classRow"] = player.Character.ClassRow,
            ["level"] = player.Pools.Level,
            ["xp"] = player.Character.Experience,
            ["life"] = player.PlayerLife,
            ["maxLife"] = player.PlayerMaxLife,
            ["dead"] = player.PlayerLife == 0,
            ["god"] = player.God,
            ["immune"] = player.Immune,
            ["pos"] = Cell(pos.X, pos.Y),
            ["passives"] = StatePassives(player),
        };
        // The flags every character is given are left out until earned (EndGameAvailable after Kitava).
        HashSet<string> unearned = [.. player.Character.UnearnedQuestFlags];
        state["flags"] = new JsonArray([.. flags.Where(f => !unearned.Contains(f)).Order(StringComparer.Ordinal).Select(f => JsonValue.Create(f))]);
        lock (player.Character.Waypoints) state["waypoints"] = new JsonArray([.. player.Character.Waypoints.Order(StringComparer.Ordinal).Select(w => JsonValue.Create(w))]);
        state["resistancePenalty"] = ResistancePenalty(player);
        // What the league's other modules add (the Pantheon module's "pantheon").
        GameEvents.RunStateBuilding(player, state);
        // The area's Stash (towns, hideouts): the driver puts what the inventory cannot hold into it (C→S 0xe0).
        state["stash"] = player.StashObject != 0 ? new JsonObject { ["id"] = player.StashObject, ["pos"] = Cell(player.StashAt.X, player.StashAt.Y) } : null;
        state["talking"] = player.TalkingTo;
        state["inTalk"] = player.InNpcTalk;
        state["reward"] = player.NpcRewardOffer is int offer
            ? new JsonObject { ["offer"] = offer, ["items"] = new JsonArray([.. player.NpcRewardItems.Select(kv => StateItem(player, kv.Key, kv.Value))]) }
            : null;
        if (brief) return state;

        var monsters = new JsonArray();
        var listed = player.Area.Monsters.Select(kv => (kv.Key, M: kv.Value, At: kv.Value.Walk.Position(now)))
            .OrderBy(e => Dist(e.At, pos)).ToList();
        // Every boss and living monster near the player; the rest only with /state all (an area has hundreds).
        foreach (var (id, m, at) in listed.Where(e => allMonsters || CampaignState.IsBoss(e.M)
            || (!e.M.Dead && e.M.Life > 0 && Dist(e.At, pos) <= StateMonsterRadius)).Take(allMonsters ? int.MaxValue : StateMonsterLimit))
            monsters.Add(new JsonObject
            {
                ["id"] = id, ["variety"] = m.Variety?.Id, ["name"] = m.Label, ["rarity"] = m.Rarity.ToString(),
                ["alive"] = !m.Dead && m.Life > 0, ["life"] = m.Life, ["hostile"] = m.Hostile, ["pos"] = Cell(at.X, at.Y),
                ["boss"] = CampaignState.IsBoss(m),
            });
        state["monsters"] = monsters;

        var clientFlags = Quests.Names(PlayerQuestFlags(player));
        var npcs = new JsonArray();
        lock (player.Npcs)
            foreach (var (id, n) in player.Npcs)
                npcs.Add(new JsonObject
                {
                    ["id"] = id, ["npc"] = n.Npc.Id, ["name"] = n.Npc.Name, ["pos"] = Cell(n.X, n.Y),
                    ["options"] = new JsonArray([.. Npcs.TalksOf(n.Npc).Where(t => t.Available(clientFlags)).OrderBy(t => t.Index)
                        .Select(t => new JsonObject { ["index"] = t.Index, ["row"] = t.Row, ["text"] = t.Option, ["category"] = t.Category, ["reward"] = t.RewardOffer,
                            // NPCTalk QuestFlag3 all set: the row was heard (of two rows with one text, the other is the quest's next).
                            ["heard"] = t.HeardFlags.Count > 0 && t.HeardFlags.All(clientFlags.Contains) })]),
                });
        state["npcs"] = npcs;
        state["objects"] = new JsonArray([.. StateObjects(player).Select(o => new JsonObject
        {
            ["id"] = o.Id, ["type"] = o.Type, ["name"] = o.Type[(o.Type.LastIndexOf('/') + 1)..], ["pos"] = Cell(o.X, o.Y), ["used"] = o.Used,
        })]);
        // (No instance before the connection entered an area.)
        state["items"] = new JsonArray([.. (player.Instance?.Ground.All() ?? []).Select(g =>
        {
            var item = StateItem(player, g.Id, g.Item.Item);
            item["pos"] = Cell(g.Item.GridX, g.Item.GridY);
            return item;
        })]);
        state["transitions"] = new JsonArray([.. player.Layout.AreaTransitions.Where(t => TransitionShown(player, t.Id, t.Transition)).Select(t => new JsonObject
        {
            ["id"] = t.Id, ["to"] = AreaTransitionCatalog.IdOf(t.Transition.DestinationHash16) ?? $"0x{t.Transition.DestinationHash16:x4}",
            ["name"] = AreaTransitionCatalog.NameOf(t.Transition.DestinationHash16), ["pos"] = Cell(t.Transition.GridX, t.Transition.GridY),
        })]);
        bool active;
        lock (player.Character.Waypoints) active = player.Character.Waypoints.Contains(player.Layout.Id);
        state["waypoint"] = player.Layout.Waypoint is { } w
            ? new JsonObject { ["id"] = WaypointCatalog.ObjectId, ["pos"] = Cell(w.GridX, w.GridY), ["active"] = active } : null;
        var inventory = ItemsOf(player);
        state["inventory"] = new JsonObject
        {
            ["w"] = 12, ["h"] = 5,
            ["items"] = new JsonArray([.. inventory.Items(PlayerInventories.MainInventory).Select(i =>
            {
                var item = StateItem(player, i.Id, i.Item);
                item["x"] = i.X;
                item["y"] = i.Y;
                return item;
            })]),
        };
        state["equipped"] = new JsonObject
        {
            ["weapon"] = inventory.Items(PlayerInventories.Weapon1).Select(i => StateItem(player, i.Id, i.Item)).FirstOrDefault(),
        };
        return state;
    }

    // /state lists the living monsters within this many cells (and every boss), at most StateMonsterLimit of them.
    const double StateMonsterRadius = 120;
    const int StateMonsterLimit = 60;

    static JsonObject StateItem(Player player, uint id, Item item)
    {
        var catalog = ItemsFor(player);
        var type = catalog.ByHash(item.TypeHash);
        var shape = ShapeOf(item);
        return new JsonObject
        {
            ["id"] = id, ["base"] = type?.Id, ["name"] = type is null ? null : catalog.Gem(type) is not null ? catalog.GemName(item) : type.Name,
            ["class"] = type?.Class, ["w"] = shape?.Width ?? 1, ["h"] = shape?.Height ?? 1,
            ["quest"] = type?.Class == "QuestItem",
        };
    }

    // Unspent points and the ordinary passives one more point can take (connected, no keystone, mastery, socket or
    // cluster node): the driver's simple allocation takes the first.
    static JsonObject StatePassives(Player player)
    {
        var tree = PassivesFor(player);
        // The same budget the allocation checks (0x33's extra_skill_points): the quest books' points too.
        int quest = QuestPassivePoints(player);
        int budget = Progression.PassivePoints(player.Pools.Level) + player.Character.ExtraPassivePoints + quest + tree.PointsGranted(player.Character.Passives) + AscendancyOrdinaryPoints(player);
        int unspent = Math.Max(0, budget - player.Character.Passives.Count - player.Character.ClusterPassives.Count);
        var candidates = OrdinaryPassives.Frontier((byte)SkillsFor(player).ClassFor(player.Character.ClassRow).IntegerId,
                player.Character.MainAscendancy.Selection?.Family ?? AscendancyFamily.Normal, player.Character.Passives)
            .Where(n => tree.Find(n) is { IsKeystone: false, IsJewelSocket: false, IsProxy: false, IsExpansion: false, IsMastery: false, IsAscendancy: false })
            .OrderBy(n => tree.Find(n)!.IsNotable).ThenBy(n => n).Take(16);
        return new JsonObject
        {
            ["unspent"] = unspent,
            ["quest"] = quest,
            ["refund"] = (int)player.Character.RefundPoints,
            ["allocated"] = new JsonArray([.. player.Character.Passives.Select(n => JsonValue.Create(n))]),
            ["candidates"] = new JsonArray([.. candidates.Select(n => JsonValue.Create(n))]),
        };
    }

    /// <summary>An object of the area the driver can use: its id, object type, cell, and whether it was used.</summary>
    internal sealed record StateObject(uint Id, string Type, int X, int Y, bool Used);

    // The interactive server objects of the area: the opening's support chest, a1q4's Rhoa Nests and Glyph Wall, the quest
    // engine's chests and objects (used: opened; a quest object once its flags are set). /tp finds them by these ids too.
    internal static List<StateObject> StateObjects(Player player)
    {
        var list = new List<StateObject>();
        if (player.TutorialChestId != 0 && player.TutorialChest is { } chest)
            list.Add(new(player.TutorialChestId, chest.Type, chest.X, chest.Y, CharacterSaves.Load(CharacterName(player))?.Tutorial?.ChestOpened == true));
        lock (player.GlyphNests)
            foreach (var (id, nest) in player.GlyphNests)
                list.Add(new(id, nest.Type, nest.At.X, nest.At.Y, player.Instance.ChestOpened(nest.At.X, nest.At.Y)));
        if (player.GlyphWallId != 0 && player.Glyphs?.Wall is { } wall)
            list.Add(new(player.GlyphWallId, GlyphQuest.Wall, wall.X, wall.Y, ClientHasQuestFlag(player, GlyphQuest.PlacedGlyphs)));
        lock (player.QuestChests)
            foreach (var (id, (rule, at)) in player.QuestChests)
                list.Add(new(id, rule.ChestType, at.X, at.Y, player.Instance.ChestOpened(at.X, at.Y)));
        bool Has(string flag) => ClientHasQuestFlag(player, flag);
        lock (player.QuestObjects)
            foreach (var (id, o) in player.QuestObjects)
            {
                var flags = QuestsOf(player).OnUsed(player.Layout.Id, o.Rule.ObjectType, _ => true);
                bool used = o.Rule.TransitionedBy is { } f ? Has(f) : flags.Count > 0 && flags.All(Has);
                list.Add(new(id, o.Rule.ObjectType, o.At.X, o.At.Y, used));
            }
        return list;
    }
}
