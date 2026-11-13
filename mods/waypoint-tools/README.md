# Waypoint Tools

Lets [Waypoint](https://github.com/LocalExile/Waypoint)'s tools reach the game without the client control bridge, so they
work on a LocalExile release too. Waypoint's item crafter gives its items through it: no Frida, no Python, and no limit on
how long the item may be.

It adds no chat command and changes nothing in the game by itself. It does only what Waypoint asks, for a character on
this server, and says so in that character's chat (`Waypoint: Added Dire Abyss Iron Ring`).

## How Waypoint talks to it

Through files in the LocalExile folder (the extracted release, or a checkout); nothing listens on the network.

- `data/waypoint-tools/loaded.json`: written while the mod is loaded, removed when it unloads.
  `{"protocol": 1, "version": "1.0.0", "pid": <the Gateway's process id>, "started": "<when that process started, UTC>",
  "actions": ["item_give"]}`. A file whose `pid` is not a running Gateway was left by one that stopped.
  It names the Gateway that runs the game: the first to load the mod in the folder, for as long as it runs. A Gateway
  that is only asked a question (`GameServer.Gateway commands` or `values`, as Waypoint's Console does when it starts)
  loads the mods too; it leaves the file and the requests alone while the named one runs. When the named Gateway ended
  without unloading, another takes the file within a quarter of a second.
- `data/waypoint-tools/requests/<id>.json`: a request, one JSON object. Write it as `<id>.tmp` and rename it, so the mod
  never reads half of it. `<id>` is the caller's (a GUID).
  `{"action": "item_give", "character": "<name>", "item": <the item>}`. Without `character` (or with `""`) it goes to
  the one character in game; with several in game it is answered `Several characters are in game (…): name one`.
  `item` is an item in the format of LocalExile's
  [item crafter hook](https://github.com/LocalExile/LocalExile/blob/main/Agents/docs/dev/item-crafter.md), which the
  mod builds with the Gateway's own `ItemCrafter.Build`: one object, or an array whose items are given in order.
- `data/waypoint-tools/replies/<id>.json`: the answer, written before the request is removed.
  `{"protocol": 1, "id": "<id>", "ok": true, "message": "Added Dire Abyss Iron Ring"}`. The caller reads and deletes it.

A request is carried out on the next tick of that character's connection (about 4 a second while the character is in an
area and not paused), so it changes the inventory and sends to the client from the connection's own task, as a chat
command does. A character counts as in game while its connection ticked in the last 2 seconds. One that waits more than
30 seconds for its character is answered `<name> is not in game` (`No character is in game` when it names none); one that cannot be
read is answered with why. A request left when the mod unloads stays, and the next load reads it.

## Settings

None.

## Works with

1.0.3: LocalExile v0.4.3-beta, v0.5.0-beta and v0.5.1-beta; it fixes Give item answering that the mod is not loaded
after Waypoint was started while the game ran. 1.0.2: the same versions (the same code as 1.0.1); 1.0.0: v0.4.2-beta. It needs no other mod: it uses the server and LocalExile's Game module directly, as a module
does, and lists `Game` (that module, not a mod) in `depends` so it reloads with it. So it needs v0.4.0-beta or later
(the first where mods reach what modules reach).
