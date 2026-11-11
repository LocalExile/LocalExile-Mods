using GameServer.Modding;

public sealed class Toptierdrops : IMod
{
    public void Load(IModContext mod)
    {
        GameEvents.DropGeneration.Add(mod.Manifest.Id, drop => drop.SelectEquipmentAffix = HighestEligibleTier);
    }

    static ushort? HighestEligibleTier(ModCatalog.Mod selected, IReadOnlyList<(ModCatalog.Mod Mod, int Weight)> eligible, Random rng)
    {
        // Preserve the original series draw. Families alone can group effects such as flat and percentage bonuses.
        var series = eligible.Where(p => p.Mod.Domain == selected.Domain
            && p.Mod.GenerationType == selected.GenerationType && p.Mod.ModType == selected.ModType
            && (selected.Families.Count == 0 ? p.Mod.Hash == selected.Hash
                : p.Mod.Families.Count == selected.Families.Count && p.Mod.Families.All(selected.Families.Contains))
            && p.Mod.Stats.Select(s => s.Id).SequenceEqual(selected.Stats.Select(s => s.Id))).ToArray();
        int level = series.Max(p => p.Mod.Level);
        var highest = series.Where(p => p.Mod.Level == level).ToArray();
        if (highest.Length == 1) return highest[0].Mod.Hash;
        int roll = rng.Next(highest.Sum(p => p.Weight));
        foreach (var (mod, weight) in highest)
        {
            if (roll < weight) return mod.Hash;
            roll -= weight;
        }
        return selected.Hash;
    }
}
