# GM Web Tool

Serves [gm-commands](../gm-commands/)' commands as a browser page at http://127.0.0.1:6111/: pick a command and it
runs in game, without typing it in the client's chat. One machine only — the page is served on 127.0.0.1, and a
request body that is not a slash command is refused. A request from another site (another `Host` or `Origin`) is
refused too, so a web page open in the browser cannot run commands in game.

- The tabs search everything the commands resolve, with a button per row: the areas `/goto` finds, the monsters
  `/spawn_mob` can create, every base type and unique `/item` gives, every gem `/gem` takes with its level and quality
  boxes, and every jewel the jewel commands make — `/unique_jewel`, `/radius_jewel`, `/timeless`, and the three
  `/cluster_jewel` sizes, each row carrying the command that makes it. `GET /names` builds those tables from the
  server's own catalogs, in English — nothing is read from the client.
- The In-area TP tab lists the current area's targets — monsters, NPCs, exits, the waypoint, the captured objects —
  the same candidates in-game `/tp list` shows, and one click teleports (`GET /tp`).
- Quick buttons for the everyday commands (heal, kill, ascendancy points, waypoints…), and a command bar that runs
  any slash command. The header shows who is in game (`GET /online`).

## How a picked command runs

The way typing it in the client's chat does: on the online player's connection, through the same command table the
chat path builds (`Program.Commands`), under the frames gate. Its reply goes to the in-game chat, so the result of
a picked command is seen in game. With nobody in game `/run` answers 503.

## The fuse: a snapshot before a command

A command that goes wrong — a `/roll` on the wrong item, a `/set_level` on the wrong character — is only undoable if
the saves from before it still exist. So the first command of a window snapshots them first:

- `data/characters.db` and `data/auth.db` are copied into `data/gm-backups/<yyyyMMdd-HHmmss>/` through SQLite's own
  online backup API, so the copy is consistent while the server keeps writing (about 1.8 MB, a fraction of a second).
  A `manifest.json` beside them records the stamp and the source paths.
- One snapshot serves every command for `fuseMinutes` (10 by default), so a run of twenty commands writes one folder
  rather than twenty. The header shows which snapshot is in force and how long is left; clicking it takes one now.
- `keep` older snapshots are kept (12 by default), the rest are removed. Nothing else is ever deleted.
- A snapshot that fails does not stop the command: the failure is logged and the command runs, so a full disk cannot
  lock the panel. `fuse` off in the settings turns the whole thing off; `POST /fuse` snapshots on demand either way.

To go back to a snapshot, stop the server (the files are in use while it runs), copy `characters.db` and `auth.db` out
of `data/gm-backups/<stamp>/` back into `data/`, and delete the stale `characters.db-wal` / `characters.db-shm` beside
them. Snapshots are of the saves only — the client's own files are not touched.

## Endpoints

| Request | Answer |
|---|---|
| `GET /` or `/index.html` | the page (`gm.html`) |
| `GET /online` | the name of the character in game, or empty |
| `GET /names` | the search tables: `{area, mob, item, gem, jewel}`, five columns a row |
| `GET /tp` | the current area's teleport targets |
| `GET /ping` | `{ok, version, port, mod, game, fuse:{on, strict, stamp, age, window, bytes, where}}` — `age` and `window` in seconds |
| `POST /fuse` | takes a snapshot now: `{ok, stamp, bytes, seconds, where}` |
| `POST /run` | runs the body as a slash command: `{ok, ran, fuse, stamp, game}`, `fuse` one of `off`, `fresh`, `reused`, `failed` |

## Settings

`settings.json`, editable from Waypoint's Mods page or by hand in `data/modules/gm-web.json`:

| Setting | Default | What it does |
|---|---|---|
| `port` | `6111` | the loopback port the page is served on; set another one to run a second page beside this one |
| `fuse` | `true` | snapshot the saves before the first command of a window |
| `fuseMinutes` | `10` | how long one snapshot serves the commands after it; `0` snapshots before every command |
| `keep` | `12` | how many snapshots to keep, oldest removed beyond this |

## The page

`gm.html` ships with the mod. A `gm.html` in the LocalExile folder takes its place, so a server can carry its own
page — another language, other buttons — without touching the mod.
