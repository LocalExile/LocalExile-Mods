using System.Net.Sockets;
using System.Text.Json;

namespace LocalExile.Mods.GmWeb;

/// <summary>
/// GET /tp: the named places the player's area offers to /tp — exactly the list gm-commands builds
/// (TeleportTargets.Candidates, which is also what a sent /tp resolves against), in the same order, each with a
/// kind for the page's grouping and a transition's destination area. Names are matched on the page, which holds
/// the name tables (GET /names); here they would need another copy of the same data.
/// </summary>
static class TpApi
{
    const int Cap = 800;
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    sealed record TargetView(string Name, uint? Id, int X, int Y, string Kind, string? Dest, string? DestEn);
    sealed record AreaView(string Id, string Name, uint Seed, int GridW, int GridH);
    sealed record TpView(bool Online, string Character, AreaView Area, int PlayerX, int PlayerY, bool Truncated, List<TargetView> Targets);

    internal static async Task Handle(NetworkStream stream, string method)
    {
        if (method != "GET") { await GmWebMod.Write(stream, 404, "text/plain", "not found"u8.ToArray()); return; }
        try
        {
            var player = GmWebMod.OnlinePlayer();
            if (player is null) { await WriteJson(stream, 503, new { online = false, error = "nobody is in game" }); return; }
            // The area's live collections: read them under the frames gate, like the commands themselves do.
            var view = await player.Frames.Run(async () =>
            {
                double now = player.Clock.Elapsed.TotalSeconds;
                var targets = new List<TargetView>();
                foreach (var (id, m) in player.Area.Monsters)
                {
                    var (x, y) = m.Walk.Position(now);
                    targets.Add(new(m.Label, id, x, y, "monster", null, null));
                }
                lock (player.Npcs)
                    foreach (var (id, n) in player.Npcs)
                    {
                        // Same two rows per NPC as TeleportTargets: by name, and by the type's last path part
                        // (several NPCs share a name; the campaign steps name the one they mean).
                        targets.Add(new(n.Npc.Name, id, n.X, n.Y, "npc", null, null));
                        string type = n.Npc.Id[(n.Npc.Id.LastIndexOf('/') + 1)..];
                        if (!type.Equals(n.Npc.Name, StringComparison.OrdinalIgnoreCase)) targets.Add(new(type, id, n.X, n.Y, "npc", null, null));
                    }
                foreach (var (id, t) in player.Layout.AreaTransitions.Where(e => TransitionShown(player, e.Id, e.Transition)))
                {
                    string? dest = AreaTransitionCatalog.IdOf(t.DestinationHash16);
                    targets.Add(new($"transition to {dest ?? $"0x{t.DestinationHash16:x4}"}", id, t.GridX, t.GridY, "transition", dest,
                        AreaTransitionCatalog.NameOf(t.DestinationHash16)));
                }
                if (player.Layout.Waypoint is { } w) targets.Add(new("waypoint", null, w.GridX, w.GridY, "waypoint", null, null));
                targets.Add(new("arrival", null, player.Layout.SpawnX, player.Layout.SpawnY, "arrival", null, null));
                bool truncated = false;
                foreach (var (name, x, y) in CapturedObjects(player.Layout.Id, player.Seed))
                {
                    if (targets.Count >= Cap) { truncated = true; break; }
                    targets.Add(new(name, null, x, y, "object", null, null));
                }
                var (px, py) = player.Walk.Position(now);
                var layout = player.Layout;
                return new TpView(true, player.Character.Name ?? "",
                    new AreaView(layout.Id, AreaTransitionCatalog.NameOf(layout.Hash) ?? "", player.Seed, layout.GridW, layout.GridH),
                    px, py, truncated, targets);
            });
            await WriteJson(stream, 200, view);
        }
        catch (Exception e) { await WriteJson(stream, 500, new { error = e.Message }); }
    }

    /// <summary>The objects of the area's walk-map capture, named by the last part of their object type, in file order
    /// (TeleportTargets.CapturedObjects: the same names /tp resolves against).</summary>
    static IEnumerable<(string Name, int X, int Y)> CapturedObjects(string area, uint seed, string folder = "data/walkability")
    {
        string file = Path.Combine(folder, $"{area}_seed{seed}.objects.json");
        if (!File.Exists(file)) return [];
        using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
        if (!doc.RootElement.TryGetProperty("objects", out var objects)) return [];
        var list = new List<(string, int, int)>();
        foreach (var o in objects.EnumerateArray())
            if (o.GetProperty("objectType").GetString() is { Length: > 0 } type)
                list.Add((type[(type.LastIndexOf('/') + 1)..], o.GetProperty("x").GetInt32(), o.GetProperty("y").GetInt32()));
        return list;
    }

    static async Task WriteJson(NetworkStream stream, int status, object payload) =>
        await GmWebMod.Write(stream, status, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(payload, Json));
}
