using System.Text.Json.Nodes;
using GameServer.Core.World;

namespace LocalExile.Mods.AiOperator;

/// <summary>
/// The gate behind <c>/aiop reach</c>, <c>/aiop item</c> and <c>/aiop why</c>: it answers the questions an outside
/// operator would otherwise guess at, from what the server knows rather than from what the client claims. Every
/// answer is derived here; nothing is stored, and nothing the player can see changes (<c>README.md</c>).
/// </summary>
internal static class Gate
{
    /// <summary>The shape version of these answers, bumped with <see cref="WorldModel.Version"/>.</summary>
    public const int Version = 1;

    /// <summary>
    /// Whether the character can walk to a cell, and how far it is: the server's own layout (<see cref="AreaLayouts"/>),
    /// the live blockers a walk has to go round, and the same search the client's MoveTo uses
    /// (<see cref="ClientPathfinder.FindPath"/>). A cell outside the grid, a wall and a body standing on the cell are
    /// told apart, because "unreachable" alone does not say what to do next.
    /// </summary>
    public static JsonObject Reach(ClientState conn, int x, int y)
    {
        IPlayer player = Program.PlayerOf(conn);
        (int px, int py) = player.Position;
        AreaLayout layout = AreaLayouts.For(conn.Layout.Hash, conn.Seed);
        int distance = (int)Math.Round(Math.Sqrt((double)(px - x) * (px - x) + (double)(py - y) * (py - y)));
        var answer = new JsonObject
        {
            ["v"] = Version,
            ["area"] = player.AreaId,
            ["from"] = new JsonArray(px, py),
            ["to"] = new JsonArray(x, y),
            ["dist"] = distance,
            ["steps"] = 0,
            ["reachable"] = false,
            // What the answer is derived from: the server's own walk grid for the area, plus the bodies standing on
            // it. The client keeps its own grid and is authoritative for its own walking, so a "no" here means the
            // server's walk grid sees no route, not that the character is certain to be unable to get there.
            ["basis"] = "the server's walk grid for this area, plus live blockers",
        };
        if (x < 0 || y < 0 || x >= layout.GridW || y >= layout.GridH)
        {
            answer["blockedBy"] = $"({x}, {y}) is outside the area's grid ({layout.GridW}x{layout.GridH})";
            return answer;
        }

        bool goalWalkable = layout.WalkableFor(x, y, 1);
        answer["goalWalkable"] = goalWalkable;
        List<(int X, int Y)> path = ClientPathfinder.FindPath((px, py), (x, y),
            (cx, cy) => layout.WalkableFor(cx, cy, 1), layout.GridW, layout.GridH, conn.Blockers,
            moverSize: 1, minRange: 0, maxRange: 0, tolerance: -1,
            limit: ClientPathfinder.SearchLimit.Player, out bool reached);
        answer["reachable"] = reached;
        answer["exact"] = reached;
        // The search answers with waypoints, not one cell per step, so the length is measured corner to corner.
        answer["steps"] = Steps(path);
        if (reached) return answer;

        // A goal that cannot be stood on — a transition's own cell, a chest, a blocked doorway — is still useful
        // within reach of it: report the nearest walkable cell a walk can finish on, so the caller aims at that.
        if (StandNear(conn, layout, (px, py), x, y) is { } stand)
        {
            answer["reachable"] = true;
            answer["standAt"] = new JsonArray(stand.Path[^1].X, stand.Path[^1].Y);
            answer["standDist"] = Math.Round(CellDistance(stand.Path[^1], (x, y)), 1);
            answer["steps"] = Steps(stand.Path);
            return answer;
        }

        answer["blockedBy"] = !goalWalkable
            ? "the goal cell is not walkable and no walkable cell around it can be reached " +
              "(a wall, water, or an object standing there)"
            : ClientPathfinder.ObjectBlocks(conn.Blockers, x, y, threshold: 2, radius: 1)
                ? "a monster or an object stands on the goal cell right now, and no cell around it can be reached"
                : "no route on the server's walk grid (a sealed corridor, or the grid disagrees with the client's own)";
        return answer;
    }

