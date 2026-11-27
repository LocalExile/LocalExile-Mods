# GM Web Tool

Serves [gm-commands](../gm-commands/)' commands as a browser page at http://127.0.0.1:6111/: pick a command and it
runs in game, without typing it in the client's chat. One machine only — the page is served on 127.0.0.1, and a
request body that is not a slash command is refused. A request from another site (another `Host` or `Origin`) is
refused too, so a web page open in the browser cannot run commands in game.

- The tabs search everything the commands resolve, with a button per row: the areas `/goto` finds, the monsters
  `/spawn_mob` can create, and every base type and unique `/item` gives. `GET /names` builds those tables from the
  server's own catalogs, in English — nothing is read from the client.
- The In-area TP tab lists the current area's targets — monsters, NPCs, exits, the waypoint, the captured objects —
  the same candidates in-game `/tp list` shows, and one click teleports (`GET /tp`).
- Quick buttons for the everyday commands (heal, kill, ascendancy points, waypoints…), and a command bar that runs
  any slash command. The header shows who is in game (`GET /online`).

## How a picked command runs

The way typing it in the client's chat does: on the online player's connection, through the same command table the
chat path builds (`Program.Commands`), under the frames gate. Its reply goes to the in-game chat, so the result of
a picked command is seen in game. With nobody in game `/run` answers 503.

## The page

`gm.html` ships with the mod. A `gm.html` in the LocalExile folder takes its place, so a server can carry its own
page — another language, other buttons — without touching the mod.

## Settings

None.
