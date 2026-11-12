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
        // /ai world (AiCommands.cs): the server tells an outside process what is around the character.
        _events.Add(GameEvents.Command(mod.Manifest.Id, "ai", AiCommands.Usage,
            (conn, args) => AiCommands.Run(conn, args), "world"));
    }

    public void Dispose()
    {
        foreach (IDisposable subscription in _events) subscription.Dispose();
    }
}
