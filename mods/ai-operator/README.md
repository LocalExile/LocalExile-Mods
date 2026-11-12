# AI Operator

Read-only chat commands that hand an outside AI a JSON picture of the world the character is in, and answer the
questions it would otherwise guess at: can this cell be walked to, is that item in pickup range, and why can this
target not be used right now. This is the server side of the operator bridge that a brain outside the game reads.

The command changes nothing. It does not walk, teleport, use a skill, pick anything up or touch the character's
state: the only thing that leaves the server is the chat reply.

## `/aiop world [radius]`

Prints the server's world model for the connection the command arrived on, as one compact JSON object split over
chat lines: the first line starts with `AI `, the continuations with `AI+ `, and no line carries more than 900 JSON
characters (the prefixes are extra, the same budget `gm-commands` uses for `/state`). A line never ends between a
surrogate pair.

`radius` is in cells and defaults to 60. Monsters and ground items further away than the radius are left out;
monsters and ground items are listed nearest first. A list is cut at 60 monsters / 30 ground items / 30 exits, and
`truncated` is `true` when any of them was cut.

```json
{
  "v": 1,
  "character": "SpikeWitch",
  "account": "spike",
  "area": { "id": "1_1_town", "hash": 9052, "town": true },
  "seed": 1234567,
  "pos": [197, 261],
  "life": [120, 120],
  "monsters": [
    { "id": 2883585, "name": "Hillock", "dist": 42.3, "life": 250, "maxLife": 250,
      "hostile": true, "dead": false, "asleep": false, "invulnerable": false }
  ],
  "ground": [ { "id": 1048577, "label": "Scroll of Wisdom", "dist": 3.2 } ],
  "exits": [ { "index": 0, "id": "1_1_1", "name": "The Twilight Strand", "pos": [388, 231], "dist": 12.0 } ]
}
```

* `area.id` and `seed` come from the player; `area.hash` is the live layout's `HASH16`; `area.town` is the client's
  `WorldAreas` row (`IsTown`) for that hash. `pos` is the server's estimate of where the character stands, in cells.
* `life` is `[current, maximum]`.
* A monster's `name` is its variety's name, falling back to its label. `life`/`maxLife`, `hostile`, `dead`,
  `asleep` (sent out of the wake zone) and `invulnerable` are what the server knows about it right now. There is no
  `retired` field: a monster's retirement is the connection's, not the monster's.
* `ground[].label` is the item's base type name, or its components when the catalog does not know the type.
* `exits[].index` is the transition's index, `id`/`name` the destination area (the raw `0x…` hash when the client
  table does not know it), `pos` the cell the transition stands on (the bridge and the gate aim at cells, so an exit
  is useless to them without one), `dist` the distance to the transition in cells.
* Distances are in cells, to one decimal place.

### The client session is a bridge concept

`/aiop world` reports the **server-side** identity — `character` and `account` — because the server does not know the
name a control bridge gives its client session. In v1 the bridge maps its own session name 1:1 onto the character,
so the brain joins the two; nothing here hard-codes a session string.

## `/aiop reach x y`

Whether the character can walk to a cell, from the server's own layout (the same walkability a generated area is
built from, plus the live blockers a walk has to go round) and the same search the client's `MoveTo` runs.

```json
{ "v": 1, "area": "1_1_town", "from": [197, 261], "to": [300, 250], "dist": 104,
  "steps": 105, "reachable": true,
  "basis": "the server's walk grid for this area, plus live blockers",
  "goalWalkable": true, "exact": true }
```

`reachable` is the search's own verdict and `steps` the length of the path it found, counted the way a walk
counts moves (the search answers with waypoints, not one cell per step; the cell underfoot is not a step).

`exact` is `true` when the walk can end on the cell itself. When it cannot — a transition's own cell, a chest, a
dead corner — and a cell near it can be reached, the answer is still `reachable: true` but carries `standAt` (the
cell to aim at) and `standDist` (its distance to the goal), so the caller aims somewhere a character can actually
stand instead of giving up:

```json
{ "v": 1, "reachable": true, "standAt": [329, 250], "standDist": 1.4, "steps": 133, "exact": false, … }
```

