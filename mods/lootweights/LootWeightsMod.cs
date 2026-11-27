using GameServer.Modding;

namespace LocalExile.Mods.LootWeights;

public sealed class LootWeightsMod : IMod
{
    sealed record Kinds(double Currency, double Gems, double Flasks, double Equipment);
    sealed record MapExtras(double Cards, double Scarabs);
    sealed record Choices(double Quantity, double Rarity, double Gold, double Maps, Kinds Kinds, MapExtras MapExtras,
        Dictionary<string, double> Items);

    public void Load(IModContext mod)
    {
        var choices = mod.Settings<Choices>();
        var items = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (name, factor) in choices.Items)
        {
            if (Program.Items.Find(name) is not { Supported: true } type)
            {
                mod.Log($"Loot Weights: skipping '{name}', not an item the server knows.");
                continue;
            }
            if (type.Class is not ("StackableCurrency" or "DivinationCard")
                && !type.Id.Contains("/Scarabs/", StringComparison.Ordinal))
            {
                mod.Log($"Loot Weights: skipping '{name}', only currency, divination cards and scarabs have a weight.");
                continue;
            }
            items[type.Id] = factor;
        }
        mod.Log($"Loot Weights: quantity x{choices.Quantity}, rarity x{choices.Rarity}, gold x{choices.Gold}, maps x{choices.Maps}; "
            + $"currency x{choices.Kinds.Currency}, gems x{choices.Kinds.Gems}, flasks x{choices.Kinds.Flasks}, "
            + $"equipment x{choices.Kinds.Equipment}; cards x{choices.MapExtras.Cards}, scarabs x{choices.MapExtras.Scarabs}; "
            + $"{items.Count} single item(s).");

        GameEvents.Drops.Add(mod.Manifest.Id, drops =>
        {
            drops.Quantity *= choices.Quantity;
            drops.Rarity *= choices.Rarity;
            drops.Gold *= choices.Gold;
            drops.MapRelated *= choices.Maps;
            var weights = drops.Weights;
            weights.Currency *= choices.Kinds.Currency;
            weights.Gems *= choices.Kinds.Gems;
            weights.Flasks *= choices.Kinds.Flasks;
            weights.Equipment *= choices.Kinds.Equipment;
            weights.Cards *= choices.MapExtras.Cards;
            weights.Scarabs *= choices.MapExtras.Scarabs;
            foreach (var (id, factor) in items)
                weights.Items[id] = weights.Item(id) * factor;
        });
    }
}
