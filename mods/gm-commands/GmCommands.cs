using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using GameServer.Core.Accounts;
using Progression = GameServer.Core.World.Progression;
using DamageModel = GameServer.Core.World.DamageModel;
using DamageType = GameServer.Core.World.DamageType;
using static Program;
using GameServer.Core;

static class GmCommands
{
    /// <summary>The most /item_tab adds of one item type at a time.</summary>
    internal const int ItemTabMax = 1000;

    /// <summary>The most tabs /stash_add leaves in all: the visible roots (a map tab's hidden pages do not count), so 40
    /// by default; Game_STASH_TAB_CAP overrides it without a rebuild, and one call adds at most 10.</summary>
    internal static readonly int StashTabCap =
        int.TryParse(Environment.GetEnvironmentVariable("Game_STASH_TAB_CAP"), out int cap) && cap > 0 ? cap : 40;

    internal const int StashAddMax = 10;

    /// <summary>The /stash command: Game_STASH_PROBE=0 turns it off without a rebuild (the map case is unverified).</summary>
    internal static readonly bool StashProbe = Environment.GetEnvironmentVariable("Game_STASH_PROBE") != "0";

    /// <summary>The /item_tab tab: the one with that name (case ignored), else the only tab of a type whose name starts with
    /// it (e.g. "divination" → DivinationStash); null when none or several match.</summary>
    internal static int? ItemTabOf(AccountStash stash, string name)
    {
        var tabs = Enumerable.Range(0, stash.Count).Where(t => stash.UniqueParent(t) == t).ToList();
        var named = tabs.Where(t => string.Equals(stash.NameOf(t), name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (named.Count > 0) return named[0];
        var typed = tabs.Where(t => stash.TypeOf(t).StartsWith(name, StringComparison.OrdinalIgnoreCase)).ToList();
        return typed.Count == 1 ? typed[0] : null;
    }

    /// <summary>The chat commands' names and usages without a connection: building them only captures it in their
    /// handlers, none of which runs.</summary>

    // /stash_place and the like: <x> <y> <degrees>.
    internal static (int X, int Y, float Degrees) PlaceArgs(string[] args, string usage) =>
        args.Length == 3 && int.TryParse(args[0], out int x) && int.TryParse(args[1], out int y) && float.TryParse(args[2],
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float degrees)
            ? (x, y, degrees) : throw new ArgumentException($"{usage} <x> <y> <degrees>");

    /// <summary>The item specs of an /item_give line: one JSON object, or every element of a JSON array in order.</summary>
    internal static List<string> GiftSpecs(string json)
    {
        var text = json.TrimStart();
        if (!text.StartsWith('[')) return [json];
        using var document = System.Text.Json.JsonDocument.Parse(text);
        return [.. document.RootElement.EnumerateArray().Select(element => element.GetRawText())];
    }

    // A stationary object moved and turned in place (S→C 0x1b7 and the Positioned rotation update, as the hideout editor
    // does), with its path blocker; until the player leaves the area.
    internal static async Task MoveStationary(Player player, uint id,
        int x, int y, float degrees, int blockerSize, string what)
    {
        player.SetBlocker(id, x, y, blockerSize);
        await Send(player, InstanceProtocol.Teleport(id, x, y, 0x7fffffff), $"{what} 0x{id:x} to ({x}, {y})");
        await Send(player, InstanceHideoutEditing.Rotation(id, degrees * MathF.PI / 180), $"{what} turned {degrees}°");
        await player.Commands!.Reply($"{what} at ({x}, {y}), {degrees}°");
    }

    // The stat commands' values: a stat buff on the player's entity in its area (AreaStatBuffs), added like any other
    // source and gone with the area. One owner per command, so /set_speed and /set_aoe never undo /set_stat's stats.
    static readonly object StatBuff = new(), SpeedBuff = new(), AoeBuff = new();

    static void SetBuffStat(Player player, object owner, uint stat, int? value)
    {
        if (player.Frames.Area is not { } area) throw new InvalidOperationException("not in an area");
        area.ChangeStatBuff(new EntityId(player.Id), owner, stats =>
        {
            if (value is int v && v != 0) stats[stat] = v;
            else stats.Remove(stat);
        });
    }

    internal static ChatCommands Register(ChatCommands commands, Player player)
    {
        return commands
        .Add("raw","/raw <hex> (send one S→C message)", async args =>
        {
            byte[] m = ChatCommands.ParseHex(args);
            await Send(player, m, $"0x{m[0] << 8 | m[1]:x4} raw ({m.Length} bytes)");
        })
        .Add("set_speed", "/set_speed <percent> | off (100 = normal: adds percent - 100 to movement_velocity_+% while in this area)", async args =>
        {
            if (args.Length == 0) throw new ArgumentException("percent or off");
            int? percent = args[0].Equals("off", StringComparison.OrdinalIgnoreCase) ? null : int.Parse(args[0]);
            if (percent is < 10 or > 1000) throw new ArgumentException("10 to 1000");
            SetBuffStat(player, SpeedBuff, Stat.MovementVelocityPlusPct, percent - 100);
            await player.Commands!.Reply(percent is int p ? $"Movement speed {p - 100:+0;-0}% in this area." : "Movement speed: the character's own.");
        })
        .Add("blind", "/blind [seconds] (dev: incoming Blind through normal immunity)", async args =>
        {
            double seconds = args.Length == 0 ? 4 : double.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture);
            if (!double.IsFinite(seconds) || seconds <= 0 || seconds > 60) throw new ArgumentException("seconds in (0, 60]");
            bool accepted = Game.InflictPlayerBlind(player, seconds);
            await Game.SendBlind(player);
            await player.Commands!.Reply(accepted ? "Blind applied." : "Blind refused (immune or unavailable).");
        })
        .Add("set_stat", "/set_stat <stat name or client key> <value|off> (adds the value to the stat while in this area; off takes it away)", async args =>
        {
            uint stat = Stats.Key(args[0]) ?? throw new ArgumentException($"unknown stat {args[0]}");
            bool off = args[1].Equals("off", StringComparison.OrdinalIgnoreCase);
            int? value = off ? null : int.Parse(args[1]);
            SetBuffStat(player, StatBuff, stat, value);
            await player.Commands!.Reply(value is int v ? $"{Stats.Name(stat)} {v:+0;-0} in this area." : $"{Stats.Name(stat)}: the character's own.");
        }, "stat")
        .Add("stat", "/stat <stat name or client key> (dev: current character stat value)", async args =>
        {
            if (args.Length != 1 || Stats.Key(args[0]) is not uint stat)
            {
                await player.Commands!.Reply("Usage: /stat <stat name or client key>");
                return;
            }
            int value = CharacterPlayerStats(player).GetValueOrDefault(stat);
            await player.Commands!.Reply($"{args[0]} (key {stat}) = {value}");
        }, "stat")
        .Add("spawn_mob", "/spawn_mob <MonsterVarieties row, Id or name>[@level] [friendly] [normal|magic|rare|unique] [name]", async args =>
        {
            // "friendly": on the player's team (Positioned team 0); monsters are hostile (team 1) otherwise.
            bool friendly = args.Any(a => a.Equals("friendly", StringComparison.OrdinalIgnoreCase));
            args = [.. args.Where(a => !a.Equals("friendly", StringComparison.OrdinalIgnoreCase))];
            if (args.Length == 0) throw new ArgumentException("which monster");
            if (Monsters.Error is { } error) { await player.Commands!.Reply(error); return; }
            // Words up to a rarity keyword name the monster; words after it are a custom name. Only the four names count:
            // Enum.TryParse would also accept "100" as a rarity number and send a byte the client cannot read (Buffer Underflow).
            int r = Array.FindIndex(args, a => a.ToLowerInvariant() is "normal" or "magic" or "rare" or "unique");
            var rarity = r < 0 ? MonsterRarity.Normal : Enum.Parse<MonsterRarity>(args[r], ignoreCase: true);
            string? name = r < 0 || r == args.Length - 1 ? null : string.Join(' ', args[(r + 1)..]);
            if (r == 0) throw new ArgumentException("which monster");
            string query = string.Join(' ', r < 0 ? args : args[..r]);
            byte level = 1;
            if (query.LastIndexOf('@') is int at and > 0)
            {
                level = byte.Parse(query[(at + 1)..]);
                if (level is < 1 or > 100) throw new ArgumentException("level 1 to 100");
                query = query[..at];
            }
            else if (r < 0 && args.Length > 1 && int.TryParse(args[^1], out int bare) && bare is >= 1 and <= 100)
            {
                // A bare number after the monster is the level most people mean (the syntax itself wants @).
                level = (byte)bare;
                query = query[..query.LastIndexOf(' ')];
            }
            if (Monsters.Find(query) is not { } variety) { await player.Commands!.Reply($"No monster matches \"{query}\"."); return; }
            if (!variety.Supported)
            {
                await player.Commands!.Reply($"{variety} is not supported yet: its type has components " +
                    $"{string.Join(", ", variety.ComponentsOrBase.Except(MonsterObject.BaseComponents))} that are not serialized.");
                return;
            }
            var (x, y) = player.SpawnNear(player.Walk.Position(player.Clock.Elapsed.TotalSeconds));
            await SpawnMonster(player, variety, level, rarity, x, y, name, friendly);
        }, "monster")
        .Add("statue", "/statue <MonsterVarieties row, Id or name> [variation] (dev: place that monster's stone statue at the player)", async args =>
        {
            if (args.Length == 0)
            {
                await player.Commands!.Reply("Usage: /statue <MonsterVarieties row, Id or name> [variation]");
                return;
            }
            if (Monsters.Error is { } error) { await player.Commands!.Reply(error); return; }
            // 5 = the stone statue actor variation; 6 the dead pose, 7 another stance (the task reads this as its flag).
            byte variation = 5;
            if (args.Length > 1 && byte.TryParse(args[^1], out byte v)) { variation = v; args = args[..^1]; }
            string query = string.Join(' ', args);
            if (Monsters.Find(query) is not { } variety) { await player.Commands!.Reply($"No monster matches \"{query}\"."); return; }
            var (x, y) = player.Walk.Position(player.Clock.Elapsed.TotalSeconds);
            uint id = player.NextObjectId++;
            await Send(player, InstanceEntities.CreateObject(id,
                InstanceEntities.ObjectTypeHash(InstanceMonsterStatue.TypePath),
                InstanceMonsterStatue.Blob(x, y, variety.Hash16, 100, variation)),
                $"0x02bb monster statue of {variety} at ({x}, {y})");
            await Send(player, InstanceProtocol.EnterWakeZone(id),
                $"0x01a9 monster statue 0x{id:x} enters wake zone");
            await Send(player, InstanceProtocol.AnimationUpdate(id, 0, 0, 1),
                $"0x1b2 monster statue 0x{id:x} idle");
            if (HomeHideout(player)?.AreaHash16 == player.Layout.Hash && CharacterName(player) is { } character)
                CharacterSaves.AddMonsterStatue(character, player.Layout.Id,
                    new CharacterSaves.SavedMonsterStatue(x, y, 0, variety.Hash16, 100, variation));
            await player.Commands!.Reply($"Monster statue of {variety} at ({x}, {y}) (object 0x{id:x}).");
        }, "monster")
        .Add("item", "/item <BaseItemTypes row, Id, name or unique> [count] [magic|rare] [max] (into the inventory, e.g. /item Small Life Flask 2, /item Iron Ring rare, /item Mageblood max; max: as well rolled as it can be, as /item_max)", async args =>
        {
            if (args.Length == 0) throw new ArgumentException("which item?");
            ItemRarity rolled = ItemRarity.Normal;
            bool max = args.Length > 1 && args[^1].Equals("max", StringComparison.OrdinalIgnoreCase);
            if (max) args = args[..^1];
            if (args.Length > 1 && args[^1].ToLowerInvariant() is "magic" or "rare") { rolled = Enum.Parse<ItemRarity>(args[^1], true); args = args[..^1]; }
            int count = 1, requested = 1;
            if (args.Length > 1 && int.TryParse(args[^1], out int n)) { requested = n; count = Math.Clamp(n, 1, 60); args = args[..^1]; }
            if (ItemsFor(player).Error is { } error) { await player.Commands!.Reply(error); return; }
            string query = string.Join(' ', args);
            if (UniquesFor(player).Find(query) is { } unique)
            {
                var uniqueItems = ItemsOf(player);
                var uniqueAdded = new List<InventoryItem>();
                for (int i = 0; i < count; i++)
                {
                    if (UniquesFor(player).Create(unique, Random.Shared) is not { } createdUnique
                        || ItemsFor(player).ByHash(createdUnique.TypeHash) is not { } uniqueType
                        || uniqueItems.FreeCell(PlayerInventories.MainInventory, uniqueType.Width, uniqueType.Height, ShapeOf) is not { } uniqueCell) break;
                    uniqueAdded.Add(uniqueItems.Put(PlayerInventories.MainInventory,
                        new InventoryItem(uniqueItems.NewItemId(), uniqueCell.X, uniqueCell.Y, max ? MaxRolled(player, createdUnique) : createdUnique)));
                }
                if (uniqueAdded.Count == 0) { await player.Commands!.Reply("No room in the inventory"); return; }
                await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
                    uniqueItems.NextRevision(PlayerInventories.MainInventory), [], uniqueAdded),
                    $"0x00bf inventory 1: /item {unique.Name} x {uniqueAdded.Count}");
                await player.Commands!.Reply($"Added {unique.Name}{(max ? ", as well rolled as it can be" : "")}");
                return;
            }
            if (UniquesFor(player).NamedMap(query) is { } legendary)
            {
                var mapItems = ItemsOf(player);
                if (ItemsFor(player).ByHash(legendary.TypeHash) is not { } mapType
                    || mapItems.FreeCell(PlayerInventories.MainInventory, mapType.Width, mapType.Height, ShapeOf) is not { } mapCell)
                { await player.Commands!.Reply("No room in the inventory"); return; }
                var placedMap = mapItems.Put(PlayerInventories.MainInventory,
                    new InventoryItem(mapItems.NewItemId(), mapCell.X, mapCell.Y, legendary));
                await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
                    mapItems.NextRevision(PlayerInventories.MainInventory), [], [placedMap]),
                    "0x00bf inventory 1: /item legendary map");
                await player.Commands!.Reply("Added a legendary map");
                return;
            }
            if (ItemsFor(player).Find(query) is not { } type) { await player.Commands!.Reply($"No item type \"{query}\""); return; }
            if (!type.Supported)
            {
                await player.Commands!.Reply($"{type} has components the server cannot write yet: {string.Join(", ", type.Components.Where(c => !Item.Supported.Contains(c)))}");
                return;
            }
            var items = ItemsOf(player);
            if (type.Stackable)
            {
                // A stackable type: [count] is how many, onto the stacks already there first, then into every free cell (at most
                // a full main inventory, 12 × 5 stacks); what doesn't fit is reported.
                int amount = Math.Clamp(requested, 1, type.MaxStack * 60);
                var stacked = items.AddStacked(PlayerInventories.MainInventory, ItemCatalog.Create(type) with { StackSize = (ushort)Math.Min(amount, ushort.MaxValue) }, ShapeOf);
                if (!stacked.Any) { await player.Commands!.Reply("No room in the inventory"); return; }
                await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
                    items.NextRevision(PlayerInventories.MainInventory), stacked.RemovedIds, stacked.AddedItems),
                    $"0x00bf inventory 1: /item {type} × {amount - stacked.Left} ({stacked.Changed.Count} stack(s) grown, {stacked.Added.Count} new)");
                if (stacked.Left > 0) await player.Commands!.Reply($"No room for {stacked.Left} of them");
                return;
            }
            int itemLevel = Math.Clamp(player.Pools.Level, 1, 100);
            var added = new List<InventoryItem>();
            for (int i = 0; i < count; i++)
            {
                if (items.FreeCell(PlayerInventories.MainInventory, type.Width, type.Height, ShapeOf) is not { } cell) break;
                var (sockets, links) = ItemsFor(player).DefaultSockets(type, itemLevel);
                var created = ItemCatalog.Create(type, sockets, links) with { ItemLevel = (byte)itemLevel };
                if (rolled != ItemRarity.Normal) created = RollsFor(player).Roll(created, rolled, Random.Shared);
                if (max) created = MaxRolled(player, created);
                added.Add(items.Put(PlayerInventories.MainInventory, new InventoryItem(items.NewItemId(), cell.X, cell.Y, created)));
            }
            if (added.Count == 0) { await player.Commands!.Reply("No room in the inventory"); return; }
            await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
                items.NextRevision(PlayerInventories.MainInventory), [], added), $"0x00bf inventory 1: /item {type} × {added.Count}");
        }, "item")
        .Add("item_give", "/item_give <item as JSON> (dev: an item the launcher's item crafter composed, with its mods, into the inventory; a JSON array gives each item in it; Agents/docs/dev/item-crafter.md)", async args =>
        {
            if (args.Length == 0) throw new ArgumentException("which item?");
            if (ItemsFor(player).Error is { } error) { await player.Commands!.Reply(error); return; }
            var items = ItemsOf(player);
            var added = new List<InventoryItem>();
            string? failure = null, singleName = null;
            foreach (string spec in GiftSpecs(string.Join(' ', args)))
            {
                if (ItemCrafter.Build(ItemsFor(player), RollsFor(player), spec, player.Pools.Level, Random.Shared, out string? why) is not { } created
                    || ItemsFor(player).ByHash(created.TypeHash) is not { } type)
                { failure = why ?? "Unknown item type"; break; }
                if (items.FreeCell(PlayerInventories.MainInventory, type.Width, type.Height, ShapeOf) is not { } cell)
                { failure = "No room in the inventory"; break; }
                added.Add(items.Put(PlayerInventories.MainInventory, new InventoryItem(items.NewItemId(), cell.X, cell.Y, created)));
                singleName = $"{(created.NameWords.Count > 0 ? RollsFor(player).NameOf(created.NameWords) + " " : "")}{type.Name}";
            }
            if (added.Count == 0) { await player.Commands!.Reply(failure ?? "No room in the inventory"); return; }
            await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
                items.NextRevision(PlayerInventories.MainInventory), [], added), $"0x00bf inventory 1: /item_give × {added.Count}");
            await player.Commands!.Reply(failure is null
                ? added.Count == 1 && singleName is not null ? $"Added {singleName}" : $"Added {added.Count} item(s)"
                : $"Added {added.Count} item(s), then stopped: {failure}");
        })
        .Add("stash_place", "/stash_place <x> <y> <degrees> (dev: move and turn the stash of this area until you leave it; it opens from its new cell)", async args =>
        {
            if (player.StashObject == 0) { await player.Commands!.Reply("No stash in this area"); return; }
            var (x, y, degrees) = PlaceArgs(args, "/stash_place");
            player.StashAt = (x, y);
            await MoveStationary(player, player.StashObject, x, y, degrees, Player.StashBlockerSize, "stash");
        })
        .Add("guild_stash_place", "/guild_stash_place <x> <y> <degrees> (dev: move and turn the guild stash of this area until you leave it)", async args =>
        {
            if (player.GuildStashObject == 0) { await player.Commands!.Reply("No guild stash in this area"); return; }
            var (x, y, degrees) = PlaceArgs(args, "/guild_stash_place");
            player.GuildStashAt = (x, y);
            await MoveStationary(player, player.GuildStashObject, x, y, degrees, Player.StashBlockerSize, "guild stash");
        })
        .Add("wp_place", "/wp_place [x y degrees] (dev: move and turn this area's waypoint; without arguments shows where it is)", async args =>
        {
            if (player.Layout.Waypoint is not { } waypoint) { await player.Commands!.Reply("This area has no waypoint"); return; }
            if (args.Length == 0)
            {
                await player.Commands!.Reply($"Waypoint at ({waypoint.GridX}, {waypoint.GridY}), {waypoint.Rotation * 180 / MathF.PI:0.#}°");
                return;
            }
            if (args.Length != 3 || !int.TryParse(args[0], out int x) || !int.TryParse(args[1], out int y) || !float.TryParse(args[2],
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float degrees))
                throw new ArgumentException("/wp_place [x y degrees]");
            float radians = degrees * MathF.PI / 180;
            player.MoveWaypoint(waypoint with { GridX = x, GridY = y, Rotation = radians });
            await Send(player, InstanceProtocol.Teleport(WaypointCatalog.ObjectId, x, y, 0x7fffffff), $"waypoint to ({x}, {y})");
            await Send(player, InstanceHideoutEditing.Rotation(WaypointCatalog.ObjectId, radians), $"waypoint turned {degrees}°");
            await player.Commands!.Reply($"Waypoint at ({x}, {y}), {degrees}°");
        })
        .Add("wp_state", "/wp_state active <n> | bits all|none|area|<bit> | model <.ao> (dev: recreate this area's waypoint with StateMachine \"active\" n or another model, or send 0x4c with all, no, this area's or one bit, to find what lights it)", async args =>
        {
            if (player.Layout.Waypoint is not { } waypoint) { await player.Commands!.Reply("This area has no waypoint"); return; }
            string usage = "/wp_state active <n> | bits all|none|area|<bit> | model <.ao>";
            if (args.Length != 2) throw new ArgumentException(usage);
            if (args[0] == "bits")
            {
                int count = WaypointCatalog.Areas.Entries.Count;
                List<int> bits = args[1] switch
                {
                    "all" => [.. Enumerable.Range(0, InstanceWaypoints.BitsetLength * 8)],
                    "none" => [],
                    "area" => WaypointCatalog.Areas.BitOf(player.Layout.Hash) is int own ? [own] : [],
                    var n when int.TryParse(n, out int bit) => [bit],
                    _ => throw new ArgumentException(usage),
                };
                await Send(player, InstanceWaypoints.Waypoints(bits), $"0x004c waypoints: {bits.Count} bits (/wp_state, {count} waypoint areas)");
                await player.Commands!.Reply($"0x4c sent with {bits.Count} bit(s)");
                return;
            }
            var changed = args[0] switch
            {
                "active" when int.TryParse(args[1], out int active) => waypoint with { Active = active },
                "model" => waypoint with { Model = args[1] },
                _ => throw new ArgumentException(usage),
            };
            player.MoveWaypoint(changed);
            await Send(player, InstanceProtocol.RemoveObject(WaypointCatalog.ObjectId), "0x02bd remove the waypoint (/wp_state)");
            await Send(player, InstanceEntities.CreateObject(WaypointCatalog.ObjectId,
                InstanceEntities.ObjectTypeHash(WaypointObject.MetadataPath), changed.Blob()), $"0x02bb create waypoint {changed} (/wp_state)");
            await Send(player, InstanceProtocol.EnterWakeZone(WaypointCatalog.ObjectId), null);
            await player.Commands!.Reply($"Waypoint recreated: active {changed.Active}, model {changed.Model}");
        })
        .Add("item_tab", $"/item_tab <stash tab name> <count> (dev: slotted tabs add up to count, at most {ItemTabMax}; Gem, Flask and Unique add one of each available missing variant, e.g. /item_tab Unique 1)", async args =>
        {
            if (args.Length < 2 || !int.TryParse(args[^1], out int requested) || requested < 1)
                throw new ArgumentException("/item_tab <stash tab name> <count>, e.g. /item_tab Currency 1000");
            if (Items.Error is { } error) { await player.Commands!.Reply(error); return; }
            int amount = Math.Min(requested, ItemTabMax);
            string name = string.Join(' ', args[..^1]);
            var stash = PersonalStashOf(player);
            if (ItemTabOf(stash, name) is not int tab)
            {
                await player.Commands!.Reply($"No stash tab \"{name}\"; tabs: {string.Join(", ", Enumerable.Range(0, stash.Count).Select(stash.NameOf))}");
                return;
            }
            if (stash.TabType(tab) == StashRules.Flask)
            {
                var flasks = stash.FillFlasks(tab, ItemsFor(player), ShapeOf, out string flaskWhy);
                if (flasks is null) { await player.Commands!.Reply($"Tab \"{stash.NameOf(tab)}\": {flaskWhy}"); return; }
                if (flasks.Added.Count > 0)
                    await SendTabChange(player, stash, tab, [], flasks.Added,
                        $"0x00bf stash tab {tab}: /item_tab {stash.NameOf(tab)}, {flasks.Added.Count} flask(s)");
                await player.Commands!.Reply($"Tab \"{stash.NameOf(tab)}\" (FlaskStash): {flasks.Added.Count} flask(s) added, {flasks.Full} already present, {flasks.Skipped} unavailable; one of each flask/tincture base and unique");
                return;
            }
            if (stash.TabType(tab) == StashRules.Unique)
            {
                var collection = stash.FillUniques(tab, UniquesFor(player), ShapeOf);
                await Game.RefreshUniqueCollection(player, stash, tab);
                await player.Commands!.Reply($"Tab \"{stash.NameOf(tab)}\": {collection.Added} unique(s) added, {collection.Present} already present, {collection.Skipped.Count} unavailable.");
                if (collection.Skipped.Count > 0) Console.WriteLine($"[gateway] /item_tab unique unavailable: {string.Join(", ", collection.Skipped)}");
                return;
            }
            if (stash.TabType(tab) == StashRules.Gem)
            {
                // The gem tab: one of every creatable gem variant (transfigured and Vaal too), red, green, blue then white,
                // actives before supports; one already there is not added again.
                var gems = new[] { 1, 2, 3, 4 }.SelectMany(colour => new[] { false, true }.SelectMany(support => Items.FullShopGems(colour, support)))
                    .Select(g => ItemCatalog.Create(g.Type) with { GemVariant = g.Variant.Index });
                var cells = stash.FillCells(tab, gems, i => $"{i.TypeHash:x8}:{i.GemVariant}", ShapeOf, out string gemWhy);
                if (cells is null) { await player.Commands!.Reply($"Tab \"{stash.NameOf(tab)}\": {gemWhy}"); return; }
                if (cells.Added.Count > 0)
                    await SendTabChange(player, stash, tab, [], cells.Added,
                        $"0x00bf stash tab {tab}: /item_tab {stash.NameOf(tab)}, {cells.Added.Count} gem(s)");
                await player.Commands!.Reply($"Tab \"{stash.NameOf(tab)}\" (GemStash): {cells.Added.Count} gem(s) added, one of each variant" +
                    (cells.Full > 0 ? $"; {cells.Full} already there" : "") + (cells.Skipped > 0 ? $"; {cells.Skipped} found no room" : ""));
                return;
            }
            Item? Create(uint hash) => Items.ByHash(hash) is { Supported: true } type ? ItemCatalog.Create(type) : null;
            if (stash.FillSlots(tab, amount, Create, ShapeOf, out string why) is not { } fill)
            {
                await player.Commands!.Reply($"Tab \"{stash.NameOf(tab)}\": {why}");
                return;
            }
            var now = fill.Changed.Concat(fill.Added).ToList();
            if (now.Count > 0)
                await SendTabChange(player, stash, tab,
                    [.. fill.Changed.Select(i => i.Id)], now, $"0x00bf stash tab {tab}: /item_tab {stash.NameOf(tab)} {amount}, {now.Count} slot(s)");
            await player.Commands!.Reply($"Tab \"{stash.NameOf(tab)}\" ({stash.TypeOf(tab)}): up to {amount} more of {now.Count} item type(s)" +
                (requested > amount ? $" (at most {ItemTabMax} at a time)" : "") +
                (fill.Full > 0 ? $"; {fill.Full} slot(s) already full" : "") +
                (fill.Skipped > 0 ? $"; {fill.Skipped} type(s) skipped (the server can't create them or has no cell for their slot)" : ""));
        })
        .Add("stash_add", $"/stash_add <normal|premium|quad> [count] (dev: append 1-{StashAddMax} general stash tabs, at most {StashTabCap} in all; re-enter the area or relog to see them)", async args =>
        {
            if (args.Length is < 1 or > 2) throw new ArgumentException("/stash_add <normal|premium|quad> [count]");
            byte type = args[0].ToLowerInvariant() switch
            {
                "normal" or "0" => 0,
                "premium" or "1" => 1,
                "quad" or "7" => 7,
                _ => throw new ArgumentException($"only the general tabs: normal, premium, quad (not \"{args[0]}\"); the special tabs hold one kind of item"),
            };
            int count = 1;
            if (args.Length == 2 && (!int.TryParse(args[1], out count) || count is < 1 or > StashAddMax))
                throw new ArgumentException($"count is 1-{StashAddMax}");
            var stash = PersonalStashOf(player);
            if (stash.VisibleCount + count > StashTabCap)
            {
                await player.Commands!.Reply($"Refused: the stash has {stash.VisibleCount} visible tab(s) and at most {StashTabCap} are allowed (asked for {count}).");
                return;
            }
            // The tab's name and colour are this command's choice, not the stash's.
            (string Name, byte R, byte G, byte B) look = type switch
            {
                0 => ("Normal", (byte)0x7c, (byte)0x54, (byte)0x36),
                1 => ("Premium", (byte)0x2c, (byte)0x6e, (byte)0xc8),
                _ => ("Quad", (byte)0x8a, (byte)0x2b, (byte)0xe2),
            };
            var added = stash.AddTabs(type, count, look.Name, look.R, look.G, look.B);
            string kind = type switch { 0 => "NormalStash", 1 => "PremiumStash", _ => "QuadStash" };
            Console.WriteLine($"[gateway] {player.Connection.Remote} /stash_add {kind} {count} -> tabs {string.Join(",", added)} ({stash.VisibleCount} visible in all, account {player.Character.Account ?? CharacterName(player)}, league {player.Character.League ?? "Standard"})");
            await player.Commands!.Reply($"Added {added.Count} {kind} tab(s) {string.Join(", ", added)}; {stash.VisibleCount}/{StashTabCap} tabs. Re-enter the area (or relog) to see them.");
        })
        .Add("stash", "/stash (open the stash panel where the character stands: S→C 0x7b kind 1 on the area's stash object)", async args =>
        {
            if (!StashProbe) { await player.Commands!.Reply("/stash is off (Game_STASH_PROBE=0)"); return; }
            uint objectId = player.StashObject;
            await Send(player, InstanceWaypoints.OpenUi(InstanceStash.OpenStashUi, objectId, 0),
                $"0x007b open the stash on /stash (object 0x{objectId:x}, no walk)");
            Console.WriteLine($"[gateway] {player.Connection.Remote} /stash 0x7b kind {InstanceStash.OpenStashUi} object 0x{objectId:x} in {player.Layout.Id}");
            await player.Commands!.Reply($"Sent the stash panel request (object 0x{objectId:x}). No panel means the client wants a real stash object in this area.");
        })
        .Add("adorned", "/adorned [0-1000] (dev: The Adorned unique jewel into the inventory, corrupted, for passive-jewel testing; the normal item range is 0-100)", async args =>
        {
            int percent = args.Length > 0 && int.TryParse(args[0], out int value) ? Math.Clamp(value, 0, 1000) : 100;
            if (Items.Find("Crimson Jewel") is not { } baseType) { await player.Commands!.Reply("The Crimson Jewel base type is missing"); return; }
            if (UniqueJewelVisuals.Of("The Adorned") is not ushort visual) { await player.Commands!.Reply("The Adorned visual identity is missing"); return; }
            if (Items.Mods.ById("CorruptedMagicJewelModEffectUnique__1") is not { } mod) { await player.Commands!.Reply("The Adorned modifier is missing"); return; }
            var items = ItemsOf(player);
            if (items.FreeCell(PlayerInventories.MainInventory, 1, 1, ShapeOf) is not { } cell) { await player.Commands!.Reply("No room in the inventory"); return; }
            var jewel = ItemCatalog.Create(baseType) with
            {
                Rarity = ItemRarity.Unique, Corrupted = true, ItemLevel = 20, VisualIdentity = visual,
                Mods = [new ItemMod(ModSection.Explicit, mod.Hash, [percent])],
            };
            var added = items.Put(PlayerInventories.MainInventory, new InventoryItem(items.NewItemId(), cell.X, cell.Y, jewel));
            await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
                items.NextRevision(PlayerInventories.MainInventory), [], [added]), $"0x00bf inventory 1: /adorned {percent}%");
            await player.Commands!.Reply($"The Adorned ({percent}%) added to the inventory");
        })
        .Add("unique_jewel", "/unique_jewel <name> | list (dev: a global unique jewel, max-rolled, into the inventory)", async args =>
        {
            if (args.Length == 0 || args[0].Equals("list", StringComparison.OrdinalIgnoreCase))
            {
                await player.Commands!.Reply(string.Join(", ", UniqueJewelCatalogue.Names));
                return;
            }
            if (CreateUniqueJewel(string.Join(' ', args)) is not { } jewel)
            {
                await player.Commands!.Reply($"No global unique jewel \"{string.Join(' ', args)}\"");
                return;
            }
            var items = ItemsOf(player);
            if (items.FreeCell(PlayerInventories.MainInventory, 1, 1, ShapeOf) is not { } cell)
            {
                await player.Commands!.Reply("No room in the inventory");
                return;
            }
            var added = items.Put(PlayerInventories.MainInventory, new InventoryItem(items.NewItemId(), cell.X, cell.Y, jewel));
            await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
                items.NextRevision(PlayerInventories.MainInventory), [], [added]), $"0x00bf inventory 1: /unique_jewel {string.Join(' ', args)}");
            await player.Commands!.Reply($"{string.Join(' ', args)} added to the inventory");
        })
        .Add("radius_jewel", "/radius_jewel <name> | list (dev: a radius unique jewel, max-rolled, into the inventory; pilot: the six attribute-conversion jewels)", async args =>
        {
            if (args.Length == 0 || args[0].Equals("list", StringComparison.OrdinalIgnoreCase))
            {
                await player.Commands!.Reply(string.Join(", ", RadiusJewelCatalogue.Names));
                return;
            }
            if (RadiusJewelCatalogue.FindWithVariant(string.Join(' ', args)) is not { } found
                || RadiusJewelCatalogue.Create(ItemsFor(player), found.Entry, found.Variant) is not { } jewel)
            {
                await player.Commands!.Reply($"No radius unique jewel \"{string.Join(' ', args)}\"");
                return;
            }
            var items = ItemsOf(player);
            if (items.FreeCell(PlayerInventories.MainInventory, 1, 1, ShapeOf) is not { } cell)
            {
                await player.Commands!.Reply("No room in the inventory");
                return;
            }
            var added = items.Put(PlayerInventories.MainInventory, new InventoryItem(items.NewItemId(), cell.X, cell.Y, jewel));
            await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
                items.NextRevision(PlayerInventories.MainInventory), [], [added]), $"0x00bf inventory 1: /radius_jewel {string.Join(' ', args)}");
            await player.Commands!.Reply($"{string.Join(' ', args)} added to the inventory");
        })
        .Add("impossible_escape", "/impossible_escape <keystone> | list (dev: Impossible Escape for that keystone, into the inventory)", async args =>
        {
            if (args.Length == 0 || args[0].Equals("list", StringComparison.OrdinalIgnoreCase))
            {
                await player.Commands!.Reply(string.Join(", ", ImpossibleEscapeCatalogue.Names));
                return;
            }
            string query = string.Join(' ', args);
            if (ImpossibleEscapeCatalogue.Create(ItemsFor(player), query) is not { } jewel)
            {
                await player.Commands!.Reply($"No Impossible Escape keystone \"{query}\"");
                return;
            }
            var items = ItemsOf(player);
            if (items.FreeCell(PlayerInventories.MainInventory, 1, 1, ShapeOf) is not { } cell)
            {
                await player.Commands!.Reply("No room in the inventory");
                return;
            }
            var added = items.Put(PlayerInventories.MainInventory, new InventoryItem(items.NewItemId(), cell.X, cell.Y, jewel));
            await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
                items.NextRevision(PlayerInventories.MainInventory), [], [added]), $"0x00bf inventory 1: /impossible_escape {query}");
            bool keystoneAllocated = ImpossibleEscapeCatalogue.Find(query) is { } keystone
                && player.Character.Passives.Contains(keystone.GraphId);
            await player.Commands!.Reply($"Impossible Escape ({query}) added to the inventory" +
                (keystoneAllocated ? "" : ". Allocate the chosen keystone first; the ring and bypass only apply around an allocated keystone."));
        })
        .Add("timeless", "/timeless <name> [seed] [conqueror] | list (dev: a Legion timeless jewel into the inventory; Elegant Hubris seeds are the displayed x20 values 2000-160000)", async args =>
        {
            if (args.Length == 0 || args[0].Equals("list", StringComparison.OrdinalIgnoreCase))
            {
                await player.Commands!.Reply(string.Join("; ", TimelessJewelCatalog.All.Select(e =>
                    $"{e.Name} {e.DisplayedMinSeed}-{e.DisplayedMaxSeed}: " +
                    string.Join(", ", e.Conquerors.Select(c => c.Name)))));
                return;
            }
            var tokens = args.ToList();
            string? conquerorText = null;
            if (tokens.Count > 1)
            {
                string last = tokens[^1];
                if ((int.TryParse(last, out int index) && index is >= 1 and <= 3)
                    || TimelessJewelCatalog.All.Any(e => e.Conquerors.Any(c => c.Name.Equals(last, StringComparison.OrdinalIgnoreCase))))
                {
                    conquerorText = last;
                    tokens.RemoveAt(tokens.Count - 1);
                }
            }
            int? seed = null;
            if (tokens.Count > 1 && int.TryParse(tokens[^1], out int parsedSeed))
            {
                seed = parsedSeed;
                tokens.RemoveAt(tokens.Count - 1);
            }
            if (TimelessJewelCatalog.Find(string.Join(' ', tokens)) is not { } entry)
            {
                await player.Commands!.Reply($"No timeless jewel \"{string.Join(' ', tokens)}\"");
                return;
            }
            if (seed is int asked && (asked < entry.DisplayedMinSeed || asked > entry.DisplayedMaxSeed
                || entry.DisplayMultiplier > 1 && asked % entry.DisplayMultiplier != 0))
            {
                await player.Commands!.Reply($"{entry.Name} seed is {entry.DisplayedMinSeed}-{entry.DisplayedMaxSeed}" +
                    (entry.DisplayMultiplier > 1 ? $" in steps of {entry.DisplayMultiplier}" : ""));
                return;
            }
            int? conqueror = null;
            if (conquerorText is not null)
            {
                if (TimelessJewelCatalog.FindConqueror(entry, conquerorText) is not { } chosen)
                {
                    await player.Commands!.Reply($"{entry.Name} conqueror is one of " +
                        string.Join(", ", entry.Conquerors.Select(c => c.Name)));
                    return;
                }
                conqueror = chosen.Index;
            }
            if (TimelessJewelCatalog.Create(ItemsFor(player), entry.Name, seed, conqueror) is not { } jewel)
            {
                await player.Commands!.Reply("The timeless jewel data is missing");
                return;
            }
            var items = ItemsOf(player);
            if (items.FreeCell(PlayerInventories.MainInventory, 1, 1, ShapeOf) is not { } cell)
            {
                await player.Commands!.Reply("No room in the inventory");
                return;
            }
            var added = items.Put(PlayerInventories.MainInventory, new InventoryItem(items.NewItemId(), cell.X, cell.Y, jewel));
            await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
                items.NextRevision(PlayerInventories.MainInventory), [], [added]), $"0x00bf inventory 1: /timeless {entry.Name}");
            var spec = TimelessJewelCatalog.Read(jewel, ItemsFor(player).Mods);
            await player.Commands!.Reply($"{entry.Name} (seed {spec?.DisplayedSeed}, {spec?.ConquerorName}) added to the inventory");
        })
        .Add("voices", "/voices [1|3|5|7] (dev: the Voices unique Large Cluster Jewel with that many empty passives, into the inventory)", async args =>
        {
            int empty = args.Length > 0 && int.TryParse(args[0], out int value) ? value : 7;
            if (CreateVoices(empty) is not { } jewel)
            {
                await player.Commands!.Reply("Voices needs 1, 3, 5 or 7 empty passives");
                return;
            }
            var items = ItemsOf(player);
            if (items.FreeCell(PlayerInventories.MainInventory, 1, 1, ShapeOf) is not { } cell)
            {
                await player.Commands!.Reply("No room in the inventory");
                return;
            }
            var added = items.Put(PlayerInventories.MainInventory, new InventoryItem(items.NewItemId(), cell.X, cell.Y, jewel));
            await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
                items.NextRevision(PlayerInventories.MainInventory), [], [added]), $"0x00bf inventory 1: /voices {empty}");
            await player.Commands!.Reply($"Voices ({empty} empty passive(s)) added to the inventory");
        })
        .Add("cluster_jewel", "/cluster_jewel <small|medium|large> [passives] [sockets] [theme] [mod ...] (dev: a non-unique cluster jewel into the inventory. theme: its name with underscores, e.g. fire_damage, or none; without one it gets one of its size's. mod: Mods Ids of its affixes, e.g. a notable's. Defaults: 3/4/8 passives and 0/1/2 sockets)", async args =>
        {
            int size = args.Length > 0 ? args[0].ToLowerInvariant() switch
            {
                "small" => 0,
                "medium" => 1,
                "large" => 2,
                _ => -1,
            } : -1;
            if (size < 0)
            {
                await player.Commands!.Reply("small, medium or large");
                return;
            }
            int passives = args.Length > 1 && int.TryParse(args[1], out int p) ? p : size switch { 2 => 8, 1 => 4, _ => 3 };
            int sockets = args.Length > 2 && int.TryParse(args[1], out _) && int.TryParse(args[2], out int s) ? s : size switch { 2 => 2, 1 => 1, _ => 0 };
            // The numbers may be left out: every word that is not one is a theme or a mod.
            int numbers = args.Skip(1).TakeWhile(a => int.TryParse(a, out _)).Take(2).Count();
            if (CreateClusterJewel(size, passives, sockets, args.Skip(1 + numbers), out string? why) is not { } jewel)
            {
                await player.Commands!.Reply(why ?? "The cluster jewel data is missing");
                return;
            }
            var items = ItemsOf(player);
            if (items.FreeCell(PlayerInventories.MainInventory, 1, 1, ShapeOf) is not { } cell)
            {
                await player.Commands!.Reply("No room in the inventory");
                return;
            }
            var added = items.Put(PlayerInventories.MainInventory, new InventoryItem(items.NewItemId(), cell.X, cell.Y, jewel));
            await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
                items.NextRevision(PlayerInventories.MainInventory), [], [added]), $"0x00bf inventory 1: /cluster_jewel {size} {passives} {sockets}");
            string themeName = ItemsFor(player).ByHash(jewel.TypeHash) is { } made && ClusterJewels.ThemeOf(made, jewel) is { } theme ? theme.Name : "no theme";
            await player.Commands!.Reply($"Cluster jewel ({args[0]}, {themeName}, {passives} passive(s), {sockets} socket(s)) added to the inventory");
        })
        .Add("corrupt", "/corrupt (dev: marks the held item corrupted, e.g. to make a corrupted magic jewel)", async _ =>
        {
            var items = ItemsOf(player);
            if (items.Items(PlayerInventories.Cursor) is not [var held, ..]) { await player.Commands!.Reply("Hold an item first"); return; }
            if (items.Change(PlayerInventories.Cursor, held.Id, it => it with { Corrupted = true }) is not { } changed) return;
            await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
                items.NextRevision(PlayerInventories.Cursor), [held.Id], [changed]), "0x00bf cursor: corrupted");
            await player.Commands!.Reply("Corrupted");
        })
        .Add("gem", "/gem <name> [level] [quality] (a gem into the inventory at that level and quality, e.g. /gem Fireball 20 20, /gem Ice Nova of Frostbolts, /gem Vaal Fireball 5)", async args =>        {
            // The trailing numbers: the level, then the quality.
            var numbers = new List<int>();
            while (args.Length > 1 && numbers.Count < 2 && int.TryParse(args[^1], out int n)) { numbers.Insert(0, n); args = args[..^1]; }
            if (args.Length == 0) throw new ArgumentException("which gem?");
            if (ItemsFor(player).Error is { } error) { await player.Commands!.Reply(error); return; }
            string query = string.Join(' ', args);
            if (ItemsFor(player).FindGem(query) is not var (type, variant) || ItemsFor(player).Gem(type) is not { } gem) { await player.Commands!.Reply($"No gem \"{query}\""); return; }
            if (!type.Supported)
            {
                await player.Commands!.Reply($"{variant.Name} has components the server cannot write yet: {string.Join(", ", type.Components.Where(c => !Item.Supported.Contains(c)))}");
                return;
            }
            int level = numbers.Count > 0 ? numbers[0] : 1, quality = numbers.Count > 1 ? numbers[1] : 0;
            // Corruption (one level over the cap, quality up to 23%) is not modelled yet: the uncorrupted limits.
            if (level < 1 || level > gem.MaxLevel) { await player.Commands!.Reply($"{variant.Name} has levels 1 to {gem.MaxLevel}"); return; }
            if (quality is < 0 or > 20) { await player.Commands!.Reply("Quality is 0 to 20"); return; }
            var items = ItemsOf(player);
            if (items.FreeCell(PlayerInventories.MainInventory, type.Width, type.Height, ShapeOf) is not { } cell) { await player.Commands!.Reply("No room in the inventory"); return; }
            var created = ItemCatalog.Create(type) with { GemExperience = gem.ExperienceFor(level), GemVariant = variant.Index, Quality = (byte)quality };
            var added = items.Put(PlayerInventories.MainInventory, new InventoryItem(items.NewItemId(), cell.X, cell.Y, created));
            await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
                items.NextRevision(PlayerInventories.MainInventory), [], [added]),
                $"0x00bf inventory 1: /gem {variant.Name} (variant {variant.Index}) level {level}, {quality}% quality, experience {created.GemExperience}");
        }, "gem")
        .Add("gem_xp", "/gem_xp <amount|near> (dev: set the experience of every gem in the inventory and the sockets; near = one below the next level's threshold)", async args =>
        {
            if (args.Length != 1) throw new ArgumentException("an amount, or near");
            bool near = args[0].Equals("near", StringComparison.OrdinalIgnoreCase);
            int amount = near ? 0 : int.Parse(args[0]);
            var items = ItemsOf(player);
            int Set(Item gem, ItemCatalog.GemType type)
            {
                int level = ItemsFor(player).GemLevel(gem);
                int max = Math.Min(SkillsFor(player).MaxLevelAt(type.Variant(gem.GemVariant).Effect, player.Pools.Level), type.MaxLevel);
                return near ? (level < max ? type.ExperienceFor(level + 1) - 1 : gem.GemExperience) : amount;
            }
            var changed = new List<(byte Row, InventoryItem Item)>();
            foreach (var held in items.Items(PlayerInventories.MainInventory))
                if (ItemsFor(player).ByHash(held.Item.TypeHash) is { } baseType && ItemsFor(player).Gem(baseType) is { } type)
                    if (items.Change(PlayerInventories.MainInventory, held.Id, it => it with { GemExperience = Set(it, type) }) is { } after)
                        changed.Add((PlayerInventories.MainInventory, after));
            foreach (var (row, item, _) in items.ChangeSocketedGems((_, gem) =>
                ItemsFor(player).ByHash(gem.TypeHash) is { } baseType && ItemsFor(player).Gem(baseType) is { } type ? gem with { GemExperience = Set(gem, type) } : null))
                changed.Add((row, item));
            if (changed.Count == 0) { await player.Commands!.Reply("No gem to change"); return; }
            foreach (var row in changed.GroupBy(c => c.Row))
                await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(row.Key), items.NextRevision(row.Key),
                    [.. row.Select(c => c.Item.Id)], [.. row.Select(c => c.Item)]),
                    $"0x00bf inventory {row.Key}: /gem_xp {(near ? "near" : amount.ToString())} ({string.Join(", ", row.Select(c => $"{c.Item.Id:x} exp {c.Item.Item.GemExperience}"))})");
        })
        .Add("map_portal", "/map_portal [type 1-4] [created 0|1] [open after ms, -1 never] [model 0-2] (dev: one MultiplexPortal next to you, listed in the map device's 0x8f)", async args =>
        {
            if (args.Length > 3) player.MapPortalStyle = byte.Parse(args[3]);
            byte type = args.Length > 0 ? byte.Parse(args[0]) : MapPortalObject.MapDevicePortal;
            byte created = args.Length > 1 ? byte.Parse(args[1]) : (byte)1;
            int openMs = args.Length > 2 ? int.Parse(args[2]) : -1;
            var (x, y) = player.SpawnNear(player.Walk.Position(player.Clock.Elapsed.TotalSeconds));
            uint id = player.NextObjectId++;
            await Send(player, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(MapPortalObject.MetadataPath),
                new MapPortalObject(x, y, 0, type, created, Rotation: player.PortalRotationRadians).Blob()),
                $"0x02bb create test portal 0x{id:x} type {type}, transitionable {created}, at ({x}, {y})");
            await Send(player, InstanceProtocol.EnterWakeZone(id), null);
            lock (player.MapPortals) player.MapPortals[id] = (x, y, -1);
            if (openMs >= 0) OpenMapPortalLater(player, id, openMs);
            uint deviceId;
            lock (player.HideoutDoodads) deviceId = player.HideoutDoodads.Keys.FirstOrDefault();
            if (deviceId != 0) SendMapPortalsLater(player, deviceId, MapDevice.OpenOf(CharacterName(player))?.Area.Hash16 ?? player.Layout.Hash, MapPortalListMs);
            await player.Commands!.Reply($"Portal 0x{id:x}: type {type}, created {created}, " + (openMs >= 0 ? $"opens after {openMs} ms" : "not opened"));
        })
        .Add("cosmetic", "/cosmetic portal <name, number or part of a name> | /cosmetic list [filter] | /cosmetic off (the portal cosmetic: the shop effect replaces the base portal visual)", async args =>
        {
            if (args.Length == 0)
            {
                await player.Commands!.Reply($"{Cosmetics.Portals.Count} portal effects. " +
                    "/cosmetic list <part of a name> lists the ones that have it, /cosmetic portal <number or name> applies one.");
                return;
            }
            string what = args[0].ToLowerInvariant();
            if (what is not ("list" or "portal" or "off")) throw new ArgumentException("list, portal or off");
            if (what == "off")
            {
                player.Character.CosmeticPortalVariation = 0;
                CharacterSaves.SetPortalCosmetic(CharacterName(player), 0);
                bool back = await ResendMapPortals(player);
                await player.Commands!.Reply(back
                    ? "Portal cosmetic off: the open portals are back to the base effect."
                    : "Portal cosmetic off: the next portal uses the base effect.");
                return;
            }
            string filter = string.Join(' ', args.Skip(1).Where(a => !a.Equals("portal", StringComparison.OrdinalIgnoreCase)));
            if (what == "list")
            {
                var shown = filter.Length == 0 ? Cosmetics.Portals : CosmeticCatalog.Matches(Cosmetics.Portals, filter);
                await player.Commands!.Reply(shown.Count == 0
                    ? Cosmetics.Portals.Count == 0 ? "No portal effects: data/tables is missing." : $"No portal effects match \"{filter}\"."
                    : $"{shown.Count} portal effects: " + string.Join(", ", shown.Take(12).Select(c => c.ToString())) + (shown.Count > 12 ? ", ..." : ""));
                return;
            }
            if (filter.Length == 0)
            {
                // Nothing named: the first page of the list, so the numbers that pick one are right there.
                await player.Commands!.Reply($"{Cosmetics.Portals.Count} portal effects: " + string.Join(", ", Cosmetics.Portals.Take(12)) +
                    (Cosmetics.Portals.Count > 12 ? ", ..." : "") + " — use a number, a name or a piece of one.");
                return;
            }
            var matches = CosmeticCatalog.Matches(Cosmetics.Portals, filter);
            if (matches.Count == 0) throw new ArgumentException($"no portal effect matches \"{filter}\" (try /cosmetic list {filter})");
            if (matches.Count > 1)
            {
                // Several contain what was typed: name them with the numbers that pick one, rather than guessing which was
                // meant. The next /cosmetic with one of the numbers (or a longer fragment) applies it.
                await player.Commands!.Reply($"{matches.Count} portal effects match \"{filter}\": " +
                    string.Join(", ", matches.Take(12).Select(c => c.ToString())) + (matches.Count > 12 ? ", ..." : "") +
                    ". Add more of the name, or use one of the numbers.");
                return;
            }
            var cosmetic = matches[0];
            // A portal cosmetic is the portal's own variation (+0x26 of its 0x008f entry), not a second object: the client
            // swaps the base portal effect for the shop one.
            player.Character.CosmeticPortalVariation = (ushort)cosmetic.Index;
            CharacterSaves.SetPortalCosmetic(CharacterName(player), player.Character.CosmeticPortalVariation);   // survives a relog
            bool applied = await ResendMapPortals(player);
            await player.Commands!.Reply($"portal: {cosmetic.Name} (variation {cosmetic.Index})" +
                (applied ? ", applied to the open portals" : ", the next portal uses it"));
        })
        .Add("portal_rotation", "/portal_rotation <degrees> (dev: turn the map portals around the vertical axis; rebuilds the ones standing)", async args =>
        {
            if (args.Length == 0) throw new ArgumentException("degrees");
            player.PortalRotationDegrees = float.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture);
            int rebuilt = await RebuildMapPortals(player);
            await player.Commands!.Reply(rebuilt == 0
                ? $"Map portals will be created at {player.PortalRotationDegrees:0.#}°."
                : $"{rebuilt} map portal(s) rebuilt at {player.PortalRotationDegrees:0.#}°.");
        })
        .Add("item_mod", "/item_mod [implicit|explicit|enchant|scourge|hidden] <Mods row, Id or part of it> [values…] | /item_mod set [section] <mod> [values…] | /item_mod remove [section] <mod> | /item_mod clear [section] (the held item; missing values are rolled)", async args =>
        {
            if (args.Length == 0) throw new ArgumentException("which mod?");
            ModSection? section = null;
            if (args[0].Equals("set", StringComparison.OrdinalIgnoreCase))
            {
                args = args[1..];
                if (args.Length == 0) throw new ArgumentException("which mod?");
                ModSection? setSection = null;
                if (args.Length > 1 && ParseSection(args[0]) is { } parsed) { setSection = parsed; args = args[1..]; }
                if (ItemsFor(player).Mods.Find(args[0]) is not { } setMod) { await player.Commands!.Reply($"No mod \"{args[0]}\" in Mods.dat"); return; }
                if (ItemsOf(player).Items(PlayerInventories.Cursor) is not [var held, ..]) { await player.Commands!.Reply("Hold an item first"); return; }
                var setValues = args[1..].Select(int.Parse).ToList();
                var replacement = setMod.With(setSection ?? setMod.DefaultSection, setValues, Random.Shared);
                int existing = held.Item.Mods.Select((m, index) => (m, index))
                    .Where(p => p.m.Hash == replacement.Hash && (setSection is null || p.m.Section == setSection))
                    .Select(p => p.index).DefaultIfEmpty(-1).First();
                if (existing < 0) { await player.Commands!.Reply($"The held item has no {setMod.Id}; use /item_mod to add it"); return; }
                var targetSection = held.Item.Mods[existing].Section;
                replacement = replacement with { Section = targetSection };
                await ChangeHeldItem(player, i =>
                {
                    var mods = i.Mods.ToList();
                    int index = mods.FindIndex(m => m.Hash == replacement.Hash && m.Section == targetSection);
                    if (index >= 0) mods[index] = replacement;
                    return i with { Mods = mods };
                }, $"set {ItemsFor(player).Mods.Describe(replacement)}");
                return;
            }
            if (args[0].Equals("remove", StringComparison.OrdinalIgnoreCase))
            {
                args = args[1..];
                if (args.Length == 0) throw new ArgumentException("which mod?");
                ModSection? removeSection = null;
                if (args.Length > 1 && ParseSection(args[0]) is { } parsed) { removeSection = parsed; args = args[1..]; }
                if (ItemsFor(player).Mods.Find(args[0]) is not { } removeMod) { await player.Commands!.Reply($"No mod \"{args[0]}\" in Mods.dat"); return; }
                if (ItemsOf(player).Items(PlayerInventories.Cursor) is not [var held, ..]) { await player.Commands!.Reply("Hold an item first"); return; }
                if (!ItemModEdits.Contains(held.Item, removeMod.Hash, removeSection)) { await player.Commands!.Reply($"The held item has no {removeMod.Id}"); return; }
                var removed = held.Item.Mods.First(m => m.Hash == removeMod.Hash && (removeSection is null || m.Section == removeSection));
                await ChangeHeldItem(player,
                    i => ItemModEdits.Remove(i, removeMod.Hash, removeSection),
                    $"removed {ItemsFor(player).Mods.Describe(removed)}");
                return;
            }
            if (args.Length > 1 && ParseSection(args[0]) is { } s) { section = s; args = args[1..]; }
            if (args[0].Equals("clear", StringComparison.OrdinalIgnoreCase))
            {
                var only = args.Length > 1 ? ParseSection(args[1]) : section;
                await ChangeHeldItem(player, i => i with { Mods = [.. i.Mods.Where(m => only is { } o && m.Section != o)] },
                    $"mods cleared{(only is { } o ? $" ({o})" : "")}");
                return;
            }
            if (ItemsFor(player).Mods.Find(args[0]) is not { } mod) { await player.Commands!.Reply($"No mod \"{args[0]}\" in Mods.dat"); return; }
            var values = args[1..].Select(int.Parse).ToList();
            var added = mod.With(section ?? mod.DefaultSection, values, Random.Shared);
            await ChangeHeldItem(player, i => i with { Mods = [.. i.Mods, added] }, $"+ {ItemsFor(player).Mods.Describe(added)}");
        })
        .Add("roll", "/roll <magic|rare> [item level] (new affixes and name for the held item)", async args =>
        {
            if (args.Length == 0 || !Enum.TryParse<ItemRarity>(args[0], true, out var rarity) || rarity is not (ItemRarity.Magic or ItemRarity.Rare))
                throw new ArgumentException("magic or rare");
            byte? level = args.Length > 1 ? byte.Parse(args[1]) : null;
            await ChangeHeldItem(player, i => RollsFor(player).Roll(level is byte l ? i with { ItemLevel = l } : i, rarity, Random.Shared),
                $"rolled {rarity}");
        })
        .Add("item_max", "/item_max (the held item as well rolled as it can be: every implicit and explicit value at the top of its range, a magic or rare item's affixes at their best tier for its item level, the top base defence roll on armour; a value that picks something, as a Timeless Jewel's seed, stays)", async _ =>
            await ChangeHeldItem(player, i => MaxRolled(player, i), "as well rolled as it can be"))
        .Add("item_mods", "/item_mods (the held item's rarity and mods)", async args =>
        {
            if (ItemsOf(player).Items(PlayerInventories.Cursor) is not [var held, ..]) { await player.Commands!.Reply("Hold an item first"); return; }
            await player.Commands!.Reply($"{(held.Item.NameWords.Count > 0 ? RollsFor(player).NameOf(held.Item.NameWords) + ", " : "")}" +
                $"{ItemsFor(player).ByHash(held.Item.TypeHash)?.Name} ilvl {held.Item.ItemLevel}, {held.Item.Rarity}" +
                $"{(held.Item.Identified ? "" : ", unidentified")}: " +
                (held.Item.Mods.Count == 0 ? "no mods" : string.Join("; ", held.Item.Mods.Select(ItemsFor(player).Mods.Describe))));
        })
        .Add("base_defence", "/base_defence [0 to 31] (show or set the held armour's shared defence base roll)", async args =>
        {
            if (ItemsOf(player).Items(PlayerInventories.Cursor) is not [var held, ..])
            { await player.Commands!.Reply("Hold an armour item first"); return; }
            if (!held.Item.Components.Contains("Armour"))
            { await player.Commands!.Reply("The held item has no Armour component"); return; }
            if (args.Length == 0)
            { await player.Commands!.Reply($"Defence base roll: {held.Item.DefenceBaseRoll} (ordinary range 0 to 31)"); return; }
            if (args.Length != 1 || !byte.TryParse(args[0], out byte roll) || roll > Item.MaximumDefenceBaseRoll)
                throw new ArgumentException("defence base roll 0 to 31");
            await ChangeHeldItem(player, i => i with { DefenceBaseRoll = roll },
                $"defence base roll {roll} / {Item.MaximumDefenceBaseRoll}", "Armour");
        })
        .Add("item_state", "/item_state [corrupted|mirrored|split|synthesised|shaper|elder|crusader|redeemer|hunter|warlord|clear …] (switches them on the held item; without a state, shows them)", async args =>
        {
            if (ItemsOf(player).Items(PlayerInventories.Cursor) is not [var held, ..]) { await player.Commands!.Reply("Hold an item first"); return; }
            if (args.Length == 0) { await player.Commands!.Reply(ItemStates.Describe(held.Item)); return; }
            var result = args.Aggregate(held.Item, (item, state) => ItemStates.Toggle(item, state) ?? throw new ArgumentException($"no state {state}"));
            await ChangeHeldItem(player, i => args.Aggregate(i, (item, state) => ItemStates.Toggle(item, state)!),
                ItemStates.Describe(result), args.Any(ItemStates.NeedsMods) ? "Mods" : "Base");
        })
        .Add("rarity", "/rarity <normal|magic|rare|unique> [unid] (the held item)", async args =>
        {
            if (args.Length == 0 || !Enum.TryParse<ItemRarity>(args[0], true, out var rarity)) throw new ArgumentException("normal, magic, rare or unique");
            bool identified = !args.Skip(1).Any(a => a.StartsWith("unid", StringComparison.OrdinalIgnoreCase));
            await ChangeHeldItem(player, i => i with { Rarity = rarity, Identified = identified },
                $"{rarity}{(identified ? "" : ", unidentified")}");
        })
        .Add("sockets", "/sockets [colours] (the held item's sockets: R G B W, linked within a word, e.g. /sockets RRGBBB a 6-link, /sockets RRG B a 3-link and a single; without colours, shows them; Game_SOCKET_CAPACITY=1..6 lets any socketable base take that many, e.g. a 6-link wand)", async args =>
        {
            if (ItemsOf(player).Items(PlayerInventories.Cursor) is not [var held, ..]) { await player.Commands!.Reply("Hold an item first"); return; }
            if (args.Length == 0) { await player.Commands!.Reply($"sockets: {SocketLayout.Describe(held.Item)}"); return; }
            if (ItemsFor(player).ByHash(held.Item.TypeHash) is not { } type) { await player.Commands!.Reply("Unknown item type"); return; }
            if (SocketLayout.Parse(args, out string? error) is not var (colours, links)) throw new ArgumentException(error);
            int capacity = SocketLayout.CapacityOverride;
            if (SocketLayout.Apply(held.Item, type, colours, links, out error, capacity) is not { } result) { await player.Commands!.Reply(error!); return; }
            await ChangeHeldItem(player, i => SocketLayout.Apply(i, type, colours, links, out _, capacity) ?? i,
                $"sockets: {SocketLayout.Describe(result)}", "Sockets");
        })
        .Add("set_level", "/set_level <1 to 100> (saved to the account; the level's experience and stats are sent at once)", async args =>
        {
            if (args.Length == 0 || !int.TryParse(args[0], out int level) || level is < 1 or > 100) throw new ArgumentException("level 1 to 100");
            await SetLevel(player, level);
            var r = await SynchronizeAccountLevel(player);
            if (!r.Ok) await player.Commands!.Reply($"Level saved locally; account update failed: {r.Error}");
        })
        .Add("add_xp", "/add_xp <amount> (experience as from a kill: levels up, saved)", async args =>
        {
            if (args.Length == 0 || !uint.TryParse(args[0], out uint xp) || xp == 0) throw new ArgumentException("an amount of experience");
            await GainExperience(player, xp, "/add_xp");
        })
        .Add("ascendancy", "/ascendancy [normal|phrecia] [name] (list or choose an available ascendancy)", async args =>
        {
            byte baseClass = (byte)Skills.ClassFor(player.Character.ClassRow).IntegerId;
            var choices = Ascendancies.Choices(baseClass).Where(c => AscendancyPolicies.Available(player, c.Identity)).ToArray();
            if (choices.Length == 0) throw new ArgumentException("ascendancy data is missing; run scripts/dev/extract-data.ps1");
            if (args.Length == 0)
            {
                var current = Game.AscendancyRules.Validate(baseClass, player.Character.MainAscendancy);
                int spent = Game.AscendancyRules.SpentPoints(baseClass, player.Character.MainAscendancy);
                await player.Commands!.Reply($"Main ascendancy - {current?.Name ?? "none"}. Ascendancy points {spent}/{player.Character.MainAscendancy.EarnedPoints}.\n" +
                    string.Join("\n", choices.Select(c => $"/ascendancy {c.Identity.Family.Name().ToLowerInvariant()} {c.Name}")));
                return;
            }
            bool hasFamily = AscendancyFamilies.TryNamed(args[0], out var family);
            string name = string.Join(' ', hasFamily ? args[1..] : args);
            var matching = choices.Where(c => (!hasFamily || c.Identity.Family == family) &&
                (c.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || c.Identity.Id.Equals(name, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (matching.Length != 1) throw new ArgumentException("choose one name from /ascendancy, including its normal/phrecia family when needed");
            var chosen = matching[0];
            AscendancyClientCompatibility.RequirePlayable(player, chosen.Identity);
            if (player.Character.MainAscendancy.Selection == chosen.Identity)
            {
                await player.Commands!.Reply($"{chosen.Name} is already your main ascendancy.");
                return;
            }
            Game.SelectMainAscendancy(player, chosen);
            await player.Commands!.Reply($"Main ascendancy selected - {chosen.Name}. Reloading the area to show its tree.");
            await ChangeArea(player, new PendingTransfer(player.Layout.Hash, 0, false), $"ascendancy {chosen.Name}");
        })
        .Add("ascendancy_points", "/ascendancy_points <normal|cruel|merciless|eternal|all> (dev: grant each Labyrinth's two ascendancy points once)", async args =>
        {
            if (args.Length != 1) throw new ArgumentException("a Labyrinth tier or all");
            string tier = args[0].ToLowerInvariant();
            int earned = await Game.AwardAscendancyPoints(player, tier == "all" ? global::AscendancyRules.AwardKeys : [tier]);
            await player.Commands!.Reply($"Ascendancy points earned - {earned}; each tier can be earned once.");
        })
        .Add("reset_ascendancy", "/reset_ascendancy (refund all ascendancy nodes; five refund points per node/effect, choice shells are free)", async _ =>
        {
            if (player.Character.MainAscendancy.Allocated.Count == 0) { await player.Commands!.Reply("No ascendancy passives allocated."); return; }
            var error = await ChangePassives(player, player.Character.MainAscendancy.Allocated.ToArray(), false, "/reset_ascendancy", refundPoints: true);
            await player.Commands!.Reply(error ?? "Ascendancy passives refunded; you can choose another main ascendancy.");
        })
        .Add("descendancy", "/descendancy [name|none|open [row|name]|refund <node|all>] (dev: the second ascendancy layer 0x33's spare byte selects: Affliction's Wildwood specialisations and the 3.29 Bloodline classes; open shows the client's Descendancy class selector)", async args =>
        {
            if (Game.Descendancies.Rows.Count <= 1) throw new ArgumentException("Descendancy data is missing; run scripts/dev/extract-data.ps1");
            // One Azmeri class at a time: a class flag left from an earlier selection keeps that Wildwood row unlocked
            // for the game's own selector after the character has moved on. The point flags stay, as earned progress.
            async Task ClearAzmeriClass(byte keep)
            {
                string[] stale = [.. new byte[] { 1, 2, 3 }.Where(r => r != keep)
                    .Select(r => DescendancyCatalog.UnlockFlag(r)).OfType<string>().Where(player.Character.HasQuestFlag)];
                if (stale.Length > 0) await SetQuestFlags(player, stale, on: false);
            }
            if (args.Length == 0)
            {
                var current = Game.Descendancies.Rows.FirstOrDefault(r => r.Index == player.Character.DescendancyRow);
                await player.Commands!.Reply($"Descendancy - {(current is { Index: > 0 } ? current.Name : "none")}.\n" +
                    string.Join("\n", Game.Descendancies.Rows.Where(r => r.Index != 0).Select(r => $"/descendancy {r.Id}")));
                return;
            }
            if (args[0].Equals("open", StringComparison.OrdinalIgnoreCase))
            {
                // 0x7b kind 0x4c: the client's Descendancy class selector, with the Descendancy.dat row as its first u32.
                byte descRow = args.Length > 1
                    ? Game.Descendancies.Find(args[1])?.Index ?? byte.Parse(args[1])
                    : player.Character.DescendancyRow;
                if (args.Length > 1 && Game.Descendancies.Rows.All(r => r.Index != descRow))
                    throw new ArgumentException("no such Descendancy row");
                if (descRow == 0)
                    throw new ArgumentException("no Descendancy chosen; pass a row name or number, for example /descendancy open 6");
                string descName = Game.Descendancies.Rows.FirstOrDefault(r => r.Index == descRow)?.Name ?? "?";
                await player.Commands!.Reply($"0x7b kind 0x4c row {descRow} ({descName})");
                await Game.OpenDescendancySelector(player, descRow);
                return;
            }
            if (args[0].Equals("refund", StringComparison.OrdinalIgnoreCase))
            {
                if (player.Character.DescendancyNodes.Count == 0) { await player.Commands!.Reply("No descendancy nodes are allocated."); return; }
                ushort[] nodes = args.Length > 1 && !args[1].Equals("all", StringComparison.OrdinalIgnoreCase)
                    ? [ushort.Parse(args[1])]
                    : [.. player.Character.DescendancyNodes];
                foreach (ushort n in nodes)
                    if (!player.Character.DescendancyNodes.Contains(n)) throw new ArgumentException($"{n} is not an allocated descendancy node");
                string? refundError = await ChangePassives(player, nodes, allocate: false,
                    "/descendancy refund", refundPoints: true);
                await player.Commands!.Reply(refundError ?? $"Refunded {nodes.Length} descendancy node(s).");
                return;
            }
            if (args[0].Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                Game.SelectDescendancyForDevelopment(player, 0);
                await ClearAzmeriClass(keep: 0);
                await player.Commands!.Reply("Descendancy cleared. Reloading the area to show the change.");
                await ChangeArea(player, new PendingTransfer(player.Layout.Hash, 0, false), "descendancy none");
                return;
            }
            string name = string.Join(' ', args);
            var row = Game.Descendancies.Find(name) ?? throw new ArgumentException("choose one Id from /descendancy");
            // Affliction's Wildwood rows keep their own gate in the client: the Azmeri class flag and the four point flags
            // unlock the tree and its eight points (the 3.29 Bloodline rows use the 0x33 byte alone).
            string[] azmeri = row.Index switch
            {
                1 => ["AzmeriAreWarden", "WardenPassivePoints1", "WardenPassivePoints2", "WardenPassivePoints3", "WardenPassivePoints4"],
                2 => ["AzmeriAreVoodooShaman", "ShamanPassivePoints1", "ShamanPassivePoints2", "ShamanPassivePoints3", "ShamanPassivePoints4"],
                3 => ["AzmeriArePrimalist", "PrimalistPassivePoints1", "PrimalistPassivePoints2", "PrimalistPassivePoints3", "PrimalistPassivePoints4"],
                _ => [],
            };
            Game.SelectDescendancyForDevelopment(player, row.Index);
            await ClearAzmeriClass(keep: row.Index);
            if (azmeri.Length > 0) await SetQuestFlags(player, azmeri);
            await player.Commands!.Reply($"Descendancy selected - {row.Name} (row {row.Index})" +
                (azmeri.Length > 0 ? " with its Azmeri flags" : "") + ". Reloading the area to show its tree.");
            await ChangeArea(player, new PendingTransfer(player.Layout.Hash, 0, false), $"descendancy {row.Name}");
        })
        .Add("gold", "/gold <amount> (dev: add gold, or take it with a negative amount; the account's, saved)", async args =>
        {
            if (args.Length == 0 || !long.TryParse(args[0], out long delta)) throw new ArgumentException("an amount of gold (negative takes gold)");
            uint gold = PersonalStashOf(player).AddGold(delta);
            await Send(player, InstanceEntities.Gold(gold), $"0x02aa gold: {gold} (/gold {delta})");
            await player.Commands!.Reply($"Gold: {gold}");
        })
        .Add("add_passive_points", "/add_passive_points <n> (dev: add n extra passive skill points, saved)", async args =>
        {
            if (args.Length == 0 || !int.TryParse(args[0], out int add) || add <= 0) throw new ArgumentException("a positive number");
            ushort total = Saves.Atomically(() =>
            {
                ushort current = CharacterSaves.Load(CharacterName(player))?.ExtraPassivePoints ?? 0;
                ushort next = (ushort)Math.Min(ushort.MaxValue, (long)current + add);
                CharacterSaves.SetExtraPassivePoints(CharacterName(player), next);
                return next;
            });
            player.Character.ExtraPassivePoints = total;
            await SendPassives(player);
            await player.Commands!.Reply($"{add} extra passive point(s) added; {total} extra total.");
        })
        .Add("add_refund_points", "/add_refund_points <n> (dev: add n refund points, saved)", async args =>
        {
            if (args.Length == 0 || !int.TryParse(args[0], out int add) || add <= 0) throw new ArgumentException("a positive number");
            ushort total = Saves.Atomically(() =>
            {
                ushort next = (ushort)Math.Min(ushort.MaxValue, (CharacterSaves.Load(CharacterName(player))?.RefundPoints ?? 0) + add);
                CharacterSaves.SetRefundPoints(CharacterName(player), next);
                return next;
            });
            player.Character.RefundPoints = total;
            player.Character.SavedPassiveProgress = new(player.Character.MainAscendancy, player.Character.Passives, player.Character.MasteryEffects, total);
            await SendPassives(player);
            await player.Commands!.Reply($"{add} refund point(s) added; {total} total.");
        })
        .Add("add_passive", "/add_passive <PassiveSkillGraphId> [...] (dev: allocate a connected set of passive nodes)", async args =>
        {
            if (args.Length == 0 || args.Any(a => !ushort.TryParse(a, out _))) throw new ArgumentException("PassiveSkillGraphId numbers");
            ushort[] nodes = [.. args.Select(ushort.Parse)];
            string? error = await ChangePassives(player, nodes, allocate: true, "/add_passive");
            await player.Commands!.Reply(error ?? $"Passives {string.Join(", ", nodes)} allocated.");
        })
        .Add("reset_passives", "/reset_passives (refunds every passive, saved)", async _ =>
        {
            var (_, _, nodes, _) = PassiveState(player);
            await ChangePassives(player, nodes, allocate: false, "/reset_passives");
            await player.Commands!.Reply($"{nodes.Count} passive(s) refunded.");
        })
        .Add("resistances", "/resistances (your uncapped and capped fire/cold/lightning/chaos resistance)", async _ =>
        {
            var p = player.Pools;
            int penalty = ResistancePenalty(player);
            int fire = p.FireResistance + penalty, cold = p.ColdResistance + penalty;
            int lightning = p.LightningResistance + penalty, chaos = p.ChaosResistance + penalty;
            await player.Commands!.Reply(
                $"Kitava penalty {penalty}%. " +
                $"Fire {fire}% (passives {p.FireResistance}%, max {p.FireMaxResistance}%, capped {DamageModel.Capped(fire, p.FireMaxResistance)}%), " +
                $"Cold {cold}% (passives {p.ColdResistance}%, max {p.ColdMaxResistance}%, capped {DamageModel.Capped(cold, p.ColdMaxResistance)}%), " +
                $"Lightning {lightning}% (passives {p.LightningResistance}%, max {p.LightningMaxResistance}%, capped {DamageModel.Capped(lightning, p.LightningMaxResistance)}%), " +
                $"Chaos {chaos}% (passives {p.ChaosResistance}%, max {p.ChaosMaxResistance}%, capped {DamageModel.Capped(chaos, p.ChaosMaxResistance)}%).");
        })
        .Add("hit", "/hit <fire|cold|lightning|chaos|physical> <damage> [ranged] [freeze] (dev: incoming enemy test contact through resistances or armour)", async args =>
        {
            if (args.Length < 2) throw new ArgumentException("a damage type and an amount");
            if (ParseDamageType(args[0]) is not { } type) throw new ArgumentException("fire, cold, lightning, chaos or physical");
            if (!int.TryParse(args[1], out int amount) || amount <= 0) throw new ArgumentException("a positive amount");
            if (!TrySyntheticEnemyHit(player, type, amount, false, false, false, out var hit,
                ranged: args.Length > 2 && args[2] == "ranged"))
            {
                await player.Commands!.Reply(player.PlayerLife == 0 ? "You are already dead." : "Enemy test hit rejected while paused or leaving.");
                return;
            }
            if (args.Contains("freeze"))
            {
                double now = player.Clock.Elapsed.TotalSeconds, before = player.ElementalAilments.ActionFactor(now);
                var stats = CharacterPlayerStats(player);
                player.ElementalAilments.Inflict(PlayerElementalAilments.Kind.Freeze, 100, 1, now,
                    key => stats.GetValueOrDefault(key), player.IncomingHitRandom);
                PlayerAilmentsChangedLocked(player, before, now);
            }
            int dealt = hit.Damage; string mitigation = hit.Mitigation; uint life = hit.Life;
            await SendPlayerEnergyShield(player);
            if (life == 0)
            {
                SetStats(player, player.Id, [(Stat.HideMiniLifeBar, 1)]);
                Note("0x01b3 player hide_mini_life_bar = 1 (dev hit)");
            }
            Note($"0x01b1 player dev {type} hit for {dealt} ({amount} before {mitigation}), life {life}/{player.PlayerMaxLife}");
            if (hit.Mana != hit.ManaBefore)
                await SendPlayerMana(player, $"0x01b1 player mana {hit.Mana} ({(long)hit.Mana - hit.ManaBefore:+0;-0})");
            if (life == 0)
                await PlayerDied(player, $"0x0055 death screen ({player.Character.League ?? "?"} league, dev hit)");
            await player.Commands!.Reply($"{type}: {amount} → {dealt} after {mitigation}; life {life}/{player.PlayerMaxLife}" +
                $"{(hit.Mana != hit.ManaBefore ? $", mana {hit.Mana} ({(long)hit.Mana - hit.ManaBefore:+0;-0})" : "")}.");
        })
        .Add("suppression", "/suppression (Spell Suppression chance, roll and damage reduction)", async _ =>
        {
            var (chance, effect, lucky, unlucky) = Suppression(player);
            double effective = DamageModel.EffectiveSuppressionChance(chance, lucky, unlucky);
            await player.Commands!.Reply($"Spell Suppression: {chance}% chance (cap {DamageModel.SpellSuppressionChanceCap}%, effective {effective:0.#}%" +
                $"{(lucky ? ", lucky" : "")}{(unlucky ? ", unlucky" : "")}), {effect}% less damage on a suppressed spell hit.");
        })
        .Add("suppression_luck", "/suppression_luck <lucky|unlucky|off> (dev: force a lucky/unlucky suppression roll)", async args =>
        {
            string mode = args.Length == 0 ? "" : args[0].ToLowerInvariant();
            bool lucky = mode == "lucky", unlucky = mode == "unlucky";
            if (mode is not ("lucky" or "unlucky" or "off" or "")) throw new ArgumentException("lucky, unlucky or off");
            player.ForceSpellSuppressionLucky = lucky;
            player.ForceSpellSuppressionUnlucky = unlucky;
            player.Pools = PoolsFor(player, player.Pools.Level);
            await SendCharacterStats(player, PlayerStatChange.Sources);
            await player.Commands!.Reply($"Spell Suppression roll: {(lucky ? "lucky" : unlucky ? "unlucky" : "normal")}.");
        })
        .Add("accuracy", "/accuracy [monster level] (accuracy rating and attack hit chance; default: your level)", async args =>
        {
            int level = player.Pools.Level;
            if (args.Length > 0)
            {
                if (!int.TryParse(args[0], out int requested) || requested < 1 || requested > Progression.MaxLevel)
                    throw new ArgumentException($"monster level 1 to {Progression.MaxLevel}");
                level = requested;
            }
            int evasion = Monsters.BaseEvasion(level) ?? 0;
            int evasiveEvasion = Monsters.BaseEvasiveEvasion(level) ?? 0;
            int chance = DamageModel.AccuracyHitChance(evasion, player.Pools.Accuracy);
            int evasiveChance = DamageModel.AccuracyHitChance(evasiveEvasion, player.Pools.Accuracy);
            var withMastery = player.Pools.AccuracyMoreVsUniquePct != 0 || player.Pools.AccuracyMoreInCloseRangePct != 0
                ? $" Targets gain {player.Pools.AccuracyMoreVsUniquePct:0.#}% more accuracy against unique enemies and " +
                  $"{player.Pools.AccuracyMoreInCloseRangePct:0.#}% more within the 20-unit close range, applied per attack roll."
                : "";
            await player.Commands!.Reply($"Accuracy {player.Pools.Accuracy} ({player.Pools.AccuracyPct}% increased): " +
                $"{chance}% chance to hit a level {level} monster (evasion {evasion}), " +
                $"{evasiveChance}% against evasive monsters (evasion {evasiveEvasion}). Spells always hit." + withMastery);
        })
        .Add("defences", "/defences [monster level] (armour, evasion and avoidance; default: your level)", async args =>
        {
            int level = player.Pools.Level;
            if (args.Length > 0)
            {
                if (!int.TryParse(args[0], out int requested) || requested < 1 || requested > Progression.MaxLevel)
                    throw new ArgumentException($"monster level 1 to {Progression.MaxLevel}");
                level = requested;
            }
            int monsterAccuracy = Monsters.BaseAccuracy(level) ?? 0;
            int monsterDamage = Monsters.BaseDamage(level) ?? 0;
            int evasion = PlayerEvasion(player, player.Clock.Elapsed.TotalSeconds);
            int hitChance = DamageModel.AccuracyHitChance(evasion, monsterAccuracy);
            int reduction = DamageModel.ArmourReduction(player.Pools.Armour, monsterDamage);
            await player.Commands!.Reply($"Armour {player.Pools.Armour} ({player.Pools.ArmourPct}% increased): " +
                $"{reduction}% physical reduction against a level {level} monster hit ({monsterDamage}), " +
                $"evasion {evasion} ({player.Pools.EvasionPct}% increased): {100 - hitChance}% chance to evade " +
                $"(monster accuracy {monsterAccuracy}, {hitChance}% chance to be hit).");
        })
        .Add("auras", "/auras [off <name|effectId|all>] (list reservations or disable one, including stances)", async args =>
        {
            if (args.Length > 0)
            {
                if (args.Length < 2 || !args[0].Equals("off", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("/auras off <name|effectId|all>");
                await DisableReservations(player, string.Join(' ', args.Skip(1)));
            }
            List<AuraCatalog.Aura> auras = [.. player.Auras.Values];
            int reserved = ReservedMana(player);
            int maxMana = (int)player.Pools.Mana;
            int efficiency = player.Pools.ManaReservationEfficiencyPct;
            int auraEffect = player.Pools.AuraEffectPct;
            string list = auras.Count == 0 ? "none" : string.Join(", ", auras.Select(a =>
                $"{a.Name} ({a.ManaFlat} flat, {a.ManaPercent / 100.0:0.##}% {(a.ReservesLife ? "life" : "mana")})"));
            await player.Commands!.Reply($"Auras: {list}. Mana {player.PlayerMana}/{maxMana}, {reserved} reserved " +
                $"({maxMana - reserved} unreserved); Life {player.PlayerLife}/{player.PlayerMaxLife}, {player.PlayerReservedLife} reserved. " +
                $"Reservation efficiency mana {efficiency}%, life {player.Pools.LifeReservationEfficiencyPct}%, aura effect {auraEffect}%.");
        })
        .Add("spellhit", "/spellhit <fire|cold|lightning|chaos|physical> <damage> (dev: spell hit through resistance and Spell Suppression)", async args =>
        {
            if (args.Length < 2) throw new ArgumentException("a damage type and an amount");
            if (ParseDamageType(args[0]) is not { } type) throw new ArgumentException("fire, cold, lightning, chaos or physical");
            if (!int.TryParse(args[1], out int amount) || amount <= 0) throw new ArgumentException("a positive amount");
            if (!TrySyntheticEnemyHit(player, type, amount, false, true, true, out var hit))
            {
                await player.Commands!.Reply(player.PlayerLife == 0 ? "You are already dead." : "Enemy test hit rejected while paused or leaving.");
                return;
            }
            int dealt = hit.Damage; string mitigation = hit.Mitigation; uint life = hit.Life;
            bool suppressed = hit.Suppressed;
            var (chance, effect, lucky, unlucky) = hit.Suppression;
            await SendPlayerEnergyShield(player);
            if (life == 0)
            {
                SetStats(player, player.Id, [(Stat.HideMiniLifeBar, 1)]);
                Note("0x01b3 player hide_mini_life_bar = 1 (dev spell hit)");
            }
            Note($"0x01b1 player dev spell {type} hit for {dealt} ({amount} before {mitigation}, suppression {(suppressed ? "yes" : "no")}), life {life}/{player.PlayerMaxLife}");
            if (hit.Mana != hit.ManaBefore)
                await SendPlayerMana(player, $"0x01b1 player mana {hit.Mana} ({(long)hit.Mana - hit.ManaBefore:+0;-0})");
            if (life == 0)
                await PlayerDied(player, $"0x0055 death screen ({player.Character.League ?? "?"} league, dev spell hit)");
            double effective = DamageModel.EffectiveSuppressionChance(chance, lucky, unlucky);
            await player.Commands!.Reply($"{type} spell hit: {amount} → {dealt} after {mitigation}; " +
                $"suppression {(suppressed ? "rolled" : "not rolled")} ({chance}% chance, effective {effective:0.#}%" +
                $"{(lucky ? ", lucky" : "")}{(unlucky ? ", unlucky" : "")}); life {life}/{player.PlayerMaxLife}.");
        })
        .Add("charges", ChargesUsage, args => ChargesCommand(player, args))
        .Add("crit", "/crit (attack and spell critical strike chance and multiplier)", async _ =>
        {
            var p = player.Pools;
            await player.Commands!.Reply(
                $"Attack crit {p.AttackCriticalStrikeChance / 100.0:0.##}% ({p.CriticalStrikeChancePct}% increased), " +
                $"multiplier {AttackCriticalMultiplier(player, CharacterPlayerStats(player))}%. Spell crit reference (5% base) {p.SpellCriticalStrikeChance / 100.0:0.##}% " +
                $"({p.SpellCriticalStrikeChancePct}% increased), multiplier {p.SpellCriticalStrikeMultiplier}%.");
        })
        .Add("block", "/block (attack and spell block chance and caps)", async _ =>
        {
            var p = player.Pools;
            await player.Commands!.Reply($"Block {p.BlockChance}% (max {p.BlockChanceMax}%, uncapped {p.BlockChanceUncapped}%, effective {DamageModel.EffectiveBlockChance(p.BlockChance, p.AttackBlockLucky, p.AttackBlockUnlucky):0.#}%), " +
                $"spell block {p.SpellBlockChance}% (max {p.SpellBlockChanceMax}%, uncapped {p.SpellBlockChanceUncapped}%, effective {DamageModel.EffectiveBlockChance(p.SpellBlockChance, p.SpellBlockLucky, p.SpellBlockUnlucky):0.#}%)" +
                $"{(p.AttackBlockLucky ? "; attack lucky" : "")}{(p.SpellBlockLucky ? "; spell lucky" : "")}{(p.AttackBlockUnlucky ? "; attack unlucky" : "")}{(p.SpellBlockUnlucky ? "; spell unlucky" : "")}.");
        })
        .Add("block_luck", "/block_luck <attack|spell|both|off> (dev: force lucky block)", async args =>
        {
            string mode = args.Length == 0 ? "" : args[0].ToLowerInvariant();
            bool attack = mode is "attack" or "both", spell = mode is "spell" or "both";
            if (mode is not ("attack" or "spell" or "both" or "off" or "")) throw new ArgumentException("attack, spell, both or off");
            player.ForceAttackBlockLucky = attack;
            player.ForceSpellBlockLucky = spell;
            player.Pools = PoolsFor(player, player.Pools.Level);
            await SendCharacterStats(player, PlayerStatChange.Sources);
            await player.Commands!.Reply($"Lucky block: attack {(attack ? "on" : "off")}, spell {(spell ? "on" : "off")}.");
        })
        .Add("blockhit", "/blockhit <attack|spell> <fire|cold|lightning|chaos|physical> <damage> (dev: hit through block and resistance)", async args =>
        {
            if (args.Length < 3) throw new ArgumentException("attack|spell, a damage type and an amount");
            bool spell = args[0].Equals("spell", StringComparison.OrdinalIgnoreCase);
            if (!spell && !args[0].Equals("attack", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("attack or spell");
            if (ParseDamageType(args[1]) is not { } type) throw new ArgumentException("fire, cold, lightning, chaos or physical");
            if (!int.TryParse(args[2], out int amount) || amount <= 0) throw new ArgumentException("a positive amount");
            if (!TrySyntheticEnemyHit(player, type, amount, true, spell, false, out var hit))
            {
                await player.Commands!.Reply(player.PlayerLife == 0 ? "You are already dead." : "Enemy test hit rejected while paused or leaving.");
                return;
            }
            int dealt = hit.Damage; string mitigation = hit.Mitigation; uint life = hit.Life;
            uint mana = hit.Mana, manaBefore = hit.ManaBefore;
            bool blocked = hit.Blocked; int chance = hit.BlockChance;
            await SendPlayerEnergyShield(player);
            if (life == 0)
            {
                SetStats(player, player.Id, [(Stat.HideMiniLifeBar, 1)]);
                Note("0x01b3 player hide_mini_life_bar = 1 (dev block hit)");
            }
            Note($"0x01b1 player dev {(spell ? "spell" : "attack")} hit for {dealt} ({(blocked ? "blocked" : mitigation)}), life {life}/{player.PlayerMaxLife}");
            if (mana != manaBefore)
                await SendPlayerMana(player,
                    $"0x01b1 player mana {mana} ({(long)mana - manaBefore:+0;-0}{(blocked ? ", blocked" : "")})");
            if (life == 0)
                await PlayerDied(player, $"0x0055 death screen ({player.Character.League ?? "?"} league, dev block hit)");
            await player.Commands!.Reply($"{(spell ? "Spell" : "Attack")} hit: {amount} → {dealt}; {(blocked ? "blocked" : "not blocked")} ({chance}% chance); life {life}/{player.PlayerMaxLife}.");
        })
        .Add("god", "/god [on | off | immune] (toggles, or on/off: 1000% increased attack and cast speed, 100% of life and 10 000 mana regenerated per second, 200% increased area of effect, +10 000 life, +1000 attributes; immune: also no damage taken; kept across area changes)",
            args => GodCommand(player, args))
        .Add("set_aoe", "/set_aoe <percent> | off (adds to the character's increased area of effect, base_skill_area_of_effect_+%, while in this area)", async args =>
        {
            if (args.Length == 0) throw new ArgumentException("percent or off");
            int? value = args[0].Equals("off", StringComparison.OrdinalIgnoreCase) ? null : int.Parse(args[0]);
            SetBuffStat(player, AoeBuff, Stat.BaseSkillAreaOfEffectPlusPct, value);
            await player.Commands!.Reply(value is int v ? $"Increased area of effect {v:+0;-0}% in this area." : "Increased area of effect: the character's own.");
        })
        .Add("heal", "/heal (full life; also revives: 0x1b1 from 0 runs the client's revive)", async _ =>
        {
            (player.PlayerLife, player.PlayerMana) = (player.PlayerUnreservedLife, (uint)UnreservedMana(player));
            player.EnergyShield.SetMaximum(player.Pools.EnergyShield, true);
            Note($"0x01b1 player healed to {player.PlayerMaxLife}");
            await SendPlayerMana(player, $"0x01b1 player mana {player.PlayerMana}");
            SetStats(player, player.Id, [(Stat.HideMiniLifeBar, 0)]);
            Note("0x01b3 player hide_mini_life_bar = 0");
        })
        .Add("death_screen", "/death_screen [hex flags] (show the death screen; default: this league's; 2 hides it)", async args =>
        {
            byte flags = args.Length > 0 ? Convert.ToByte(args[0].Replace("0x", ""), 16) : DeathScreenFlags(player);
            await Send(player, InstanceProtocol.DeathScreen(flags), $"0x0055 death screen flags 0x{flags:x2}");
        })
        .Add("transition", "/transition [pos [here | x y | +dx +dy] | rot <degrees> | z <z | none> | model <path | none> | index <n>] " +
            "(dev: show or change the first area transition; it is re-created)", async args =>
        {
            var (px, py) = player.Walk.Position(player.Clock.Elapsed.TotalSeconds);
            if (player.Layout.AreaTransitions.Length == 0) { await player.Commands!.Reply("This area has no area transition."); return; }
            var (id, at) = player.Layout.AreaTransitions[0];
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string? what = args.Length > 0 ? args[0] : null;
            string[] rest = args.Skip(1).ToArray();
            AreaTransitionObject? changed = what switch
            {
                "pos" when rest.Length > 0 => rest[0] == "here" ? at with { GridX = px, GridY = py }
                    : rest[0].StartsWith('+') || rest[0].StartsWith('-')
                        ? at with { GridX = at.GridX + int.Parse(rest[0]), GridY = at.GridY + int.Parse(rest[1]) }
                        : at with { GridX = int.Parse(rest[0]), GridY = int.Parse(rest[1]) },
                "rot" => at with { Rotation = float.Parse(rest[0], inv) * MathF.PI / 180f },
                "z" => at with { Height = rest[0] == "none" ? null : float.Parse(rest[0], inv) },
                "model" => at with { Model = rest[0] == "none" ? null : string.Join(' ', rest) },
                "index" => at with { Index = int.Parse(rest[0]) },
                _ => null,
            };
            if (changed is not null)
            {
                player.ReplaceAreaTransition(id, changed);
                at = changed;
                await Send(player, InstanceProtocol.RemoveObject(id), $"0x02bd remove area transition 0x{id:x}");
                await Send(player, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(at.Type), at.Blob()),
                    $"0x02bb create area transition {at}");
                await Send(player, InstanceProtocol.EnterWakeZone(id), $"0x01a9 area transition 0x{id:x} enters wake zone");
            }
            await player.Commands!.Reply($"Area transition 0x{id:x} at ({at.GridX}, {at.GridY}), {at.Rotation * 180 / MathF.PI:0.#} degrees, " +
                $"height {at.Height?.ToString(inv) ?? "terrain"}, model {at.Model ?? "none"}; you at ({px}, {py}).");
        })
        .Add("quest_flag", "/quest_flag [<QuestFlags Id> [off]] (dev: list, set or clear quest flags, e.g. /quest_flag a1q1-KilledHillock)", async args =>
        {
            if (args.Length < 1) { await player.Commands!.Reply(string.Join(", ", player.Character.QuestFlags.DefaultIfEmpty("no quest flags"))); return; }
            bool off = args.Length > 1 && args[1].Equals("off", StringComparison.OrdinalIgnoreCase);
            await SetQuestFlags(player, [args[0]], on: !off);
            await player.Commands!.Reply($"{args[0]} {(off ? "cleared" : "set")}");
        }, "quest_flag")
        .Add("trials", "/trials [on|off] [1|2|3|4] (dev: set or clear the Trials of Ascendancy's quest flags and league quest flags, saved, of one Labyrinth tier (1 Normal's six, 2 Cruel's three, 3 Merciless's three, 4 Eternal's six) or of all, then open the Trial Plaque window with the trials done)", async args =>
        {
            bool change = args.Length >= 1 && (args[0].Equals("on", StringComparison.OrdinalIgnoreCase) || args[0].Equals("off", StringComparison.OrdinalIgnoreCase));
            int tier = args.Length > (change ? 1 : 0) && int.TryParse(args[change ? 1 : 0], out int n) && n is >= 1 and <= 4 ? n : 0;
            List<LabyrinthTrials.Trial> trials = [.. LabyrinthTrials.Trials.Where(t => tier == 0 || LabyrinthTrials.Tier(t) == tier)];
            string which = tier == 0 ? "every tier" : $"tier {tier}";
            if (change)
            {
                bool on = args[0].Equals("on", StringComparison.OrdinalIgnoreCase);
                await SetQuestFlags(player, [.. trials.Select(t => t.Flag)], on);
                await SetLeagueQuestFlags(player, [.. trials.Select(t => t.LeagueFlag)], on);
                // Without the Normal trials the Sarn Encampment's statue closes again (its plaque opens it).
                if (!on && tier is 0 or 1)
                    await SetQuestFlags(player, [LabyrinthTrials.SarnStatueOpened, LabyrinthTrials.LabyrinthAccess], false);
                await player.Commands!.Reply($"{trials.Count} trial(s) of {which} {(on ? "done" : "cleared")}: their quest flags and league quest flags.");
            }
            int own = trials.Count(t => player.Character.HasQuestFlag(t.Flag)), league = trials.Count(t => player.Character.LeagueQuestFlags.Contains(t.LeagueFlag));
            await player.Commands!.Reply($"Trials done ({which}): {own} of {trials.Count} by the character's quest flag, {league} by the league's.");
            await Send(player, InstanceWaypoints.OpenUi(InstanceWaypoints.TrialPlaqueUi), "0x007b open the Trial Plaque window (/trials)");
        })
        .Add("lab_layout", "/lab_layout [1|2|3|4] [yyyy-mm-dd] (dev: today's Labyrinth layout of a tier, or another day's, 1 Normal by default: each room's area, section, connections and secrets; /goto a listed area to play today's room with its node)", async args =>
        {
            int tier = args.Length > 0 && int.TryParse(args[0], out int n) && n is >= 1 and <= 4 ? n : 1;
            string? date = args.FirstOrDefault(a => a.Contains('-'));
            uint seed = date is null ? LabyrinthDay.Seed : LabyrinthDay.SeedOf(DateOnly.ParseExact(date, "yyyy-MM-dd"));
            await player.Commands!.Reply($"Labyrinth day {seed}, tier {tier}:");
            foreach (var line in LabyrinthDay.Describe(seed, tier)) await player.Commands!.Reply(line);
        })
        .Add("trial_spike_hit", "/trial_spike_hit [<from ms> <to ms>] (dev: how long after firing the Trials of Ascendancy spike traps hit, up to 3000 ms, e.g. /trial_spike_hit 1000 1250)", async args =>
        {
            if (args.Length >= 2 && int.TryParse(args[0], out int from) && int.TryParse(args[1], out int to) && from >= 0 && to > from && to <= 3000)
                LabyrinthTrials.SpikeHitWindow = (from / 1000.0, to / 1000.0);
            var (f, t) = LabyrinthTrials.SpikeHitWindow;
            await player.Commands!.Reply($"Trial spike traps hit from {f * 1000:0} to {t * 1000:0} ms after firing.");
        })
        .Add("trial_spike_step", "/trial_spike_step [<ms>] (dev: the time between two spike traps of a Trials of Ascendancy ring's wave, from its next wave, e.g. /trial_spike_step 250)", async args =>
        {
            if (args.Length >= 1 && int.TryParse(args[0], out int ms) && ms is >= 50 and <= 3000)
                LabyrinthTrials.SpikeStepSeconds = ms / 1000.0;
            await player.Commands!.Reply($"Trial spike rings step {LabyrinthTrials.SpikeStepSeconds * 1000:0} ms a trap.");
        })
        .Add("trial_spike_anim", "/trial_spike_anim [<ms>] (dev: how long a Trials of Ascendancy spike trap's Emerge animation (0x1d1) is stretched over, e.g. /trial_spike_anim 2000)", async args =>
        {
            if (args.Length >= 1 && int.TryParse(args[0], out int ms) && ms is >= 100 and <= 6000)
                LabyrinthTrials.SpikeAnimationSeconds = ms / 1000.0;
            await player.Commands!.Reply($"Trial spike traps rise over a {LabyrinthTrials.SpikeAnimationSeconds * 1000:0} ms Emerge animation.");
        })
        .Add("trial_roomba_damage", "/trial_roomba_damage [<life %> <energy shield %> [<radius>]] (dev: what a Trials of Ascendancy roomba deals a second of maximum life and energy shield while the player is in it, and how near its centre that is in cells, e.g. /trial_roomba_damage 40 25 10)", async args =>
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (args.Length >= 2 && double.TryParse(args[0], System.Globalization.NumberStyles.Float, inv, out double life)
                && double.TryParse(args[1], System.Globalization.NumberStyles.Float, inv, out double shield) && life is >= 0 and <= 1000 && shield is >= 0 and <= 1000)
                LabyrinthTrials.RoombaDamagePerSecond = (life / 100, shield / 100);
            if (args.Length >= 3 && double.TryParse(args[2], System.Globalization.NumberStyles.Float, inv, out double radius) && radius is > 0 and <= 50)
                LabyrinthTrials.RoombaRadius = radius;
            var (l, e) = LabyrinthTrials.RoombaDamagePerSecond;
            await player.Commands!.Reply($"Trial roombas deal {l * 100:0.#}% of maximum life and {e * 100:0.#}% of maximum energy shield a second within {LabyrinthTrials.RoombaRadius:0.#} cells.");
        })
        .Add("trial_saw_damage", "/trial_saw_damage [<life %> <energy shield %> [<radius>]] (dev: what a Trials of Ascendancy sawblade deals a second of maximum life and energy shield while the player is in it, and how near its centre that is in cells, e.g. /trial_saw_damage 40 50 5)", async args =>
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (args.Length >= 2 && double.TryParse(args[0], System.Globalization.NumberStyles.Float, inv, out double life)
                && double.TryParse(args[1], System.Globalization.NumberStyles.Float, inv, out double shield) && life is >= 0 and <= 1000 && shield is >= 0 and <= 1000)
                LabyrinthTrials.SawDamagePerSecond = (life / 100, shield / 100);
            if (args.Length >= 3 && double.TryParse(args[2], System.Globalization.NumberStyles.Float, inv, out double radius) && radius is > 0 and <= 50)
                LabyrinthTrials.SawRadius = radius;
            var (l, e) = LabyrinthTrials.SawDamagePerSecond;
            await player.Commands!.Reply($"Trial sawblades deal {l * 100:0.#}% of maximum life and {e * 100:0.#}% of maximum energy shield a second within {LabyrinthTrials.SawRadius:0.#} cells.");
        })
        .Add("trial_dart_poison", "/trial_dart_poison [<percent>] (dev: how much of a Trials of Ascendancy dart's hit, its life share, a poison stack deals a second, from the next hit, e.g. /trial_dart_poison 10)", async args =>
        {
            if (args.Length >= 1 && double.TryParse(args[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double pct) && pct is > 0 and <= 100)
                LabyrinthTrials.ArrowPoisonPerSecond = pct / 100;
            await player.Commands!.Reply($"Trial dart poison: {LabyrinthTrials.ArrowPoisonPerSecond * 100:0.#}% of the hit's life share a second, per stack.");
        })
        .Add("trial_lava", "/trial_lava [<life %> <energy shield %> [<peak s> [<cold s>]]] (dev: what a Trials of Ascendancy hot plate deals a second of maximum life and energy shield, before fire resistance, while its lava lies on it, and how long it stays at its peak and rests cold between its 6 s heating and 7 s cooling, from its next cycle, e.g. /trial_lava 40 50 5 6)", async args =>
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (args.Length >= 2 && double.TryParse(args[0], System.Globalization.NumberStyles.Float, inv, out double life)
                && double.TryParse(args[1], System.Globalization.NumberStyles.Float, inv, out double shield) && life is >= 0 and <= 1000 && shield is >= 0 and <= 1000)
                LabyrinthTrials.LavaDamagePerSecond = (life / 100, shield / 100);
            if (args.Length >= 3 && double.TryParse(args[2], System.Globalization.NumberStyles.Float, inv, out double burn) && burn is > 0 and <= 60)
                LabyrinthTrials.LavaBurnSeconds = burn;
            if (args.Length >= 4 && double.TryParse(args[3], System.Globalization.NumberStyles.Float, inv, out double rest) && rest is >= 0 and <= 60)
                LabyrinthTrials.LavaRestSeconds = rest;
            var (l, e) = LabyrinthTrials.LavaDamagePerSecond;
            await player.Commands!.Reply($"Trial hot plates deal {l * 100:0.#}% of maximum life and {e * 100:0.#}% of maximum energy shield a second (before fire resistance), " +
                $"at the peak {LabyrinthTrials.LavaBurnSeconds:0.#} s, cold {LabyrinthTrials.LavaRestSeconds:0.#} s.");
        })
        .Add("atlas", "/atlas [layout] (dev: show or choose the fallback layout generic maps open)", async args =>
        {
            var atlas = MapDevices.Atlas;
            if (atlas is null || atlas.Layouts.Count == 0) { await player.Commands!.Reply("No Atlas layouts: extract AtlasNode.json and WorldAreas.json."); return; }
            if (args.Length == 0)
            {
                var current = atlas.Selected(CharacterName(player));
                await player.Commands!.Reply($"Atlas layout: {current?.Name} ({current?.Id}); {atlas.Layouts.Count} layouts available.");
                return;
            }
            string query = string.Join(' ', args);
            if (atlas.Find(query) is not { } layout) { await player.Commands!.Reply($"No Atlas layout \"{query}\"."); return; }
            if (CharacterName(player) is { } character)
            {
                atlas.Select(character, layout);
                CharacterSaves.SetAtlasLayout(character, layout.Id);
            }
            await player.Commands!.Reply($"Atlas layout set to {layout.Name} ({layout.Id}, tier {layout.Tier}).");
        })
        .Add("atlas_tree", "/atlas_tree (dev: open the Atlas passive tree; S→C 0x7b kind 0x11)", async _ =>
        {
            await Send(player, InstanceWaypoints.OpenUi(InstanceWaypoints.AtlasUi),
                "0x007b open the Atlas passive tree");
        })
        .Add("atlas_points", "/atlas_points [<n>] (dev: show or set the Atlas passive point budget, saved)", async args =>
        {
            if (args.Length == 0)
            {
                await player.Commands!.Reply($"Atlas passive points: {player.Character.AtlasPassivePoints}; " +
                    $"preset allocations: {string.Join(", ", player.Character.AtlasPassives.Select((p, i) => $"{i}={p.Count}"))}.");
                return;
            }
            if (!int.TryParse(args[0], out int points) || points is < 0 or > ushort.MaxValue)
                throw new ArgumentException("a number from 0 to 65535");
            player.Character.AtlasPassivePoints = (ushort)points;
            if (CharacterName(player) is { } character)
                CharacterSaves.SetAtlasPassives(character, player.Character.AtlasPassives, player.Character.AtlasPassivePoints);
            await SendAtlasSlots(player, "/atlas_points");
            await player.Commands!.Reply($"Atlas passive points: {points}.");
        })
        .Add("goto", "/goto <area id or name> [seed] (dev: change area as if through an area transition, e.g. /goto 1_1_3; with a seed, a new instance at that seed, e.g. /goto 1_1_5 1384217569)", async args =>
        {
            // A last number is the seed: a new instance at it, as the capture tour's stops (towns keep seed 1).
            uint seed = args.Length > 1 && uint.TryParse(args[^1], out var s) && s != 0 ? s : 0;
            string target = string.Join(' ', seed != 0 ? args[..^1] : args);
            if (AreaTransitionCatalog.HashOf(target) is not { } hash) { await player.Commands!.Reply($"Unknown area \"{target}\" (needs data/area-transitions.json)."); return; }
            await ChangeArea(player, new PendingTransfer(hash, 0, seed != 0) { Seed = seed },
                seed != 0 ? $"/goto {target} seed {seed}" : $"/goto {target}");
        }, "area")
        .Add("tour", "/tour <list file> | stop | status (dev, live.ps1 -Walkmaps: visit every \"<area id> <seed>\" of the file in a new instance and record it; levelgen tourlist writes one)", async args =>
        {
            string character = CharacterName(player);
            string what = args.Length > 0 ? args[0] : "status";
            if (what.Equals("stop", StringComparison.OrdinalIgnoreCase)) { await player.Commands!.Reply(CaptureTour.Stop(character)); return; }
            if (what.Equals("status", StringComparison.OrdinalIgnoreCase)) { await player.Commands!.Reply(CaptureTour.Status(character)); return; }
            string path = string.Join(' ', args);
            if (!AreaLayouts.Recording) { await player.Commands!.Reply("A tour needs the walk-map recorder: start the servers with live.ps1 -Walkmaps."); return; }
            if (!File.Exists(path)) { await player.Commands!.Reply($"No file {path} (relative to the Gateway's directory, the repository root)."); return; }
            await player.Commands!.Reply(CaptureTour.Start(character, path));
            // Not awaited: a step waits for the client to arrive or to fail.
            _ = ContinueTour(player, starting: true);
        })
        .Add("league_flag", "/league_flag [<id> [off]] (dev: list, set or clear league quest flags by the client's id, saved for the league, e.g. /league_flag 350)", async args =>
        {
            if (args.Length < 1 || !int.TryParse(args[0], out int flag))
            {
                await Send(player, InstanceChat.ChatLine($"League quest flags: {string.Join(", ", player.Character.LeagueQuestFlags.Order())}"), "0x0017 chat line: league quest flags");
                return;
            }
            bool off = args.Length > 1 && args[1].Equals("off", StringComparison.OrdinalIgnoreCase);
            await SetLeagueQuestFlags(player, [flag], on: !off);
            await Send(player, InstanceChat.ChatLine($"League quest flag {flag} {(off ? "cleared" : "set")}."), $"0x0017 chat line: league quest flag {flag}");
        })
        .Add("hideout_keys", "/hideout_keys <a> <b> (dev: the Player component's two Hideouts.dat keys, HASH16 hex or a hideout area id)", async args =>
        {
            ushort Key(string s) => HideoutCatalog.Hideouts.FirstOrDefault(h => h.AreaId.Equals(s, StringComparison.OrdinalIgnoreCase)) is { } h ? h.Hash16
                : ushort.TryParse(s.Replace("0x", ""), System.Globalization.NumberStyles.HexNumber, null, out var k) ? k : (ushort)0;
            ushort a = args.Length > 0 ? Key(args[0]) : (ushort)0, b = args.Length > 1 ? Key(args[1]) : (ushort)0;
            await Send(player, InstanceEntities.PlayerHideoutsUpdate(player.Id, a, b), $"0x02bc player hideout keys 0x{a:x4} 0x{b:x4}");
            await Send(player, InstanceChat.ChatLine($"Hideout keys 0x{a:x4} 0x{b:x4}."), "0x0017 chat line: hideout keys");
        })
        .Add("waypoints", "/waypoints [all | reset] (list the activated waypoints; dev: activate all and visit every pinned area, or keep only the towns')", async args =>
        {
            var entries = WaypointCatalog.Areas.Entries;
            if (entries.Count == 0) { await player.Commands!.Reply("No waypoints (needs data/tables/WorldAreas.json)."); return; }
            string what = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            if (what is "all" or "reset")
            {
                string[] all;
                string[] visited;
                lock (player.Character.Waypoints)
                {
                    player.Character.Waypoints.Clear();
                    player.Character.Waypoints.UnionWith(entries.Where(e => what == "all" || e.IsTown).Select(e => e.Id));
                    all = [.. player.Character.Waypoints];
                    // "all" also visits every area with a World panel pin, so every pin and line shows.
                    if (what == "all") player.Character.Visited.UnionWith(WaypointCatalog.Visits.PinnedAreas);
                    else { player.Character.Visited.Clear(); player.Character.Visited.Add(player.Layout.Id); }
                    visited = [.. player.Character.Visited];
                }
                CharacterSaves.SetWaypoints(CharacterName(player), all);
                CharacterSaves.SetVisited(CharacterName(player), visited);
                await SendWaypointList(player, $"/waypoints {what}");
            }
            string list;
            lock (player.Character.Waypoints) list = string.Join(", ", entries.Where(e => player.Character.Waypoints.Contains(e.Id)).Select(e => e.Name));
            await player.Commands!.Reply(list.Length == 0 ? "No activated waypoints." : $"Activated waypoints: {list}.");
        })
        .Add("npc", "/npc <NPCs row, Id or name> [rotation°] (spawns an NPC next to you)", async args =>
        {
            if (args.Length == 0) throw new ArgumentException("which NPC");
            if (Npcs.Error is { } error) { await player.Commands!.Reply(error); return; }
            float? deg = args.Length > 1 && float.TryParse(args[^1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float d) ? d : null;
            string query = string.Join(' ', deg is null ? args : args[..^1]);
            if (Npcs.Find(query) is not { } npc) { await player.Commands!.Reply($"No NPC {query}."); return; }
            if (!npc.Supported) { await player.Commands!.Reply($"{npc} has components the server cannot write: {string.Join(", ", npc.Components ?? [])}."); return; }
            var (x, y) = NextToPlayer(player);
            await SpawnNpc(player, npc, x, y, (deg ?? 0) * MathF.PI / 180);
        }, "npc")
        // Turns an NPC: it is created again with the new facing (0x2bd, then 0x2bb).
        .Add("npc_place", "/npc_place <name> <x> <y> <degrees> (dev: move and turn an NPC here)", async args =>
        {
            if (args.Length < 4 || !int.TryParse(args[^3], out int x) || !int.TryParse(args[^2], out int y)) throw new ArgumentException("/npc_place <name> <x> <y> <degrees>");
            float deg = float.Parse(args[^1], System.Globalization.CultureInfo.InvariantCulture);
            string name = string.Join(' ', args[..^3]);
            KeyValuePair<uint, SpawnedNpc> found;
            lock (player.Npcs) found = player.Npcs.FirstOrDefault(n => n.Value.Npc.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (found.Value is not { } npc) { await player.Commands!.Reply($"No NPC {name} here."); return; }
            lock (player.Npcs) player.Npcs.Remove(found.Key);
            player.RemoveBlocker(found.Key);
            await Send(player, InstanceProtocol.RemoveObject(found.Key), $"0x02bd remove npc 0x{found.Key:x} {npc.Npc.Name}");
            await SpawnNpc(player, npc.Npc, x, y, deg * MathF.PI / 180);
            await player.Commands!.Reply($"{npc.Npc.Name} at ({x}, {y}), {deg}°");
        })
        .Add("npc_face", "/npc_face <name> <degrees> (turn an NPC here)", async args =>
        {
            if (args.Length < 2) throw new ArgumentException("name and degrees");
            float deg = float.Parse(args[^1], System.Globalization.CultureInfo.InvariantCulture);
            string name = string.Join(' ', args[..^1]);
            KeyValuePair<uint, SpawnedNpc> found;
            lock (player.Npcs) found = player.Npcs.FirstOrDefault(n => n.Value.Npc.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (found.Value is not { } npc) { await player.Commands!.Reply($"No NPC {name} here."); return; }
            lock (player.Npcs) player.Npcs.Remove(found.Key);
            player.RemoveBlocker(found.Key);
            await Send(player, InstanceProtocol.RemoveObject(found.Key), $"0x02bd remove npc 0x{found.Key:x} {npc.Npc.Name}");
            float rad = deg * MathF.PI / 180;
            await SpawnNpc(player, npc.Npc, npc.X, npc.Y, rad);
            await player.Commands!.Reply($"{npc.Npc.Name} at ({npc.X}, {npc.Y}) faces {deg}° ({rad:0.###} rad).");
        })
        // Probe: S→C 0x7b (open a UI) with any kind and its four u32s (16-waypoints.md §3; kind 0 = World panel).
        .Add("ui", "/ui <kind hex> [a] [b] [c] [d] (S→C 0x7b open a UI)", async args =>
        {
            if (args.Length == 0) throw new ArgumentException("which kind");
            byte kind = Convert.ToByte(args[0].Replace("0x", "", StringComparison.OrdinalIgnoreCase), 16);
            uint U(int i) => args.Length > i ? Convert.ToUInt32(args[i], args[i].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 16 : 10) : 0;
            await Send(player, InstanceWaypoints.OpenUi(kind, U(1), U(2), U(3), U(4)), $"0x007b open UI kind 0x{kind:x2} ({U(1)}, {U(2)}, {U(3)}, {U(4)})");
        })
        .Add("npcs", "/npcs (list NPCs here)", async _ =>
        {
            string list;
            lock (player.Npcs) list = string.Join(", ", player.Npcs.Select(n => $"0x{n.Key:x} {n.Value.Npc.Name} ({n.Value.X}, {n.Value.Y})"));
            await player.Commands!.Reply(list.Length == 0 ? "No NPCs." : list);
        })
        .Add("pos", "/pos (your cell)", async _ =>
        {
            var (x, y) = player.Walk.Position(player.Clock.Elapsed.TotalSeconds);
            Console.WriteLine($"[gateway] {player.Connection.Remote} /pos ({x}, {y}) in {player.Layout.Id}");
            await player.Commands!.Reply($"({x}, {y}) in {player.Layout.Id}");
        })
        .Add("tp", "/tp <x> <y> | <+dx> <+dy> | <id> | <name> [n] | list [filter] (dev: teleport to a cell, a monster, NPC, transition, waypoint or captured object, e.g. /tp RhoaChest 2)",
            args => DevTeleport(player, args))
        .Add("mobs", "/mobs (list spawned monsters)", async _ =>
        {
            var buried = MonsterEmerge.Buried(player);
            var waiting = new HashSet<SpawnedMonster>(MonsterEmerge.Unrisen(player), ReferenceEqualityComparer.Instance);
            double now = player.Clock.Elapsed.TotalSeconds;
            List<string> listed = [.. player.Area.Monsters.Concat(buried).Select(m =>
                $"0x{m.Key:x} {m.Value.Label} {m.Value.Walk.Position(now)}" +
                (waiting.Contains(m.Value) ? buried.ContainsKey(m.Key) ? " (buried)" : " (lurking)" : m.Value.Aggro ? " (chasing)" : ""))];
            await player.Commands!.Reply(listed.Count == 0 ? "No monsters." : string.Join(", ", listed));
        })
        .Add("despawn", "/despawn [all | <id>] (default: the last one spawned)", async args =>
        {
            List<uint> ids = args.Length == 0 ? [.. player.Area.Monsters.Keys.Concat(MonsterEmerge.Buried(player).Keys).OrderBy(k => k).TakeLast(1)]
                : args[0].Equals("all", StringComparison.OrdinalIgnoreCase) ? [.. player.Area.Monsters.Keys, .. MonsterEmerge.Buried(player).Keys]
                : [Convert.ToUInt32(args[0], args[0].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 16 : 10)];
            foreach (uint id in ids)
            {
                bool removed = player.Area.Monsters.Remove(id) || MonsterEmerge.Buried(player).Remove(id);
                if (!removed) { await player.Commands!.Reply($"No monster 0x{id:x}."); continue; }
                await SendWorldRemoval(player, id, $"0x02bd remove monster 0x{id:x}");
            }
        })
        // Movement probes: walk (0x1ba, Positioned move with a speed) or teleport (0x1b7) a monster to a few cells from the player.
        .Add("mob_move", "/mob_move [id] [speed] (0x1ba toward you; default the last monster, speed 20)", async args =>
        {
            if (MonsterArg(player, args, 0) is not { } id) { await player.Commands!.Reply("No such monster."); return; }
            string? sp = args.FirstOrDefault(a => !a.StartsWith("0x", StringComparison.OrdinalIgnoreCase));
            float speed = sp is null ? 20f : float.Parse(sp, System.Globalization.CultureInfo.InvariantCulture);
            var (x, y) = NextToPlayer(player);
            await Send(player, InstanceProtocol.MoveTo(id, x, y, speed), $"0x01ba move monster 0x{id:x} to ({x}, {y}) speed {speed}");
            player.Area.Monsters[id].Walk = player.NewMonsterWalk(id, x, y, player.Area.Monsters[id].Walk.Speed,
                LivingCollision.Size(player.Area.Monsters[id]), player.Area.Monsters[id].Rarity == MonsterRarity.Unique);
        })
        // Action probe: start the monster's Move action (0x1ab) toward the player, like the echo of a player's click. The skill
        // id is the player's Move (0x2909) unless given (hex); sequence 0xffff (the monster is not client-predicted).
        .Add("mob_walk", "/mob_walk [id] [skill hex] (0x1ab Move toward you)", async args =>
        {
            if (MonsterArg(player, args, 0) is not { } id) { await player.Commands!.Reply("No such monster."); return; }
            string? sk = args.FirstOrDefault(a => !a.StartsWith("0x", StringComparison.OrdinalIgnoreCase));
            ushort skill = sk is null ? (ushort)0x2909 : Convert.ToUInt16(sk, 16);
            var (x, y) = NextToPlayer(player);
            (int X, int Y) from = player.Area.Monsters[id].Walk.Order(x, y, player.Clock.Elapsed.TotalSeconds);
            await Send(player, InstanceProtocol.ActionStarted(id, from.X, from.Y, x, y, skill, 0xffff, 0x0408),
                $"0x01ab monster 0x{id:x} action skill 0x{skill:x4} from {from} to ({x}, {y})");
        })
        // Animation probe: play animation index `id` on the nearest living monster (0x1b2 kind 0). The Animated handler
        // bounds-checks the index against the actor's animation count, so an out-of-range value is ignored.
        .Add("anim", "/anim <index> [speed] (0x1b2 kind 0: play an animation on the nearest monster)", async args =>
        {
            if (args.Length < 1 || !uint.TryParse(args[0], out uint index)) throw new ArgumentException("an animation index");
            float speed = args.Length > 1 ? float.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 1f;
            var here = player.Walk.Position(player.Clock.Elapsed.TotalSeconds);
            uint? pick = player.Area.Monsters.Where(kv => kv.Value.Life > 0)
                .OrderBy(kv => Dist(here, kv.Value.Walk.Position(player.Clock.Elapsed.TotalSeconds)))
                .Select(kv => (uint?)kv.Key).FirstOrDefault();
            if (pick is not { } id) { await player.Commands!.Reply("No living monster nearby."); return; }
            await Send(player, InstanceProtocol.AnimationUpdate(id, 0, index, speed),
                $"0x01b2 monster 0x{id:x} play animation {index} at speed {speed}");
            await player.Commands!.Reply($"0x1b2 anim {index} (speed {speed}) -> monster 0x{id:x}");
        })
        .Add("mob_mods", "/mob_mods [id] [mod …|pool] (a monster's mods; with mods: create it again with those affixes, e.g. /mob_mods Quick of_Flames)",
            args => MonsterModsCommand(player, args))
        .Add("mob_tp", "/mob_tp [id] (0x1b7 next to you; default the last monster)", async args =>
        {
            if (MonsterArg(player, args, 0) is not { } id) { await player.Commands!.Reply("No such monster."); return; }
            var (x, y) = NextToPlayer(player);
            await Send(player, InstanceProtocol.Teleport(id, x, y), $"0x01b7 teleport monster 0x{id:x} to ({x}, {y})");
            player.Area.Monsters[id].Walk = player.NewMonsterWalk(id, x, y, player.Area.Monsters[id].Walk.Speed,
                LivingCollision.Size(player.Area.Monsters[id]), player.Area.Monsters[id].Rarity == MonsterRarity.Unique);
        })
        // One entity, any monster: its own actor skills (by the GrantedEffects Id the client shows), its life or its
        // animation, without its AI choosing them. The verb comes first so it reads as an action; the entity id (hex)
        // may lead or trail the verb, and without one the nearest living monster is used and named in the reply.
        .Add("entity", "/entity <id or nearest> <skills | skill <name or 0x skill id> | kill | life <pct> | anim <index> [speed] | list> (dev: one action of any monster; /entity skills names its own, /entity list shows what is near)",
            async args =>
        {
            double now = player.Clock.Elapsed.TotalSeconds;
            var here = player.Walk.Position(now);
            uint? id = null;
            if (args.Length > 0 && args[0].StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                uint parsed = Convert.ToUInt32(args[0], 16);
                if (player.Area.Monsters.ContainsKey(parsed)) id = parsed;
                if (id is null) { await player.Commands!.Reply($"No monster {args[0]}."); return; }
                args = args[1..];
            }
            string verb = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            string? idText = verb switch
            {
                "skill" or "life" => args.Length > 2 && args[2].StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? args[2] : null,
                "kill" => args.Length > 1 && args[1].StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? args[1] : null,
                "anim" => args.Length > 2 && args[^1].StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? args[^1] : null,
                _ => null,
            };
            if (id is null && idText is not null)
            {
                uint parsed = Convert.ToUInt32(idText, 16);
                if (player.Area.Monsters.ContainsKey(parsed)) id = parsed;
                if (id is null) { await player.Commands!.Reply($"No monster {idText}."); return; }
            }
            if (verb == "list")
            {
                List<string> listed = [.. player.Area.Monsters.Where(kv => kv.Value.Life > 0)
                    .OrderBy(kv => Dist(here, kv.Value.Walk.Position(now))).Take(12)
                    .Select(kv => $"0x{kv.Key:x} {kv.Value.Label} {kv.Value.Walk.Position(now)} "
                        + $"{(ulong)kv.Value.Life * 100 / Math.Max(1u, kv.Value.MaxLife)}%")];
                await player.Commands!.Reply(listed.Count == 0 ? "No living monsters." : string.Join(", ", listed));
                return;
            }
            if (id is null)
                id = player.Area.Monsters.Where(kv => kv.Value.Life > 0)
                    .OrderBy(kv => Dist(here, kv.Value.Walk.Position(now)))
                    .Select(kv => (uint?)kv.Key).FirstOrDefault();
            if (id is null) { await player.Commands!.Reply("No living monster here; give an id (/mobs lists them)."); return; }
            SpawnedMonster m = player.Area.Monsters[id.Value];
            string who = $"0x{id:x} {m.Label}";
            switch (verb)
            {
                case "skills":
                    var actions = MonsterActions.Names(player, id.Value);
                    await player.Commands!.Reply(actions is { Count: > 0 }
                        ? $"{who}: {string.Join(", ", actions)}"
                        : $"{who}: the server knows no skill of its actor.");
                    return;
                case "skill":
                    if (args.Length < 2) { await player.Commands!.Reply("A skill name or a 0x skill id."); return; }
                    if (!args[1].StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    {
                        bool taken = await MonsterActions.Use(player, id.Value, args[1]);
                        await player.Commands!.Reply(taken ? $"{who}: {args[1]}."
                            : $"{who}: it does not carry \"{args[1]}\" (see /entity skills).");
                        return;
                    }
                    ushort skill = Convert.ToUInt16(args[1], 16);
                    var (x, y) = NextToPlayer(player);
                    (int X, int Y) from = m.Walk.Order(x, y, now);
                    await Send(player, InstanceProtocol.ActionStarted(id.Value, from.X, from.Y, x, y, skill, 0xffff, 0x0408),
                        $"0x01ab monster 0x{id:x} action skill 0x{skill:x4} from {from} to ({x}, {y})");
                    await player.Commands!.Reply($"{who}: action 0x{skill:x4} (the clip only; the server owes no hit).");
                    return;
                case "kill":
                    if (!m.Hostile) { await player.Commands!.Reply($"{who}: not a hostile monster."); return; }
                    // The /kill command owns the normal death path (experience, drops and quests; a boss's stage skipped).
                    await player.Commands!.RunAsync($"/kill 0x{id:x}");
                    return;
                case "life":
                    if (args.Length < 2 || !int.TryParse(args[1], out int percent) || percent is < 1 or > 100)
                    { await player.Commands!.Reply("A life percentage, 1 to 100."); return; }
                    uint life = (uint)Math.Max(1, (ulong)m.MaxLife * (ulong)percent / 100);
                    m.Life = life;
                    await Send(player, InstanceProtocol.LifeUpdate(id.Value, life), $"0x01b1 monster 0x{id:x} life {life}");
                    await player.Commands!.Reply($"{who}: life {percent}%.");
                    return;
                case "anim":
                    if (args.Length < 2 || !uint.TryParse(args[1], out uint index))
                    { await player.Commands!.Reply("An animation index and, optionally, its speed."); return; }
                    float speed = args.Length > 2 && args[2] != idText
                        ? float.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 1f;
                    await Send(player, InstanceProtocol.AnimationUpdate(id.Value, 0, index, speed),
                        $"0x01b2 monster 0x{id:x} play animation {index} at speed {speed}");
                    await player.Commands!.Reply($"{who}: animation {index} at speed {speed}.");
                    return;
            }
            await player.Commands!.Reply($"{who}: verbs: skills, skill <name|0xskill> [id], kill [id], life <pct> [id], "
                + "anim <index> [speed] [id], list.");
        }, "entity")
        .Add("ai", "/ai [on|off] (monsters chase you when you come near; emerge monsters rise either way)", async args =>
        {
            if (args.Length > 0) player.AiEnabled = !args[0].Equals("off", StringComparison.OrdinalIgnoreCase);
            await player.Commands!.Reply($"Monster AI is {(player.AiEnabled ? "on" : "off")}.");
        });
    }
}
