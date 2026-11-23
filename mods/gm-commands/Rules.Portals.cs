using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

static partial class GmCommandsRules
{
    internal static void SendMapPortalsLater(Player player, uint deviceId, ushort area, int delayMs) =>
        _ = Task.Delay(delayMs).ContinueWith(async _ => { if (!player.Closed) await SendMapPortals(player, deviceId, area); });

    // Removes and recreates the map portals that stand, at the same places and with the current rotation, then lists them
    // again: the client builds a portal's visual from its object, so a rotated portal effect can be tried without reopening
    // the device. Returns how many were rebuilt.
    internal static async Task<int> RebuildMapPortals(Player player)
    {
        uint deviceId;
        lock (player.HideoutDoodads) deviceId = player.HideoutDoodads.Keys.FirstOrDefault();
        List<(uint Id, int X, int Y, int Index)> old;
        lock (player.MapPortals)
        {
            old = [.. player.MapPortals.Select(kv => (kv.Key, kv.Value.X, kv.Value.Y, kv.Value.Index))];
            player.MapPortals.Clear();
        }
        if (old.Count == 0) return 0;
        uint type = InstanceEntities.ObjectTypeHash(MapPortalObject.MetadataPath);
        foreach (var (id, _, _, _) in old)
            await Send(player, InstanceProtocol.RemoveObject(id), $"0x02bd remove map portal 0x{id:x} (rebuild)");
        // Each portal is listed a moment after its create: 0x008f in the same burst as the 0x2bb reaches none of them (the
        // client has not built the object yet), which is why they then stay invisible.
        ushort area = MapDevice.OpenOf(CharacterName(player))?.Area.Hash16 ?? player.Layout.Hash;
        foreach (var (_, x, y, index) in old)
        {
            uint id = player.NextObjectId++;
            await Send(player, InstanceEntities.CreateObject(id, type,
                    new MapPortalObject(x, y, index, MapPortalType, Open: 1, Rotation: player.PortalRotationRadians).Blob()),
                $"0x02bb create map portal 0x{id:x} (place {index}) at ({x}, {y}) rotation {player.PortalRotationDegrees:0.#}°");
            await Send(player, InstanceProtocol.EnterWakeZone(id), null);
            lock (player.MapPortals) player.MapPortals[id] = (x, y, index);
            await Task.Delay(MapPortalListMs);
            await SendMapPortals(player, deviceId, area);
        }
        return old.Count;
    }

    // Created closed (hidden), a map device portal is drawn and targetable once it is opened (Transitionable 0 → 1). Sent in
    // the same burst as its creation, the update hung the client (live 2026-10-20), so it goes once the portal is built.
    internal static void OpenMapPortalLater(Player player, uint id, int delayMs) =>
        _ = Task.Delay(delayMs).ContinueWith(async _ =>
        {
            bool still;
            lock (player.MapPortals) still = player.MapPortals.ContainsKey(id);
            if (still && !player.Closed) await Send(player, MapPortalObject.OpenUpdate(id), $"0x02bc map portal 0x{id:x} opens");
        });
}
