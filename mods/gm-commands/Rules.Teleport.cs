using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

static partial class GmCommandsRules
{
    // /tp (dev): put the player somewhere in the area at once, for testing without walking there (TeleportTargets.cs).
    // The same move as an arena gate's (Program.Travel.cs): the current action ends (a pending hit or walk-up no longer
    // happens), the server's position estimate jumps to the cell, and S→C 0x1b7 moves the client's player there.
    internal static async Task DevTeleport(Player player, string[] args)
    {
        // The interactive objects and ground items too (by the ids /state reports), after the others so that the
        // indices of equal names stay as they were; then the room and tile objects the quest engine watches without
        // creating them (On.Near, On.SpawnsNear), by type.
        var candidates = TeleportTargets.Candidates(player);
        candidates.AddRange(StateObjects(player).Select(o => new TeleportTargets.Target(o.Type[(o.Type.LastIndexOf('/') + 1)..], o.Id, o.X, o.Y)));
        candidates.AddRange(player.QuestNear.Select(n => (n.Rule.ObjectType, n.X, n.Y)).Concat(player.QuestNearSpawns.Select(n => (n.Rule.ObjectType, n.X, n.Y)))
            .Distinct().Select(n => new TeleportTargets.Target(n.ObjectType[(n.ObjectType.LastIndexOf('/') + 1)..], null, n.X, n.Y)));
        candidates.AddRange((player.Instance?.Ground.All() ?? []).Select(g => new TeleportTargets.Target(
            ItemsFor(player).ByHash(g.Item.Item.TypeHash)?.Name ?? "item", g.Id, g.Item.GridX, g.Item.GridY)));
        if (args.Length > 0 && args[0].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            await player.Commands!.Reply(TeleportTargets.List(candidates, args.Length > 1 ? string.Join(' ', args[1..]) : null));
            return;
        }
        // A teleport sent while the player is dead leaves them where they died (Resurrect, Program.Player.cs).
        if (player.PlayerLife == 0) { await player.Commands!.Reply("You are dead."); return; }
        if (TeleportTargets.Resolve(player, args, candidates, out string? error) is not { } to)
        {
            await player.Commands!.Reply(error!);
            return;
        }
        using (ProjectileMutation(player))
        {
            Interlocked.Increment(ref player.ActionCount);
            player.Walk.Teleport(to.X, to.Y, ProjectileWorldTime(player));
        }
        await SendWorldRecord(player, player.Id, InstanceProtocol.Teleport(player.Id, to.X, to.Y),
            $"0x01b7 player /tp to ({to.X}, {to.Y}){(to.Next is null ? "" : $" next to {to.Next}")}");
        await player.Commands!.Reply($"Teleported to ({to.X}, {to.Y}) in {player.Layout.Id}{(to.Next is null ? "" : $", next to {to.Next}")}.");
    }
}
