using GameServer.Modding;

/// <summary>
/// Gives every character the third ring slot: <c>enable_ring_slot_3</c>, the stat The Unseen Hand
/// (<c>AlternateAscendancyKingInTheMists1</c>) grants when its node is allocated.
/// <para>
/// The Stats hook runs whenever the server rebuilds a character's stats, so the stat is there on login, after an area
/// change and after a Gateway restart; the client reads it from the player stats (S→C 0x1b3) and shows the slot.
/// </para>
/// </summary>
public sealed class ThirdRingSlot : IMod
{
    public void Load(IModContext mod) => GameEvents.Stats.Add(mod.Manifest.Id, stats =>
        stats.Add(Stat.EnableRingSlot3, 1));
}