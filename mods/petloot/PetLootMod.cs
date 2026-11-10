using System.Runtime.CompilerServices;
using GameServer.Modding;

public sealed class PetLootMod : IMod
{
    public sealed record Profile(bool Enabled, bool Gold, int Range, bool CommonCurrency, Dictionary<string, int> Rules);
    public sealed record Choices(bool Enabled, bool Gold, int Range, bool CommonCurrency,
        Dictionary<string, int> Rules, Dictionary<string, Profile> Profiles);

    static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "Chaos Orb", "Divine Orb", "Exalted Orb", "Mirror of Kalandra", "Orb of Annulment",
        "Orb of Alteration", "Orb of Alchemy", "Orb of Augmentation", "Orb of Chance", "Orb of Transmutation",
        "Orb of Fusing", "Chromatic Orb", "Jeweller's Orb", "Orb of Regret", "Orb of Scouring",
        "Regal Orb", "Blessed Orb", "Vaal Orb", "Gemcutter's Prism", "Glassblower's Bauble"
    };
    readonly object choicesGate = new();
    readonly ConditionalWeakTable<ClientState, RunState> states = new();
    IModContext mod = null!;
    Choices choices = null!;

    sealed class RunState
    {
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal readonly Dictionary<uint, double> RetryAfter = [];
        internal uint? Item, Pet;
        internal double Seconds, Started;
    }

    public void Load(IModContext context)
    {
        mod = context;
        var loaded = mod.Settings<Choices>();
        choices = loaded with { Rules = Normalize(loaded.Rules), Profiles = loaded.Profiles.ToDictionary(p => p.Key,
            p => p.Value with { Rules = Normalize(p.Value.Rules) }) };
        GameEvents.Tick.Add(mod.Manifest.Id, Tick);
        GameEvents.Command(mod.Manifest.Id, "petloot", "/petloot [on|off|toggle|list|nearby|gold|common|range|allow|block|remove|reset]", Command);
        mod.Log("opt-in pet pickup; /petloot for this character's rules. Requires the player-loot API.");
    }

    static string Key(IPlayer player) => string.Join("|", new[] { player.Account ?? "", player.League, player.Character }
        .Select(Uri.EscapeDataString));

    static Dictionary<string, int> Normalize(Dictionary<string, int> rules)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in rules) result[key] = value;
        return result;
    }

    Profile ProfileOf(IPlayer player)
    {
        lock (choicesGate)
            return choices.Profiles.GetValueOrDefault(Key(player))
                ?? new(choices.Enabled, choices.Gold, choices.Range, choices.CommonCurrency, choices.Rules);
    }

    // Specific metadata wins over its display name. Zero excludes it, even from the common-currency preset.
    public static bool Accept(GroundItem item, Profile profile)
    {
        if (!profile.Enabled || item.PlayerDropped || item.Quest || (!item.Gold && item.MaxStack <= 1)) return false;
        if (item.Gold && !profile.Gold) return false;
        int? threshold = null;
        foreach (var (key, value) in profile.Rules)
            if (key.Equals(item.Metadata, StringComparison.OrdinalIgnoreCase)) { threshold = value; break; }
        if (threshold is null)
            foreach (var (key, value) in profile.Rules)
                if (key.Equals(item.Name, StringComparison.OrdinalIgnoreCase)) { threshold = value; break; }
        threshold ??= item.Gold || profile.CommonCurrency && Common.Contains(item.Name) ? 1 : 0;
        return threshold > 0 && item.StackSize >= threshold;
    }

    void Release(IPlayerPets? pets, RunState state)
    {
        if (state.Pet is uint pet) pets?.ReleasePet(pet, mod.Lifetime);
        state.Item = state.Pet = null;
    }

    async Task Tick(ClientState conn, double seconds)
    {
        var player = Program.PlayerOf(conn);
        var state = states.GetValue(conn, _ => new());
        await state.Gate.WaitAsync();
        try
        {
            var loot = GameEvents.GroundLootFor(conn);
            var controls = GameEvents.WornPetsFor(conn);
            var profile = ProfileOf(player);
            if (!mod.Lifetime.IsActive || player.Closed || !profile.Enabled || loot is null || controls is null)
            { Release(controls, state); return; }
            state.Seconds += Math.Max(0, seconds);
            foreach (uint old in state.RetryAfter.Where(e => e.Value <= state.Seconds).Select(e => e.Key).ToArray())
                state.RetryAfter.Remove(old);
            var pets = controls.Nearby;
            var items = loot.Nearby(profile.Range).Where(i => Accept(i, profile) && !state.RetryAfter.ContainsKey(i.Id)).ToArray();
            if (pets.Count == 0) { Release(controls, state); return; }
            var item = items.FirstOrDefault(i => i.Id == state.Item);
            var pet = pets.FirstOrDefault(p => p.Id == state.Pet);
            if (state.Item is not null && (item is null || pet is null)) Release(controls, state);
            if (state.Item is null)
            {
                var at = player.Position;
                item = items.OrderBy(i => Distance(at.X, at.Y, i.X, i.Y)).FirstOrDefault();
                if (item is null) return;
                pet = pets.OrderBy(p => Distance(p.X, p.Y, item.X, item.Y)).First();
                state.Item = item.Id; state.Pet = pet.Id; state.Started = state.Seconds;
            }
            if (item is null || pet is null) return;
            // Revalidate filters each tick and renew the short, host-owned pet-control lease. No background task survives unload.
            if (!Accept(item, ProfileOf(player)) || !mod.Lifetime.IsActive
                || !await controls.MovePet(pet.Id, item.X, item.Y, mod.Lifetime))
            { Release(controls, state); return; }
            if (Distance(pet.X, pet.Y, item.X, item.Y) <= 2)
            {
                bool picked = await loot.PickUp(item.Id, mod.Lifetime, pet.Id);
                if (!picked) state.RetryAfter[item.Id] = state.Seconds + 2;
                Release(controls, state);
            }
            else if (state.Seconds - state.Started >= 10)
            {
                state.RetryAfter[item.Id] = state.Seconds + 2;
                Release(controls, state); // a blocked walk never turns into a remote pickup
            }
        }
        finally { state.Gate.Release(); }
    }

    static double Distance(int x, int y, int toX, int toY) => Math.Sqrt(Math.Pow((double)x - toX, 2) + Math.Pow((double)y - toY, 2));

    async Task Command(ClientState conn, string[] args)
    {
        var player = Program.PlayerOf(conn);
        var state = states.GetValue(conn, _ => new());
        await state.Gate.WaitAsync();
        try
        {
            if (!mod.Lifetime.IsActive) return;
            var profile = ProfileOf(player);
            string action = args.Length == 0 ? "status" : args[0].ToLowerInvariant();
            var loot = GameEvents.GroundLootFor(conn);
            var controls = GameEvents.WornPetsFor(conn);
            if (loot is null || controls is null)
            { await conn.Commands!.Reply("Pet Loot needs a LocalExile build with ground-loot and cosmetic-pet controls; no items were picked up."); return; }
            if (action is "status" or "list")
            {
                string rules = string.Join(", ", profile.Rules.OrderBy(r => r.Key).Select(r => $"{r.Key}: {(r.Value == 0 ? "blocked" : "min " + r.Value)}"));
                await conn.Commands!.Reply($"Pet Loot: {(profile.Enabled ? "on" : "paused")}; gold {profile.Gold}; common currency {profile.CommonCurrency}; range {profile.Range}.\n"
                    + $"Overrides: {(rules.Length == 0 ? "none" : rules)}\n"
                    + "/petloot on|off|toggle; gold|common on|off; range 5-80; allow <item> [minimum]; block|remove <item>; nearby; reset");
                return;
            }
            if (action == "nearby")
            {
                var nearby = loot.Nearby(profile.Range).Take(12).ToArray();
                await conn.Commands!.Reply(nearby.Length == 0 ? "No nearby items." : string.Join("\n", nearby.Select(i => $"{i.Name} x{i.StackSize}: {i.Metadata}")));
                return;
            }
            if (action is "on" or "off" or "toggle")
                profile = profile with { Enabled = action == "on" || action == "toggle" && !profile.Enabled };
            else if (action is "gold" or "common" && args.Length == 2 && args[1] is "on" or "off")
                profile = action == "gold" ? profile with { Gold = args[1] == "on" } : profile with { CommonCurrency = args[1] == "on" };
            else if (action == "range" && args.Length == 2 && int.TryParse(args[1], out int range) && range is >= 5 and <= 80)
                profile = profile with { Range = range };
            else if (action is "allow" or "block" or "remove" && args.Length > 1)
            {
                int minimum = 1, end = args.Length;
                if (action == "allow" && args.Length > 2 && int.TryParse(args[^1], out int parsed)) { minimum = parsed; end--; }
                string name = string.Join(" ", args.Skip(1).Take(end - 1)).Trim().Trim('"');
                if (name.Length == 0 || minimum is < 1 or > 65535)
                { await conn.Commands!.Reply("Use /petloot allow <item name or metadata> [1-65535]."); return; }
                var rules = new Dictionary<string, int>(profile.Rules, StringComparer.OrdinalIgnoreCase);
                if (action == "remove") rules.Remove(name);
                else rules[name] = action == "block" ? 0 : minimum;
                profile = profile with { Rules = rules };
            }
            else if (action != "reset")
            { await conn.Commands!.Reply("Unknown option. /petloot lists commands and current rules."); return; }

            Release(controls, state); // no pending target survives pause or a filter change
            lock (choicesGate)
            {
                var profiles = new Dictionary<string, Profile>(choices.Profiles);
                if (action == "reset") profiles.Remove(Key(player)); else profiles[Key(player)] = profile;
                var next = choices with { Profiles = profiles };
                mod.SaveSettings(next);
                choices = next;
            }
            await conn.Commands!.Reply(action == "reset" ? "Pet Loot: this character now uses the server defaults."
                : $"Pet Loot saved for {player.Character}: {(profile.Enabled ? "on" : "paused")}. /petloot list");
        }
        finally { state.Gate.Release(); }
    }
}
