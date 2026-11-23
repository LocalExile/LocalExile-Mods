using GameServer.Modding;

public sealed class BackpackMod : IMod
{
    public void Load(IModContext mod) => GameEvents.Stats.Add(mod.Manifest.Id, stats =>
        stats.Add(Stat.PrimalistAscendancyExpandedMainInventory, 1));
}
