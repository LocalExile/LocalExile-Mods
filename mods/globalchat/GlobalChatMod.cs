using GameServer.Modding;

namespace GlobalChat;

/// <summary>Moves chat to the global chat server while the mod is on; off, the Gateway's local chat server has it again.</summary>
public sealed class GlobalChatMod : IMod
{
    sealed record Choices(string Host, int Port);

    /// <inheritdoc/>
    public void Load(IModContext mod)
    {
        var server = mod.Settings<Choices>();
        mod.UseChatServer(server.Host, server.Port);
    }
}
