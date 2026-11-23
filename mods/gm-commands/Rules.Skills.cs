using System.Net.Sockets;
using System.Collections.Frozen;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using Progression = GameServer.Core.World.Progression;
using DamageModel = GameServer.Core.World.DamageModel;
using DamagePacket = GameServer.Core.World.DamagePacket;

static partial class GmCommandsRules
{
    // An intentional /set_level reset invalidates older XP events before publishing its current level and points.
    internal static async Task SetLevel(Player player, int level)
    {
        if (HardcoreProgressBlocked(player)) return;
        var change = RecordExperienceReset(player, level);
        await PublishExperienceChange(player, change, $"/set_level {level}");
    }
}