    /// <summary>
    /// The nearest cell a walk can finish on, near a goal that cannot be stood on: the rings of 1 to 3 cells around it,
    /// nearest to the goal first, each searched the way a click's MoveTo is.
    /// </summary>
    static (List<(int X, int Y)> Path, int Ring)? StandNear(ClientState conn, AreaLayout layout, (int X, int Y) from,
        int x, int y)
    {
        for (int ring = 1; ring <= 3; ring++)
        {
            var candidates = new List<(int X, int Y)>();
            for (int dy = -ring; dy <= ring; dy++)
            {
                for (int dx = -ring; dx <= ring; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != ring) continue;
                    int cx = x + dx, cy = y + dy;
                    if (cx < 0 || cy < 0 || cx >= layout.GridW || cy >= layout.GridH) continue;
                    if (!layout.WalkableFor(cx, cy, 1)) continue;
                    candidates.Add((cx, cy));
                }
            }
            candidates.Sort((a, b) => CellDistance(a, (x, y)).CompareTo(CellDistance(b, (x, y))));
            foreach ((int X, int Y) candidate in candidates)
            {
                List<(int X, int Y)> path = ClientPathfinder.FindPath(from, candidate,
                    (cx, cy) => layout.WalkableFor(cx, cy, 1), layout.GridW, layout.GridH, conn.Blockers,
                    moverSize: 1, minRange: 0, maxRange: 0, tolerance: -1,
                    limit: ClientPathfinder.SearchLimit.Player, out bool reached);
                if (reached) return (path, ring);
            }
        }
        return null;
    }

    /// <summary>The length of a path the way a walk counts it: the sum of the moves between its waypoints.</summary>
    static int Steps(List<(int X, int Y)> path)
    {
        int steps = 0;
        for (int i = 1; i < path.Count; i++)
            steps += Math.Max(Math.Abs(path[i].X - path[i - 1].X), Math.Abs(path[i].Y - path[i - 1].Y));
        return steps;
    }

    static double CellDistance((int X, int Y) a, (int X, int Y) b) =>
        Math.Sqrt((double)(a.X - b.X) * (a.X - b.X) + (double)(a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>
    /// Whether a ground item can be picked up from where the character stands, with the server's own numbers
    /// (<c>Settings.PickupReach</c> plus the base type's size on the ground).
    /// </summary>
    public static JsonObject Item(ClientState conn, uint id)
    {
        IPlayer player = Program.PlayerOf(conn);
        (int px, int py) = player.Position;
        if (conn.Instance?.Ground.Get(id) is not { } item)
        {
            return new JsonObject
            {
                ["v"] = Version,
                ["id"] = id,
                ["kind"] = "item",
                ["onGround"] = false,
                ["ok"] = false,
                ["why"] = "no ground item with that id here: it was picked up, or it lies in another area",
            };
        }

        double distance = Math.Sqrt((double)(px - item.GridX) * (px - item.GridX)
            + (double)(py - item.GridY) * (py - item.GridY));
        int tolerance = Game.PickupToleranceOf(conn, item.Item);
        double pickupRange = Game.PickupReach + tolerance;
        bool ok = distance <= pickupRange;
        string? label = Program.Items.ByHash(item.Item.TypeHash)?.Name;
        return new JsonObject
        {
            ["v"] = Version,
            ["id"] = id,
            ["kind"] = "item",
            ["label"] = label ?? string.Join('/', item.Item.Components),
            ["onGround"] = true,
            ["pos"] = new JsonArray(item.GridX, item.GridY),
            ["dist"] = Math.Round(distance, 1),
            ["pickupRange"] = pickupRange,
            ["ok"] = ok,
            ["why"] = ok
                ? $"in pickup range ({distance:0.0} of {pickupRange:0})"
                : $"walk {pickupRange - distance:0.0} cells closer: a pickup reaches {pickupRange:0} cells here",
        };
    }

    /// <summary>
    /// One sentence on why a target cannot be used right now, for a monster (<c>useSkill</c>) or a ground item
    /// (<c>loot</c>). The line test a ranged order needs is not run here, and the answer says so instead of guessing.
    /// </summary>
    public static JsonObject Why(ClientState conn, uint id)
    {
        IPlayer player = Program.PlayerOf(conn);
        (int px, int py) = player.Position;
        double now = conn.Clock.Elapsed.TotalSeconds;
        SpawnedMonster? monster = null;
        lock (conn.Monsters) conn.Monsters.TryGetValue(id, out monster);
        if (monster is null) return Item(conn, id);   // items and monsters share this command

        (int mx, int my) = monster.Walk.Position(now);
        double distance = Math.Sqrt((double)(px - mx) * (px - mx) + (double)(py - my) * (py - my));
        if (monster.Dead)
            return Monster(id, monster, (mx, my), distance, false, "already dead: there is nothing left to attack");
        if (!monster.Hostile)
        {
            return Monster(id, monster, (mx, my), distance, false,
                "not hostile: an attack on it does nothing (it is a neutral body)");
        }
        if (monster.Invulnerable)
        {
            return Monster(id, monster, (mx, my), distance, false,
                "invulnerable right now: a hit would not land");
        }
        if (monster.Asleep)
        {
            return Monster(id, monster, (mx, my), distance, false,
                "asleep (dormant): wake it first, or attack something else");
        }

        int weaponRange = Game.WeaponFor(conn).Range;
        // What the reach was built from, so the caller can check the number instead of trusting it.
        int body = Math.Max(0, monster.StopRange - 2);
        // The swing tests the whole range (+ 2) widened by the monster's body (StopRange - 2); a ranged order also needs
        // the line on the projectile grid, which this answer does not test.
        double reach = weaponRange + 2 + body;
        JsonObject answer = distance > reach
            ? Monster(id, monster, (mx, my), distance, false,
                $"{distance:0.0} cells away and the attack reaches {reach:0}: walk closer first " +
                "(/aiop reach says whether a path exists)")
            : Monster(id, monster, (mx, my), distance, true, $"in reach ({distance:0.0} of {reach:0}): attack now");
        answer["weaponRange"] = weaponRange;
        answer["monsterBody"] = body;
        answer["reach"] = reach;
        return answer;
    }

    static JsonObject Monster(uint id, SpawnedMonster m, (int X, int Y) at, double distance, bool canAttack,
        string why) => new()
    {
        ["v"] = Version,
        ["id"] = id,
        ["kind"] = "monster",
        ["name"] = m.Variety?.Name ?? m.Label,
        ["pos"] = new JsonArray(at.X, at.Y),
        ["dist"] = Math.Round(distance, 1),
        ["life"] = m.Life,
        ["maxLife"] = m.MaxLife,
        ["hostile"] = m.Hostile,
        ["dead"] = m.Dead,
        ["asleep"] = m.Asleep,
        ["invulnerable"] = m.Invulnerable,
        ["canAttackNow"] = canAttack,
        ["why"] = why,
    };
}