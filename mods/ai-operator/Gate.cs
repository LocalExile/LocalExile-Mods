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
        if (reached)
        {
            answer["waypoints"] = Waypoints(path, 12);
            return answer;
        }

        // A goal that cannot be stood on — a transition's own cell, a chest, a blocked doorway — is still useful
        // within reach of it: report the nearest walkable cell a walk can finish on, so the caller aims at that.
        // An empty path means the character already stands on that cell: there is no last corner to take, so there
        // is no stand cell to report either. Taking path[^1] of an empty path is an index of -1.
        if (StandNear(conn, layout, (px, py), x, y) is { } stand && stand.Path.Count > 0)
        {
            answer["reachable"] = true;
            answer["standAt"] = new JsonArray(stand.Path[^1].X, stand.Path[^1].Y);
            answer["standDist"] = Math.Round(CellDistance(stand.Path[^1], (x, y)), 1);
            answer["steps"] = Steps(stand.Path);
            answer["waypoints"] = Waypoints(stand.Path, 12);
            return answer;
        }

        // The search may have walked part of the way and stopped (a walled arena, a body on the only entrance). Those
        // corners are still a route as far as the grid and the live blockers allow, and they are what a caller needs to
        // get closer instead of being told "no": drop them and the operator has nothing to aim at.
        if (path.Count > 0)
        {
            answer["partial"] = true;
            answer["reachedTo"] = new JsonArray(path[^1].X, path[^1].Y);
            answer["steps"] = Steps(path);
            answer["waypoints"] = Waypoints(path, 12);
            answer["blockedBy"] = "only part of the way to it can be walked (the goal is walled off, or something " +
                "stands on it): follow these corners, then ask again";
            return answer;
        }

        answer["blockedBy"] = !goalWalkable
            ? "the goal cell is not walkable and no walkable cell around it can be reached " +
              "(a wall, water, or an object standing there)"
            : ClientPathfinder.ObjectBlocks(conn.Blockers, x, y, threshold: 2, radius: 1)
                ? "a monster or an object stands on the goal cell right now, and no cell around it can be reached"
                : "no route on the server's walk grid (a sealed corridor, or the grid disagrees with the client's own)";
        if ((ClosestToward(conn, layout, (px, py), x, y) ?? FarthestToward(conn, layout, (px, py), (x, y))) is { } part)
        {
            // The player's walk search is bounded by an ellipse around the character, so a goal a thousand cells
            // away is simply outside it. The useful answer there is not "no" but "this is how far one walk reaches
            // toward it": the operator follows those corners, re-reads the world, and asks again.
            answer["partial"] = true;
            answer["reachedTo"] = new JsonArray(part.Reached.X, part.Reached.Y);
            answer["steps"] = Steps(part.Path);
            answer["waypoints"] = Waypoints(part.Path, 12);
            answer["blockedBy"] = "the goal is beyond the client's own walk search (it is windowed around the " +
                "character): this reaches part of the way toward it — follow the corners, then ask again";
        }
        return answer;
    }

    /// <summary>
    /// The longest single walk toward a goal that the search can actually finish, as a path and the cell it reached.
    /// The hops are tried longest first so the answer is the furthest the client can go in one plan.
    /// </summary>
    /// <summary>
    /// The reachable cell that ends closest to a goal the direct route cannot reach, and the way to it.
    ///
    /// The straight-line hops of <see cref="FarthestToward"/> cannot go round a wall: every hop lands on a cell the
    /// grid calls unwalkable and the answer is "no". A boss in an arena is entered from one side, so the way in is a
    /// detour, and a detour is what this finds: a breadth-first search over the walkable cells around the character,
    /// keeping the one that ends closest to the goal.
    /// </summary>
    static (List<(int X, int Y)> Path, (int X, int Y) Reached)? ClosestToward(ClientState conn, AreaLayout layout,
        (int X, int Y) from, int x, int y)
    {
        const int window = 90;                      // the client's own walk search is windowed around the character
        // The answer has to be quick: the gateway serves the client's messages on one loop, and a search that takes
        // seconds is a client that times out and disconnects. The budget turns a slow search into a smaller one.
        long deadline = Environment.TickCount64 + 250;
        bool Walkable(int cx, int cy) =>
            cx >= 0 && cy >= 0 && cx < layout.GridW && cy < layout.GridH &&
            layout.WalkableFor(cx, cy, 1) && !ClientPathfinder.ObjectBlocks(conn.Blockers, cx, cy, 2, 5);

        var parent = new Dictionary<(int, int), (int, int)>();
        var length = new Dictionary<(int, int), int>();
        var queue = new Queue<(int X, int Y)>();
        parent[from] = from;
        length[from] = 0;
        queue.Enqueue(from);
        (int X, int Y)? best = null;
        int bestDistance = int.MaxValue, bestLength = int.MaxValue;
        while (queue.Count > 0)
        {
            var cell = queue.Dequeue();
            int cl = length[cell];
            int distance = Math.Max(Math.Abs(cell.X - x), Math.Abs(cell.Y - y));
            if (distance < bestDistance || (distance == bestDistance && cl < bestLength))
            {
                bestDistance = distance;
                bestLength = cl;
                best = cell;
            }
            if (cl >= window) continue;
            if ((cl & 63) == 0 && Environment.TickCount64 > deadline) break;
            var around = new[] { (cell.X + 1, cell.Y), (cell.X - 1, cell.Y), (cell.X, cell.Y + 1), (cell.X, cell.Y - 1) };
            foreach (var next in around)
            {
                if (parent.ContainsKey(next)) continue;
                if (!Walkable(next.Item1, next.Item2)) continue;
                parent[next] = cell;
                length[next] = cl + 1;
                queue.Enqueue(next);
            }
        }
        if (best is not { } cell2 || cell2 == from) return null;
        var path = new List<(int X, int Y)>();
        var walk = cell2;
        while (walk != from)
        {
            path.Add(walk);
            walk = parent[walk];
        }
        path.Reverse();
        return (path, cell2);
    }

    static (List<(int X, int Y)> Path, (int X, int Y) Reached)? FarthestToward(ClientState conn, AreaLayout layout,
        (int X, int Y) from, (int X, int Y) goal)
    {
        double dx = goal.X - from.X, dy = goal.Y - from.Y;
        double span = Math.Max(1.0, Math.Sqrt(dx * dx + dy * dy));
        foreach (int hop in new[] { 280, 220, 170, 130, 90, 50, 25 })
        {
            if (hop >= span) continue;      // the goal itself is nearer than this hop
            int tx = (int)Math.Round(from.X + dx / span * hop);
            int ty = (int)Math.Round(from.Y + dy / span * hop);
            if (tx < 0 || ty < 0 || tx >= layout.GridW || ty >= layout.GridH) continue;
            if (!layout.WalkableFor(tx, ty, 1)) continue;
            List<(int X, int Y)> path = ClientPathfinder.FindPath(from, (tx, ty),
                (cx, cy) => layout.WalkableFor(cx, cy, 1), layout.GridW, layout.GridH, conn.Blockers,
                moverSize: 1, minRange: 0, maxRange: 0, tolerance: -1,
                limit: ClientPathfinder.SearchLimit.Player, out bool reached);
            if (reached && path.Count > 1) return (path, (tx, ty));
        }
        return null;
    }

    /// <summary>
    /// The nearest cell a walk can finish on, near a goal that cannot be stood on: the rings of 1 to 3 cells around it,
    /// nearest to the goal first, each searched the way a click's MoveTo is.
    /// </summary>
    static (List<(int X, int Y)> Path, int Ring)? StandNear(ClientState conn, AreaLayout layout, (int X, int Y) from,
        int x, int y)
    {
        // A transition's own cell is blocked by the transition's own footprint (its size is 5), so "near" has to
        // reach further than three cells; each ring is searched nearest the goal first and only its eight nearest
        // cells are tried, which bounds the cost of the walk searches.
        for (int ring = 1; ring <= 8; ring++)
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
            foreach ((int X, int Y) candidate in candidates.Take(8))
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
    /// The corners of a path, thinned to at most <paramref name="most"/> points with the far end last. The routine
    /// walks these one at a time: a click only resolves within a screen radius, so aiming straight at a distant cell
    /// walks the character into whatever stands in the way, while the waypoints keep it on the route the server's
    /// walk grid knows.
    /// </summary>
    static JsonArray Waypoints(List<(int X, int Y)> path, int most)
    {
        var points = new JsonArray();
        if (path.Count == 0) return points;
        var kept = new List<(int X, int Y)>();
        int step = Math.Max(1, (int)Math.Ceiling(path.Count / (double)Math.Max(1, most)));
        for (int i = step; i < path.Count; i += step) kept.Add(path[i]);
        if (kept.Count == 0 || kept[^1] != path[^1]) kept.Add(path[^1]);
        foreach ((int X, int Y) cell in kept) points.Add(new JsonArray(cell.X, cell.Y));
        return points;
    }

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