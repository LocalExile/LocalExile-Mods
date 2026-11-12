using GameServer.Modding;

public sealed class BackpackMod : IMod
{
    public void Load(IModContext mod) => GameEvents.Stats.Add(mod.Manifest.Id, stats =>
        stats.Add("primalist_ascendancy_expanded_main_inventory", 1));
}
