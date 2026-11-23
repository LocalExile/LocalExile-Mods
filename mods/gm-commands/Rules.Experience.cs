using System.Collections.Concurrent;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using Progression = GameServer.Core.World.Progression;

static partial class GmCommandsRules
{
    internal static ExperienceChange RecordExperienceReset(Player player, int level)
    {
        var updates = LevelUpdatesFor(player);
        lock (updates.Gate)
        {
            if (HardcoreProgressBlocked(player))
            {
                uint held = CurrentExperience(player);
                return new(updates.Revision, held, held, false);
            }
            uint before = CurrentExperience(player), after = CharacterStats.ExperienceFor(level);
            CharacterSaves.SetExperience(CharacterName(player), after);
            player.Character.Experience = after;
            return new(++updates.Revision, before, after, true, Reset: true);
        }
    }
}
