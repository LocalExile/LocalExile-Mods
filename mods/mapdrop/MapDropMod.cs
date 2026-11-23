using System.Text.Json.Nodes;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace LocalExile.Mods.MapDrop;

public sealed class MapDropMod : IMod
{
    public void Load(IModContext mod)
    {
        var settings = mod.Settings<JsonObject>();
        var rates = new Dictionary<MonsterRarity, double>
        {
            [MonsterRarity.Normal] = Chance(settings, "normal"),
            [MonsterRarity.Magic] = Chance(settings, "magic"),
            [MonsterRarity.Rare] = Chance(settings, "rare"),
            [MonsterRarity.Unique] = Chance(settings, "unique"),
        };
        bool campaign = settings["campaign"]?.GetValue<bool>() == true;
        var atlas = new AtlasCatalog(Program.Items);
        var keys = new string?[17];
        for (int tier = 1; tier < keys.Length; tier++)
            if (atlas.KeyOfTier(tier) is uint hash && Program.Items.ByHash(hash) is { Supported: true } type)
                keys[tier] = type.Id;

        GameEvents.Drops.Add(mod.Manifest.Id, drops =>
        {
            int tier = drops.Player.Instance?.Map is { } map && map.Area.Hash16 == drops.Player.Layout.Hash
                ? map.Area.Tier : 0;
            if (tier <= 0)
            {
                if (!campaign || !LateCampaign(drops.Player.Layout.Id)) return;
                tier = 1;
            }
            if (keys[Math.Clamp(tier, 1, 16)] is { } key
                && Random.Shared.NextDouble() < rates.GetValueOrDefault(drops.Monster.Rarity))
                drops.Items.Add(key);
        });
    }

    static double Chance(JsonObject settings, string key) =>
        settings[key] is JsonValue value && value.TryGetValue<double>(out double chance) && double.IsFinite(chance)
            ? Math.Clamp(chance, 0, 1) : 0;

    static bool LateCampaign(string area) => area.StartsWith("2_8_", StringComparison.Ordinal)
        || area.StartsWith("2_9_", StringComparison.Ordinal) || area.StartsWith("2_10_", StringComparison.Ordinal);
}
