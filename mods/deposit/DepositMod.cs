// Deposit All: /deposit at the personal stash sends every main-inventory item that a special stash tab holds (currency,
// fragments, divination cards, essences, gems, flasks ...) to the first shown such tab, as one Ctrl + click each, so a
// full tab stops only what doesn't fit. Gear, items no chosen special tab holds and the keep list stay.
public sealed class DepositMod : IMod
{
    public sealed record Choices(List<string> Keep, List<string> Tabs);
    const int Reach = 30;    // cells from the stash
    const string Usage = "/deposit [keep|keep add <item>|keep remove <item>] (at your stash: currency, cards, essences ... go to their special tabs)";

    // Settings' tab kinds → StashRules tab types.
    static readonly Dictionary<string, byte[]> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["currency"] = [StashRules.Currency], ["fragments"] = [StashRules.Fragment], ["cards"] = [StashRules.Divination],
        ["essences"] = [StashRules.Essence], ["gems"] = [StashRules.Gem], ["flasks"] = [StashRules.Flask],
        ["league"] = [StashRules.Delve, StashRules.Blight, StashRules.Delirium, StashRules.Ultimatum],
    };
    IModContext mod = null!;

    public void Load(IModContext context)
    {
        mod = context;
        GameEvents.Command(mod.Manifest.Id, "deposit", Usage, Run);
    }

    Choices Current()
    {
        var c = mod.Settings<Choices>();
        return c with { Keep = c.Keep ?? [], Tabs = c.Tabs ?? [] };
    }

    async Task Run(Player player, string[] args)
    {
        var c = Current();
        if (args.Length > 0 && args[0].Equals("keep", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length > 2 && args[1].ToLowerInvariant() is "add" or "remove")
            {
                string item = string.Join(" ", args.Skip(2)).Trim().Trim('"');
                var keep = c.Keep.Where(s => !s.Equals(item, StringComparison.OrdinalIgnoreCase)).ToList();
                if (args[1].Equals("add", StringComparison.OrdinalIgnoreCase) && item.Length > 0) keep.Add(item);
                c = c with { Keep = keep };
                mod.SaveSettings(c);
            }
            await player.Commands!.Reply(c.Keep.Count == 0 ? "/deposit keeps nothing." : "/deposit keeps: " + string.Join(", ", c.Keep) + ".");
            return;
        }
        if (args.Length > 0) { await player.Commands!.Reply("Use " + Usage + "."); return; }
        if (!mod.Lifetime.IsActive || player.Closed || player.LeavingArea || player.PlayerLife == 0) return;
        if (!Program.ModulesFor(player).Contains("Game")) { await player.Commands!.Reply("/deposit needs the Game module."); return; }
        var (x, y) = player.Walk.Position(Game.AreaTime(player));
        if (player.StashObject == 0 || player.GuildStashOpen
            || Math.Abs(x - player.StashAt.X) > Reach || Math.Abs(y - player.StashAt.Y) > Reach)
        {
            await player.Commands!.Reply("Stand at your stash to use /deposit.");
            return;
        }
        var catalog = Game.ItemsFor(player);
        var rules = catalog.Stash;
        if (!rules.Loaded) { await player.Commands!.Reply("/deposit: the stash rules are not loaded."); return; }
        var types = c.Tabs.SelectMany(k => Kinds.GetValueOrDefault(k) ?? []).ToHashSet();
        var keepSet = c.Keep.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stash = Game.PersonalStashOf(player);
        var items = Game.ItemsOf(player);
        // Only the chosen special tabs (a grid tab holds anything); hidden and collection tabs are not shown.
        var tabs = stash.Records(null).Where(t => types.Contains(t.Type) && stash.IsShownTab(t.Index)).ToList();
        var plan = items.Items(PlayerInventories.MainInventory)
            .Where(i => catalog.ByHash(i.Item.TypeHash) is not { } type || !keepSet.Contains(type.Name) && !keepSet.Contains(type.Id))
            .Select(i => (i.Id, Tab: tabs.FirstOrDefault(t => rules.Holds(t.Type, i.Item.TypeHash))))
            .Where(p => p.Tab is not null).ToList();
        int moved = 0, stayed = 0;
        foreach (var (id, tab) in plan)
        {
            var done = ItemMoves.InventoryToStash(stash, items, tab!.Index, id, Game.ShapeOf, out string refused);
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= /deposit: move item {id} to stash tab {tab.Index} ({tab.Name})"
                + (done is null ? $": refused ({refused})" : ""));
            if (done is null) { stayed++; continue; }
            await Game.SendTransfer(player, items, stash, done, tabFirst: false);
            if (done.LeftInInventory is null) moved++; else stayed++;
        }
        Console.WriteLine($"[gateway] {player.Connection.Remote} /deposit: {moved} deposited, {stayed} didn't fit");
        await player.Commands!.Reply(moved + stayed == 0 ? "Nothing to deposit."
            : $"Deposited {moved} stack(s)" + (stayed > 0 ? $", {stayed} didn't fit." : "."));
    }
}
