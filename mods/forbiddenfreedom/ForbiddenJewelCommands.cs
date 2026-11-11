using GameServer.Core.Protocol.Instance;
using static Game;

public sealed partial class Forbiddenfreedom
{
    static async Task ForbiddenJewel(ClientState conn, string[] args)
    {
        var (remote, stream, ciphers) = conn.Wire;
        if (args.Length == 0)
        {
            var granted = Game.ForbiddenPassiveNodes(conn);
            await conn.Commands!.Reply("Forbidden pairs (cross-class; each node once): " +
                string.Join(", ", granted.Select(id => ForbiddenJewelCatalogue.Find(id.ToString())!.Name).DefaultIfEmpty("none")));
            return;
        }
        if (args[0].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            await conn.Commands!.Reply(string.Join("\n", ForbiddenJewelCatalogue.All.Select(n => $"{n.Id}: {n.Name} ({n.Ascendancy})")));
            return;
        }
        if (args.Length < 2 || ForbiddenJewelCatalogue.Find(string.Join(' ', args[1..])) is not { } notable
            || ForbiddenJewelCatalogue.Create(ItemsFor(conn), args[0], notable.Id) is not { } jewel)
            throw new ArgumentException("Use /forbidden_jewel flame|flesh <one name or graph id from list>");
        var items = ItemsOf(conn);
        if (items.FreeCell(PlayerInventories.MainInventory, 1, 1, ShapeOf) is not { } cell)
            throw new ArgumentException("No room in the inventory");
        var added = items.Put(PlayerInventories.MainInventory, new InventoryItem(items.NewItemId(), cell.X, cell.Y, jewel));
        await Program.Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
            items.NextRevision(PlayerInventories.MainInventory), [], [added]), "0x00bf inventory 1: /forbidden_jewel");
        await conn.Commands!.Reply($"Forbidden {args[0]} ({notable.Name}) added; socket its matching opposite to grant the node across classes.");
    }
}
