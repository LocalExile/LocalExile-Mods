using GameServer.Core.Protocol.Instance;
using static Game;

public sealed partial class Equipmentfusion
{
    static async Task SkinTransfer(ClientState conn, string[] args)
    {
        var (remote, stream, ciphers) = conn.Wire;
        var items = ItemsOf(conn);
        if (args.Length == 1 && args[0].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            var rows = new byte[] { PlayerInventories.MainInventory, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10,
                PlayerInventories.Weapon2, PlayerInventories.Offhand2 };
            await conn.Commands!.Reply(string.Join("; ", rows.SelectMany(row => items.Items(row).Select(i =>
                $"A {i.Id} row {row}: {ItemsFor(conn).ByHash(i.Item.TypeHash)?.Name}"
                + (i.Item.SkinTransferSource is { } b ? $" [B: {ItemsFor(conn).ByHash(b.TypeHash)?.Name}]" : "")))));
            return;
        }
        if (args.Length != 2 || !uint.TryParse(args[1], out uint target))
            throw new ArgumentException("/skin_transfer list | info <A item id> | bind <A item id> (hold B) | remove <A item id> (empty cursor)");
        if (args[0].Equals("info", StringComparison.OrdinalIgnoreCase))
        {
            if (items.RowOf(target) is not byte row || items.Items(row).FirstOrDefault(i => i.Id == target) is not { } a)
            { await conn.Commands!.Reply("Target A is missing"); return; }
            string Describe(Item item) => $"{ItemsFor(conn).ByHash(item.TypeHash)?.Name} q{item.Quality}: "
                + string.Join("; ", item.Mods.Select(ItemsFor(conn).Mods.Describe));
            await conn.Commands!.Reply($"A {target}: {Describe(a.Item)} | B: "
                + (a.Item.SkinTransferSource is { } source ? Describe(source) : "none"));
            return;
        }
        PlayerInventories.SkinTransferChange? change;
        string why;
        bool bind = args[0].Equals("bind", StringComparison.OrdinalIgnoreCase);
        if (bind) change = items.BindSkin(target, ShapeOf, out why);
        else if (args[0].Equals("remove", StringComparison.OrdinalIgnoreCase)) change = items.UnbindSkin(target, out why);
        else throw new ArgumentException("Use info, bind or remove");
        if (change is null) { await conn.Commands!.Reply(why); return; }
        await SendSkinTransfer(remote, stream, ciphers, conn, change, bind);
        await conn.Commands!.Reply(bind ? $"Skin transfer bound to A {target}; B preserved" : $"Skin transfer removed from A {target}; B returned to cursor");
    }
}
