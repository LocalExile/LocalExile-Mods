# AI Operator

Read-only chat commands that hand an outside AI a JSON picture of the world the character is in. This is the first
command of the operator bridge: the server side of "tell me what is around me" that a brain outside the game reads.

The command changes nothing. It does not walk, teleport, use a skill, pick anything up or touch the character's
state: the only thing that leaves the server is the chat reply.

## `/ai world [radius]`

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
  "exits": [ { "index": 0, "id": "1_1_1", "name": "The Twilight Strand", "dist": 12.0 } ]
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
  table does not know it), `dist` the distance to the transition in cells.
* Distances are in cells, to one decimal place.

### The client session is a bridge concept

`/ai world` reports the **server-side** identity — `character` and `account` — because the server does not know the
name a control bridge gives its client session. In v1 the bridge maps its own session name 1:1 onto the character,
so the brain joins the two; nothing here hard-codes a session string.

## Install

Copy this folder (or the release zip) into `Mods/Binaries/ai-operator/`, or install it with Waypoint. Then `/mod`
should list `ai-operator 0.1.0 - feature, 1 hook(s)`, and `/ai world` answers.

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
| `AiOperatorMod.cs` | the `IMod` entry point: registers `/ai` with `GameEvents.Command` |
| `AiCommands.cs` | `/ai` and its subcommands, and the usage line |
| `WorldModel.cs` | the read-only world model, built as `System.Text.Json.Nodes` |
| `ChatLines.cs` | the `AI ` / `AI+ ` splitter, 900 characters a line, surrogate-pair safe |

It lists `Game` in `depends`: it reads the game's own state (monsters, ground items, transitions) the way a module
does, and reloads whenever the Game module does. Client data (area names) comes from `client.db` through
`GameServer.Core.ClientTables`, never from a `data/tables/*.json` file.
