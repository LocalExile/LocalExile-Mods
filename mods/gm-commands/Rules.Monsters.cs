using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.Diagnostics;
using DamageModel = GameServer.Core.World.DamageModel;
using DamageType = GameServer.Core.World.DamageType;
using DamagePacket = GameServer.Core.World.DamagePacket;

static partial class GmCommandsRules
{
    // A spawned monster's id from the argument if it is 0x hex, else the last spawned one.
    internal static uint? MonsterArg(Player player, string[] args, int i)
    {
        if (args.Length > i && (args[i].StartsWith("0x", StringComparison.OrdinalIgnoreCase)))
        {
            uint id = Convert.ToUInt32(args[i], 16);
            return player.Area.Monsters.ContainsKey(id) ? id : null;
        }
        return player.Area.Monsters.Count == 0 ? null : player.Area.Monsters.Keys.Last();
    }

}
