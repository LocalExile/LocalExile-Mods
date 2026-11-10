using GameServer.Modding;

namespace LocalExile.Mods.GmCommands;

/// <summary>The development and testing chat commands: teleport, items, monsters, character progress, test hits, protocol
/// probes and the campaign driver's /state (README.md). It hands its commands to every connection's chat commands, in every
/// league, as long as it is loaded.</summary>
public sealed class GmCommandsMod : IMod, IDisposable
{
    readonly List<IDisposable> _events = [];

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var e in _events) e.Dispose();
    }

    /// <inheritdoc/>
    public void Load(IModContext mod)
    {
        _events.Add(GameEvents.CommandsBuilding.Add(mod.Manifest.Id, global::GmCommands.Register));
        // The campaign driver's /state, /kill and /quest_preset (Rules.Campaign.cs).
        _events.Add(GameEvents.CommandsBuilding.Add(mod.Manifest.Id, AddCampaignCommands));
    }
}
