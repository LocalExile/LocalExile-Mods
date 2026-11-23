using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using DamageModel = GameServer.Core.World.DamageModel;
using DamageType = GameServer.Core.World.DamageType;
using DamageOverTime = GameServer.Core.World.DamageOverTime;

static partial class GmCommandsRules
{
    internal static async Task SetGod(Player player, bool on)
    {
        player.God = on;
        if (on) GodCharacters[CharacterName(player)] = true; else GodCharacters.TryRemove(CharacterName(player), out _);
        player.Pools = PoolsFor(player, player.Pools.Level);
        player.ApplyMovementSpeed();
        player.PlayerLife = on ? player.PlayerMaxLife : Math.Min(player.PlayerLife, player.PlayerMaxLife);
        player.PlayerMana = on ? (uint)UnreservedMana(player) : Math.Min(player.PlayerMana, (uint)UnreservedMana(player));
        TrimLeechLocked(player);
        Console.WriteLine($"[gateway] {player.Connection.Remote} god mode {(on ? "on" : "off")}: life {player.Pools.Life}, mana {player.Pools.Mana}, {ReservedMana(player)} reserved");
        await SendCharacterStats(player, PlayerStatChange.God);
        Note($"0x01b1 player life {player.PlayerLife}");
        await SendPlayerMana(player, $"0x01b1 player mana {player.PlayerMana}");
    }

    internal static DamageType? ParseDamageType(string text) => text.ToLowerInvariant() switch
    {
        "fire" => DamageType.Fire,
        "cold" => DamageType.Cold,
        "lightning" or "light" => DamageType.Lightning,
        "chaos" => DamageType.Chaos,
        "physical" or "phys" => DamageType.Physical,
        _ => null,
    };
}
