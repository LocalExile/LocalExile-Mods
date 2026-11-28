using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalExile.Mods.GmWeb;

/// <summary>
/// GET /names: the page's name tables, built once from the server's own catalogs — every area /goto resolves
/// (AreaTransitionCatalog's atlas), the monsters /spawn_mob can create (MonsterCatalog, one row per name), every base
/// type and unique /item can give (ItemCatalog and UniqueCatalog), every gem /gem resolves (ItemCatalog's gem names,
/// variant names included), and every jewel the jewel commands make (the unique, radius, timeless and cluster
/// catalogs). Nothing is read from the client.
///
/// Every row is five columns: [cn, hans, en, id, command]. The id is what the row's command takes, and the command is
/// the slash command that makes the thing — so a row carries what a button needs without the page knowing the catalog
/// it came from. The gem and jewel rows land in a search of their own, which is why their command is spelled out.
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
        var result = new JsonObject
        {
            ["area"] = AreaTable(),
            ["mob"] = MobTable(),
            ["item"] = ItemTable(),
            ["gem"] = GemTable(),
            ["jewel"] = JewelTable(),
        };
        return JsonSerializer.SerializeToUtf8Bytes(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    static string Str(JsonNode? n) => n!.GetValue<string>();

    /// <summary>The page's rows are [cn, hans, en, id, command]: every name slot carries the English name (the page's
    /// data is English; its optional Chinese mode translates the page's own labels, not the game data), and the command
    /// is left out for the tables whose button the page builds itself.</summary>
    static JsonArray Row(string name, string id) => new(name, name, name, id);
    static JsonArray Row(string name, string id, string command) => new(name, name, name, id, command);

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

    /// <summary>Every gem /gem takes, its alternate versions included (a name the gem catalog resolves, so a name the
    /// command resolves), sorted by name. The command is the bare "/gem &lt;name&gt;": the level and the quality are
    /// the page's own two boxes, not part of the row.</summary>
    static JsonArray GemTable()
    {
        var seen = new Dictionary<string, JsonArray>(StringComparer.Ordinal);
        foreach (string name in Items.GemNames())
        {
            // A gem row with no letter in its name is a placeholder ("..."), not something /gem resolves.
            if (string.IsNullOrWhiteSpace(name) || !name.Any(char.IsLetter) || seen.ContainsKey(name)) continue;
            seen[name] = Row(name, name, "/gem " + name);
        }
        return [.. seen.Values.OrderBy(r => Str(r[0]), StringComparer.Ordinal).Cast<JsonNode>()];
    }

    /// <summary>The jewels the jewel commands make, each with the command that makes it: the unique jewels
    /// (/unique_jewel), the radius jewels whose passives change (/radius_jewel), the Legion jewels (/timeless), and the
    /// three cluster sizes (/cluster_jewel). One row per name; a name two commands take keeps the first.</summary>
    static JsonArray JewelTable()
    {
        var seen = new Dictionary<string, JsonArray>(StringComparer.Ordinal);
        void Add(string? name, string command)
        {
            if (string.IsNullOrWhiteSpace(name) || seen.ContainsKey(name)) return;
            seen[name] = Row(name, name, command + " " + name);
        }

        foreach (string name in UniqueJewelCatalogue.Names) Add(name, "/unique_jewel");
        foreach (string name in RadiusJewelCatalogue.Names) Add(name, "/radius_jewel");
        foreach (string name in TimelessJewelCatalog.Names) Add(name, "/timeless");
        foreach (string size in new[] { "small", "medium", "large" }) Add(size + " cluster jewel", "/cluster_jewel");
        return [.. seen.Values.OrderBy(r => Str(r[0]), StringComparer.Ordinal).Cast<JsonNode>()];
    }
}
