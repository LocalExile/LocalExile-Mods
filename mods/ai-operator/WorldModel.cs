using System.Text.Json.Nodes;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;

namespace LocalExile.Mods.AiOperator;

/// <summary>
/// The read-only world model <c>/ai world</c> prints: what the server already knows about the area the character is
/// in. Nothing here writes anything — it reads the connection, the instance's monster roster (under its lock) and
/// ground items, the area's transitions, and the client's <c>WorldAreas</c> table for the area names.
/// </summary>
internal static class WorldModel
{
    /// <summary>The shape version of the JSON: bumped when a field is added or its meaning changes.</summary>
    public const int Version = 1;

    /// <summary>How far around the character a monster or ground item is listed, in cells, when no radius is given.</summary>
    public const double DefaultRadius = 60;

    /// <summary>The largest radius a caller may ask for, in cells.</summary>
    public const double MaxRadius = 1000;

    // Caps that keep the reply inside the chat lines. /ai world reports "truncated": true when one of them cuts a list.
    public const int MonsterCap = 60;
    public const int GroundCap = 30;
    public const int ExitCap = 30;

    public static JsonObject Build(ClientState conn, double radius)
    {
        IPlayer player = Program.PlayerOf(conn);
        (int px, int py) = player.Position;
        double now = conn.Clock.Elapsed.TotalSeconds;
        Dictionary<ushort, AreaName> areas = Areas();

        var monsters = new JsonArray();
        var monsterRows = new List<(double Distance, JsonObject Row)>();
        lock (conn.Monsters)
        {
            foreach ((uint id, SpawnedMonster monster) in conn.Monsters)
            {
                (int x, int y) at = monster.Walk.Position(now);
                double distance = Distance(px, py, at.x, at.y);
                if (distance > radius) continue;
                monsterRows.Add((distance, new JsonObject
                {
                    ["id"] = id,
                    ["name"] = monster.Variety?.Name ?? monster.Label,
                    ["dist"] = Cells(distance),
                    ["life"] = monster.Life,
                    ["maxLife"] = monster.MaxLife,
                    ["hostile"] = monster.Hostile,
                    ["dead"] = monster.Dead,
                    ["asleep"] = monster.Asleep,
                    ["invulnerable"] = monster.Invulnerable,
                }));
            }
        }
        monsterRows.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));

        var ground = new JsonArray();
        var groundRows = new List<(double Distance, JsonObject Row)>();
        foreach ((uint id, WorldItemObject item) in conn.Instance?.Ground.All() ?? [])
        {
            double distance = Distance(px, py, item.GridX, item.GridY);
            if (distance > radius) continue;
            ItemCatalog.BaseType? type = Program.Items.ByHash(item.Item.TypeHash);
            groundRows.Add((distance, new JsonObject
            {
                ["id"] = id,
                ["label"] = type?.Name ?? string.Join('/', item.Item.Components),
                ["dist"] = Cells(distance),
            }));
        }
        groundRows.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));

        var exits = new JsonArray();
        var exitRows = new List<(double Distance, JsonObject Row)>();
        foreach ((uint _, AreaTransitionObject transition) in conn.Layout.AreaTransitions)
        {
            double distance = Distance(px, py, transition.GridX, transition.GridY);
            bool known = areas.TryGetValue(transition.DestinationHash16, out AreaName to);
            string? name = known ? to.Name : null;
            exitRows.Add((distance, new JsonObject
            {
                ["index"] = transition.Index,
                ["id"] = known ? to.Id : $"0x{transition.DestinationHash16:x4}",
                ["name"] = name,
                // The gate and the bridge aim at a cell, so an exit is useless to them without one.
                ["pos"] = new JsonArray(transition.GridX, transition.GridY),
                ["dist"] = Cells(distance),
            }));
        }
        exitRows.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));

        bool truncated = monsterRows.Count > MonsterCap | groundRows.Count > GroundCap | exitRows.Count > ExitCap;
        foreach ((double _, JsonObject row) in monsterRows.Take(MonsterCap)) monsters.Add(row);
        foreach ((double _, JsonObject row) in groundRows.Take(GroundCap)) ground.Add(row);
        foreach ((double _, JsonObject row) in exitRows.Take(ExitCap)) exits.Add(row);

        bool town = areas.TryGetValue(conn.Layout.Hash, out AreaName here) && here.Town;
        return new JsonObject
        {
            ["v"] = Version,
            ["character"] = player.Character,
            ["account"] = player.Account,
            ["area"] = new JsonObject
            {
                ["id"] = player.AreaId,
                ["hash"] = conn.Layout.Hash,
                ["town"] = town,
            },
            ["seed"] = player.Seed,
            ["pos"] = new JsonArray(px, py),
            ["life"] = new JsonArray(conn.PlayerLife, conn.PlayerMaxLife),
            ["monsters"] = monsters,
            ["ground"] = ground,
            ["exits"] = exits,
            ["truncated"] = truncated,
        };
    }

    readonly record struct AreaName(string Id, string Name, bool Town);

    /// <summary>
    /// The client's <c>WorldAreas</c> table by HASH16, read the way a mod reads a table on this base: out of
    /// <c>client.db</c> through <see cref="ClientTables"/>, not from a <c>data/tables/*.json</c> file. Empty when the
    /// database does not have the table, so the command still answers with the ids it knows.
    /// </summary>
    static Dictionary<ushort, AreaName> Areas()
    {
        var areas = new Dictionary<ushort, AreaName>();
        if (ClientTables.TableOrNull("WorldAreas") is not { } table) return areas;
        foreach (ClientRow row in table)
        {
            if (row.IntOrNull("HASH16") is not int hash || hash is <= 0 or > ushort.MaxValue) continue;
            areas.TryAdd((ushort)hash,
                new AreaName(row.TextOrNull("Id") ?? "", row.TextOrNull("Name") ?? "", row.BoolOrNull("IsTown") == true));
        }
        return areas;
    }

    static double Distance(int ax, int ay, int bx, int by)
    {
        double dx = ax - bx, dy = ay - by;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>A distance in cells, to one decimal place, so a whole number of cells prints without a decimal point.</summary>
    static double Cells(double distance) => Math.Round(distance, 1);
}
