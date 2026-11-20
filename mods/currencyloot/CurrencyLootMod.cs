using System.Text.Json.Nodes;
using GameServer.Modding;

namespace LocalExile.Mods.CurrencyLoot;

public sealed class CurrencyLootMod : IMod
{
    public void Load(IModContext mod)
    {
        var settings = mod.Settings<JsonObject>();
        var rates = new Dictionary<string, (double Rate, int Level)>(StringComparer.OrdinalIgnoreCase);
        if (settings["items"] is JsonObject items)
            foreach (var (name, value) in items)
            {
                if (value is not JsonValue amount || !amount.TryGetValue<double>(out double rate)
                    || !double.IsFinite(rate) || rate <= 0) continue;
                if (Program.Items.Find(name) is not { Supported: true, Stackable: true, Class: "StackableCurrency", Quest: null } type
                    || type.Id == ItemDrops.GoldId)
                {
                    mod.Log($"Currency Loot: skipping unknown or unsupported currency '{name}'.");
                    continue;
                }
                rates[type.Id] = (Math.Min(rate, 20), type.DropLevel);
                if (rates.Count == 40) break;
            }
        GameEvents.Drops.Add(mod.Manifest.Id, drops =>
        {
            foreach (var (id, (rate, level)) in rates)
            {
                if (drops.Monster.Level < level) continue;
                int count = (int)rate;
                if (rate > count && Random.Shared.NextDouble() < rate - count) count++;
                for (int i = 0; i < count; i++) drops.Items.Add(id);
            }
        });
    }
}
