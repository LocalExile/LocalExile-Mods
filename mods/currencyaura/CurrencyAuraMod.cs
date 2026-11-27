// Currency Aura: stackable currency (and, by choice, divination cards and scarabs) near the player is picked up by
// itself while playing, the way gold is. A stack is taken only when all of it fits, and only the player's own or shared
// drops; items the player dropped stay. Gear, flasks and gems are never taken. A lockstep client only gets items whose
// create it already has.
public sealed class CurrencyAuraMod : IMod
{
    public sealed record Choices(bool Enabled, int Radius, bool Cards, bool Scarabs, List<string> Skip);
    const string Usage = "/currencyaura [on|off|radius <5-100>|cards on|off|scarabs on|off|skip <item>|unskip <item>]";
    IModContext mod = null!;
    volatile Choices choices = null!;

    public void Load(IModContext context)
    {
        mod = context;
        choices = Clamp(mod.Settings<Choices>());
        GameEvents.AreaTick.Add(mod.Manifest.Id, GameEvents.Always(Tick));
        GameEvents.Command(mod.Manifest.Id, "currencyaura", Usage, Command);
        mod.Log($"currency auto-loot {(choices.Enabled ? "on" : "off")} within {choices.Radius} cells.");
    }

    static Choices Clamp(Choices c) => c with { Radius = Math.Clamp(c.Radius, 5, 100), Skip = c.Skip ?? [] };

    static bool Wanted(ItemCatalog.BaseType t, Choices c) => t.Quest is null && t.MaxStack > 1
        && (t.Class == "StackableCurrency" || c.Cards && t.Class == "DivinationCard"
            || c.Scarabs && t.Class == "MapFragment" && t.Id.Contains("/Scarabs/"))
        && !c.Skip.Any(s => s.Equals(t.Name, StringComparison.OrdinalIgnoreCase) || s.Equals(t.Id, StringComparison.OrdinalIgnoreCase));

    bool Active(Player player) => mod.Lifetime.IsActive && Game.CurrentGameRules && !player.Closed && !player.LeavingArea
        && !player.Paused && player.Connection.PlayerSent && player.PlayerLife > 0 && !player.RageRetired
        && Program.ModulesFor(player).Contains("Game") && !Game.HardcoreProgressBlocked(player) && GameEvents.AllowsLoot(player);

    async Task Tick(Player player, double seconds)
    {
        var c = choices;
        if (!c.Enabled || !Active(player)) return;
        var at = player.Walk.Position(player.Clock.Elapsed.TotalSeconds);
        var items = Game.ItemsFor(player);
        foreach (var (id, ground) in player.Instance.Ground.Within(at.X, at.Y, c.Radius, _ => true).ToList())
        {
            if (ground.PlayerDropped || items.ByHash(ground.Item.TypeHash) is not { } type || !Wanted(type, c)) continue;
            var (by, shared) = player.Instance.Ground.Origin(id);
            if (!shared && !ReferenceEquals(by, player)) continue;
            if (player.Connection.LockstepClient && !player.Connection.WorldOutput.Has(id)) continue;
            if (Game.TakeGroundStack(player, id, ground) is not { } pickup) continue;
            mod.Log($"{type.Name} x{ground.Item.StackSize}");
            await Game.SendPickup(player, id, pickup);
            if (!Active(player)) return;
        }
    }

    static bool? OnOff(string[] args) => args.Length > 1 ? args[1].ToLowerInvariant() switch { "on" => true, "off" => false, _ => null } : null;

    async Task Command(Player player, string[] args)
    {
        var c = choices;
        string action = args.Length == 0 ? "" : args[0].ToLowerInvariant();
        Choices? next = action switch
        {
            "" => c,
            "on" or "off" => c with { Enabled = action == "on" },
            "radius" when args.Length > 1 && int.TryParse(args[1], out int r) => Clamp(c with { Radius = r }),
            "cards" when OnOff(args) is bool on => c with { Cards = on },
            "scarabs" when OnOff(args) is bool on => c with { Scarabs = on },
            "skip" or "unskip" when args.Length > 1 => c with { Skip = Edit(c.Skip, string.Join(" ", args.Skip(1)).Trim().Trim('"'), action == "skip") },
            _ => null,
        };
        if (next is null) { await player.Commands!.Reply("Use " + Usage + "."); return; }
        await player.Commands!.Reply($"Currency Aura: {(next.Enabled ? "on" : "off")}, radius {next.Radius} cells, " +
            $"cards {(next.Cards ? "on" : "off")}, scarabs {(next.Scarabs ? "on" : "off")}. " +
            $"Skipped: {(next.Skip.Count == 0 ? "none" : string.Join(", ", next.Skip))}.");
        if (next != c) { choices = next; mod.SaveSettings(next); }
    }

    static List<string> Edit(List<string> list, string item, bool add)
    {
        var result = list.Where(s => !s.Equals(item, StringComparison.OrdinalIgnoreCase)).ToList();
        if (add && item.Length > 0) result.Add(item);
        return result;
    }
}
