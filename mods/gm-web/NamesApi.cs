using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalExile.Mods.GmWeb;

/// <summary>
/// GET /names: the page's name tables, built once from the server's own catalogs — every area /goto resolves
/// (AreaTransitionCatalog's atlas), the monsters /spawn_mob can create (MonsterCatalog, one row per name), and
/// every base type and unique /item can give (ItemCatalog and UniqueCatalog). Nothing is read from the client.
/// </summary>
static class NamesApi
{
    static byte[]? _cache;

    internal static async Task Handle(NetworkStream stream, string method)
    {
        if (method != "GET") { await GmWebMod.Write(stream, 404, "text/plain", "not found"u8.ToArray()); return; }
        try
        {
            _cache ??= Build();
            await GmWebMod.Write(stream, 200, "application/json; charset=utf-8", _cache);
        }
        catch (Exception e)
        {
            await GmWebMod.Write(stream, 500, "application/json; charset=utf-8",
                JsonSerializer.SerializeToUtf8Bytes(new { error = e.Message }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }
    }

    static byte[] Build()
    {
        var result = new JsonObject { ["area"] = AreaTable(), ["mob"] = MobTable(), ["item"] = ItemTable() };
        return JsonSerializer.SerializeToUtf8Bytes(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    static string Str(JsonNode? n) => n!.GetValue<string>();

    /// <summary>The page's rows are [cn, hans, en, id]: every name slot carries the English name (the page's data is
    /// English; its optional Chinese mode translates the page's own labels, not the game data).</summary>
    static JsonArray Row(string name, string id) => new(name, name, name, id);

    /// <summary>Areas in campaign id order: exactly the ones /goto resolves, from the atlas the server loads.</summary>
    static JsonArray AreaTable()
    {
        var list = new List<(string Id, JsonArray Row)>();
        foreach (var (id, name) in AreaTransitionCatalog.Areas)
            list.Add((id, Row(string.IsNullOrWhiteSpace(name) ? id : name!, id)));
        list.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return [.. list.Select(e => (JsonNode)e.Row)];
    }

    /// <summary>One row per English name, keeping the shortest metadata id (the base variety), only the varieties
    /// the server can actually spawn — sorted by name.</summary>
    static JsonArray MobTable()
    {
        var seen = new Dictionary<string, JsonArray>();
        foreach (var v in Monsters.All)
        {
            if (!v.Supported || string.IsNullOrWhiteSpace(v.Name)) continue;
            if (!seen.TryGetValue(v.Name, out var cur) || v.Id.Length < Str(cur[3]).Length)
                seen[v.Name] = Row(v.Name, v.Id);
        }
        return [.. seen.Values.OrderBy(r => Str(r[2]), StringComparer.OrdinalIgnoreCase).Cast<JsonNode>()];
    }

    /// <summary>Base types and uniques together, sorted by name: /item resolves either by name, and the page's
    /// give button carries that name as the row's id.</summary>
    static JsonArray ItemTable()
    {
        // BaseItemTypes also holds unnamed rows (internal, hideout); a button with no name is no use.
        var seen = new Dictionary<string, JsonArray>();
        foreach (var t in Items.All)
            if (!string.IsNullOrWhiteSpace(t.Name))
                seen[t.Name] = Row(t.Name, t.Name);
        foreach (var u in Uniques.All)
            if (!string.IsNullOrWhiteSpace(u.Name))
                seen[u.Name] = Row(u.Name, u.Name);
        return [.. seen.Values.OrderBy(r => Str(r[0]), StringComparer.Ordinal).Cast<JsonNode>()];
    }
}
