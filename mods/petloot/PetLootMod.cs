using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Modules.CosmeticsAndPets;
using GameServer.Modding;

public sealed class PetLootMod : IMod
{
    public sealed record Profile(bool Enabled, bool Gold, int Range, bool CommonCurrency, Dictionary<string, int> Rules)
    {
        public double MovementSpeed { get; init; } = 2;
        public bool AllCurrency { get; init; }
        public bool DivinationCards { get; init; }
        public bool MapFragments { get; init; }
    }
    public sealed record Choices(bool Enabled, bool Gold, int Range, bool CommonCurrency,
        Dictionary<string, int> Rules, Dictionary<string, Profile> Profiles)
    {
        public double MovementSpeed { get; init; } = 2;
        public bool AllCurrency { get; init; }
        public bool DivinationCards { get; init; }
        public bool MapFragments { get; init; }
    }

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
        // A hand-edited settings file gets the same 0.25-10 range /petloot speed and Waypoint enforce.
        choices = loaded with { Rules = Normalize(loaded.Rules), MovementSpeed = Speed(loaded.MovementSpeed),
            Profiles = loaded.Profiles.ToDictionary(p => p.Key,
                p => p.Value with { Rules = Normalize(p.Value.Rules), MovementSpeed = Speed(p.Value.MovementSpeed) }) };
        GameEvents.Tick.Add(mod.Manifest.Id, Tick);
        GameEvents.MovementSpeed.Add(mod.Manifest.Id, (conn, id, speed) =>
        {
            if (!mod.Lifetime.IsActive) return speed;
            lock (conn.Pets)
                return conn.Pets.ContainsKey(id) ? speed * ProfileOf(conn).MovementSpeed : speed;
        });
        GameEvents.Command(mod.Manifest.Id, "petloot", "/petloot [on|off|toggle|list|nearby|gold|common|range|speed|allow|block|remove|reset]", Command);
        mod.Log("opt-in pet pickup; /petloot for this character's rules.");
    }

    static string Key(ClientState conn) => string.Join("|", new[] { conn.Account ?? "", conn.League ?? "Standard", Program.CharacterName(conn) }
        .Select(Uri.EscapeDataString));

    static double Speed(double value) => double.IsFinite(value) ? Math.Clamp(value, .25, 10) : 2;

    static Dictionary<string, int> Normalize(Dictionary<string, int> rules)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in rules) result[key] = value;
        return result;
    }

    Profile ProfileOf(ClientState conn)
    {
        lock (choicesGate)
            return choices.Profiles.GetValueOrDefault(Key(conn))
                ?? new(choices.Enabled, choices.Gold, choices.Range, choices.CommonCurrency, choices.Rules)
                    {
                        MovementSpeed = choices.MovementSpeed, AllCurrency = choices.AllCurrency,
                        DivinationCards = choices.DivinationCards, MapFragments = choices.MapFragments,
                    };
    }

    sealed record Candidate(uint Id, WorldItemObject Item, ItemCatalog.BaseType Type);

    bool Active(ClientState conn) => mod.Lifetime.IsActive && Game.CurrentGameRules && !conn.Closed
        && !conn.LeavingArea && !conn.Paused && conn.PlayerSent && conn.PlayerLife > 0 && !conn.RageRetired
        && Program.ModulesFor(conn).Contains("Game") && Program.ModulesFor(conn).Contains("Cosmetics")
        && !Game.HardcoreProgressBlocked(conn) && GameEvents.AllowsLoot(conn);

    static bool Visible(ClientState conn, uint id)
    {
        var origin = conn.Instance.Ground.Origin(id);
        return conn.SeenGround.Contains(id) && (origin.Shared || ReferenceEquals(origin.By, conn));
    }

    static Candidate[] Nearby(ClientState conn, Profile profile)
    {
        lock (conn.Monsters)
        {
            var pos = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
            var items = Game.ItemsFor(conn);
            var nearby = new List<Candidate>();
            foreach (var (id, item) in conn.Instance.Ground.Within(pos.X, pos.Y, Math.Clamp(profile.Range, 5, 100), _ => true))
                if (Visible(conn, id) && items.ByHash(item.Item.TypeHash) is { } type)
                    nearby.Add(new(id, item, type));
            return nearby.ToArray();
        }
    }

    static bool Accept(Candidate drop, Profile profile)
    {
        bool gold = Game.IsGold(drop.Item);
        if (!profile.Enabled || drop.Item.PlayerDropped || drop.Type.Quest is not null
            || (gold ? !profile.Gold : drop.Type.MaxStack <= 1)) return false;
        if (!profile.Rules.TryGetValue(drop.Type.Id, out int minimum)
            && !profile.Rules.TryGetValue(drop.Type.Name, out minimum))
        {
            bool selected = gold || profile.CommonCurrency && Common.Contains(drop.Type.Name)
                || drop.Type.Class switch
                {
                    "StackableCurrency" => profile.AllCurrency,
                    "DivinationCard" => profile.DivinationCards,
                    "MapFragment" => profile.MapFragments,
                    _ => false,
                };
            minimum = selected ? 1 : 0;
        }
        return minimum > 0 && drop.Item.Item.StackSize >= minimum;
    }

    async Task Release(ClientState conn, RunState state)
    {
        uint? pet = state.Pet;
        state.Item = state.Pet = null;
        if (pet is uint id && Active(conn))
            await CosmeticsFeature.WalkPetTo(conn, id, conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds), () => Active(conn));
    }

    static Candidate? FindTarget(ClientState conn, RunState state, Profile profile)
    {
        double now = conn.Clock.Elapsed.TotalSeconds;
        var from = conn.Walk.Position(now);
        lock (conn.Pets)
        {
            if (state.Pet is uint id && conn.Pets.TryGetValue(id, out var pet))
                from = pet.Walk.Position(now);
            else
                state.Item = state.Pet = null;
        }

        Candidate? nearest = null;
        double nearestDistance = double.PositiveInfinity;
        foreach (var drop in Nearby(conn, profile))
        {
            if (!Accept(drop, profile) || state.RetryAfter.ContainsKey(drop.Id)) continue;
            if (drop.Id == state.Item) return drop;
            double distance = Distance(from.X, from.Y, drop.Item.GridX, drop.Item.GridY);
            if (distance < nearestDistance) { nearestDistance = distance; nearest = drop; }
        }
        return nearest;
    }

    async Task<bool> StartWalk(ClientState conn, RunState state, Candidate drop)
    {
        var to = (X: drop.Item.GridX, Y: drop.Item.GridY);
        double now = conn.Clock.Elapsed.TotalSeconds;
        uint collector;
        lock (conn.Pets)
        {
            if (conn.Pets.Count == 0) { state.Item = state.Pet = null; return false; }
            collector = state.Pet is uint id && conn.Pets.ContainsKey(id) ? id
                : conn.Pets.MinBy(p => { var pos = p.Value.Walk.Position(now); return Distance(pos.X, pos.Y, to.X, to.Y); }).Key;
        }
        state.Pet = collector;
        state.Item = drop.Id;
        state.Started = state.Seconds;
        if (await CosmeticsFeature.WalkPetTo(conn, collector, to, () => Active(conn))) return true;
        await Release(conn, state);
        return false;
    }

    async Task Tick(ClientState conn, double seconds)
    {
        var state = states.GetValue(conn, _ => new());
        await state.Gate.WaitAsync();
        try
        {
            var profile = ProfileOf(conn);
            if (!profile.Enabled || !Active(conn)) { await Release(conn, state); return; }
            state.Seconds += Math.Max(0, seconds);
            foreach (uint old in state.RetryAfter.Where(e => e.Value <= state.Seconds).Select(e => e.Key).ToArray())
                state.RetryAfter.Remove(old);
            var drop = FindTarget(conn, state, profile);
            if (drop is null) { await Release(conn, state); return; }
            if (drop.Id != state.Item && !await StartWalk(conn, state, drop)) return;
            var to = (X: drop.Item.GridX, Y: drop.Item.GridY);
            Game.GroundPickup? pickup = null;
            lock (conn.Monsters)
            lock (conn.Pets)
            {
                if (Active(conn) && state.Pet is uint id && conn.Pets.TryGetValue(id, out var pet) && Visible(conn, drop.Id)
                    && ReferenceEquals(conn.Instance.Ground.Get(drop.Id), drop.Item))
                {
                    var current = ProfileOf(conn);
                    double now = conn.Clock.Elapsed.TotalSeconds;
                    var owner = conn.Walk.Position(now); var collector = pet.Walk.Position(now);
                    if (Accept(drop, current) && Distance(owner.X, owner.Y, to.X, to.Y) <= Math.Clamp(current.Range, 5, 100))
                    {
                        if (Distance(collector.X, collector.Y, to.X, to.Y) <= 2)
                            pickup = Game.TakeGroundStack(conn, drop.Id, drop.Item);
                        else if (state.Seconds - state.Started < 10) return;
                    }
                }
            }
            state.Item = null;
            if (pickup is not null)
                await Game.SendPickup(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers, conn, drop.Id, pickup);
            else state.RetryAfter[drop.Id] = state.Seconds + 2;

            // Start the next walk before the following tick can send the collector back to its owner.
            if (Active(conn) && FindTarget(conn, state, profile) is { } next)
                await StartWalk(conn, state, next);
            else
                await Release(conn, state);
        }
        finally { state.Gate.Release(); }
    }

    static double Distance(int x, int y, int toX, int toY) => Math.Sqrt(Math.Pow((double)x - toX, 2) + Math.Pow((double)y - toY, 2));

    async Task Command(ClientState conn, string[] args)
    {
        var state = states.GetValue(conn, _ => new());
        await state.Gate.WaitAsync();
        try
        {
            if (!mod.Lifetime.IsActive) return;
            var profile = ProfileOf(conn);
            string action = args.Length == 0 ? "status" : args[0].ToLowerInvariant();
            if (action is "status" or "list")
            {
                string rules = string.Join(", ", profile.Rules.OrderBy(r => r.Key).Select(r => $"{r.Key}: {(r.Value == 0 ? "blocked" : "min " + r.Value)}"));
                await conn.Commands!.Reply($"Pet Loot: {(profile.Enabled ? "on" : "paused")}; gold {profile.Gold}; common currency {profile.CommonCurrency}; all currency {profile.AllCurrency}; cards {profile.DivinationCards}; fragments {profile.MapFragments}; range {profile.Range}; pet speed {profile.MovementSpeed.ToString(System.Globalization.CultureInfo.InvariantCulture)}x.\n"
                    + $"Overrides: {(rules.Length == 0 ? "none" : rules)}\n"
                    + "/petloot on|off|toggle; gold|common on|off; range 5-100; speed 0.25-10; allow <item> [minimum]; block|remove <item>; nearby; reset");
                return;
            }
            if (action == "nearby")
            {
                var nearby = Nearby(conn, profile).Take(12).ToArray();
                await conn.Commands!.Reply(nearby.Length == 0 ? "No nearby items." : string.Join("\n", nearby.Select(i => $"{i.Type.Name} x{i.Item.Item.StackSize}: {i.Type.Id}")));
                return;
            }
            if (action is "on" or "off" or "toggle")
                profile = profile with { Enabled = action == "on" || action == "toggle" && !profile.Enabled };
            else if (action is "gold" or "common" && args.Length == 2 && args[1] is "on" or "off")
                profile = action == "gold" ? profile with { Gold = args[1] == "on" } : profile with { CommonCurrency = args[1] == "on" };
            else if (action == "range" && args.Length == 2 && int.TryParse(args[1], out int range) && range is >= 5 and <= 100)
                profile = profile with { Range = range };
            else if (action == "speed")
            {
                if (args.Length != 2 || !double.TryParse(args[1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double speed) || !double.IsFinite(speed) || speed is < .25 or > 10)
                { await conn.Commands!.Reply("Use /petloot speed <0.25-10>; 1 is normal, 2 is double speed."); return; }
                profile = profile with { MovementSpeed = speed };
            }
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

            await Release(conn, state); // no pending target survives pause or a filter change
            lock (choicesGate)
            {
                var profiles = new Dictionary<string, Profile>(choices.Profiles);
                if (action == "reset") profiles.Remove(Key(conn)); else profiles[Key(conn)] = profile;
                var next = choices with { Profiles = profiles };
                mod.SaveSettings(next);
                choices = next;
            }
            await conn.Commands!.Reply(action == "reset" ? "Pet Loot: this character now uses the server defaults."
                : $"Pet Loot saved for {Program.CharacterName(conn)}: {(profile.Enabled ? "on" : "paused")}. /petloot list");
        }
        finally { state.Gate.Release(); }
    }
}