When the answer is `false`, `blockedBy` says which of four things went wrong, because "unreachable" alone does not
say what to do next: the cell is outside the area's grid, the goal cell is not walkable and nothing around it can
be reached (a wall, water or the edge of the map), a body stands on the goal cell right now, or the server's walk
grid finds no route at all. `basis` is part of every answer on purpose: this is the **server's** walk grid, and the
client keeps its own and is authoritative for its own walking, so a `false` means the server sees no route — not
that the character is certain to be unable to get there.

## `/aiop item id`

Whether a ground item (`id` as `/aiop world`, `/state` or the bridge's `loot` answer prints it) can be picked up
from where the character stands, with the server's own numbers: `pickupRange` is the pickup's reach (20 cells) plus
the base type's size on the ground, the way the pickup packet is judged.

```json
{ "v": 1, "id": 1048576, "kind": "item", "label": "Driftwood Wand", "onGround": true,
  "pos": [351, 1327], "dist": 6.0, "pickupRange": 21, "ok": true,
  "why": "in pickup range (6.0 of 21)" }
```

`onGround` is `false` when nothing with that id lies on the ground here any more, and then `why` says so.

## `/aiop why id`

One sentence on why a target cannot be used right now, for a monster (`useSkill`) or a ground item (`loot`). A
monster answer also carries the fields that decided it (`life`, `maxLife`, `hostile`, `dead`, `asleep`,
`invulnerable`, `dist`, `canAttackNow`); the reach it reports is the weapon's range widened the way a swing is
(two cells, plus the monster's body). The line test a ranged order needs is **not** run here, and the answer does
not pretend it was.

```json
{ "v": 1, "id": 134217729, "kind": "monster", "name": "Skulking Wretch", "pos": [191, 256],
  "dist": 7.8, "life": 23, "maxLife": 23, "hostile": true, "dead": false, "asleep": false,
  "invulnerable": false, "canAttackNow": true, "weaponRange": 120, "monsterBody": 2, "reach": 124,
  "why": "in reach (7.8 of 124): attack now" }
```

`reach` is shown with the two numbers it was built from (`weaponRange`, the equipped weapon's own range — a wand
or a bow reaches 120 cells, a sword about 11 — and `monsterBody`, the monster's body widening the range), so a
caller can check the arithmetic instead of trusting it.

An unknown id falls through to the item answer, so a caller with one id and no idea what it is gets a sentence
either way.

## Install

Copy this folder (or the release zip) into `Mods/Binaries/ai-operator/`, or install it with Waypoint. Then `/mod`
should list `ai-operator 0.2.0 - feature, 1 hook(s)`, and `/aiop world` answers.

## Build

A project mod: it builds against a LocalExile checkout with

```sh
dotnet build mods/ai-operator/ai-operator.csproj -p:LocalExileRoot="path/to/LocalExile"
```

which leaves `ai-operator.dll`, `ai-operator.pdb` and `mod.json` in that checkout's `Mods/Binaries/ai-operator/`.
Delete any `GameServer.*.dll` a plain `dotnet build` drops there as well: the server's assemblies are referenced,
never shipped (a copy in the mod's folder would load as a type of its own). `tools/build-release.ps1 ai-operator`
makes the zip and does that for you.

## Layout

| File | What it is |
|---|---|
| `AiOperatorMod.cs` | the `IMod` entry point: registers `/aiop` with `GameEvents.Command` (the name is unique: gm-commands owns `/ai`) |
| `AiCommands.cs` | `/aiop` and its subcommands, and the usage line |
| `WorldModel.cs` | the read-only world model, built as `System.Text.Json.Nodes` |
| `Gate.cs` | `/aiop reach`, `/aiop item`, `/aiop why`: the layout and path search, the pickup range, the one-sentence reasons |
| `ChatLines.cs` | the `AI ` / `AI+ ` splitter, 900 characters a line, surrogate-pair safe |

It lists `Game` in `depends`: it reads the game's own state (monsters, ground items, transitions) the way a module
does, and reloads whenever the Game module does. Client data (area names) comes from `client.db` through
`GameServer.Core.ClientTables`, never from a `data/tables/*.json` file.
