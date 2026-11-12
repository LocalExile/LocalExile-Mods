using GameServer.Modding;

namespace LocalExile.Mods.AiOperator;

/// <summary>
/// AI Operator: the read-only commands that hand an outside AI a JSON picture of the world the character is in.
/// The command changes nothing the player can see except the chat reply (<c>README.md</c>).
/// </summary>
public sealed class AiOperatorMod : IMod, IDisposable
{
    readonly List<IDisposable> _events = [];

    public void Load(IModContext mod)
    {
        // /aiop (AiCommands.cs): the server tells an outside process what is around the character, whether a cell can
        // be walked to, and why a target cannot be used now. The name is "aiop", not "ai", because a command name
        // belongs to one mod and gm-commands registered /ai (monster AI) first: with both loaded, "/ai world" answered
        // the monster AI and this mod never ran.
        _events.Add(GameEvents.Command(mod.Manifest.Id, "aiop", AiCommands.Usage,
            (conn, args) => AiCommands.Run(conn, args), "world", "reach", "item", "why"));
    }

    public void Dispose()
    {
        foreach (IDisposable subscription in _events) subscription.Dispose();
    }
}
