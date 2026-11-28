# GM Web Plus

A LocalExile mod that serves the GM command panel at **http://127.0.0.1:6111/** — and snaps your saves before it runs
anything.

It is a **script mod**: one `.cs` file the Gateway compiles itself, no .NET SDK, no build step, no LocalExile source
tree. Copy the folder in and restart; that is the whole install. It repeats the ground gm-web covers and adds the
three things this toolbox had outside the game: the fuse, the bilingual panel, and `/ping` state for it.

## Why it exists

[`gm-web`](https://github.com/LocalExile/LocalExile-Mods/tree/main/mods/gm-web) already serves a page on `:6111`. Its
page is good at searching the game's own catalogs. It has no snapshot step, and its `gm.html` is a compiled-in
asset.

This mod does the same thing from a script mod, which buys three things gm-web cannot do from where it sits:

| | gm-web | gm-web-plus |
|---|---|---|
| Runs a command | `Program.Commands(player)` under the frames gate | same |
| Snapshot before running | — | **SQLite backup, once per 10 minutes** |
| `/fuse` to snapshot on demand | — | **yes** |
| Panel | compiled into `gm.html` | **`panel.html` beside `mod.json`, your own file** |
| Language | 57 i18n keys, mostly hard-coded Chinese | **144 cards, zh/en switch** |

Nothing is simulated: no clipboard, no keystrokes, no window focus. The command runs on the online player's
connection, exactly as typing it in the client's chat does, and the reply lands in the in-game chat.

## Install

Copy the whole folder to `Mods/Binaries/gm-web-plus/`:

```
D:\LocalExile\Mods\Binaries\gm-web-plus\
  mod.json
  GmWebPlusMod.cs
  panel.html          ← the page it serves
  lx-backup.py        ← the fuse, called through the shipped Python
```

Restart LocalExile, or wait for the hot reload (~0.4 s) if it is already running. The log says:

```
[mods] gm-web-plus: 73 methods compiled in 4 ms
[mods] loaded gm-web-plus 1.0.0 (feature, 0 hook(s))
[mods] gm-web-plus: the GM page is at http://127.0.0.1:6111/  (fuse: snapshot before the first command, reused 10 min)
```

**Turn `gm-web` off first** — both bind `:6111`. Rename its `mod.json` to `mod.json.disabled`, or set
`gm-web off` in `Mods/Binaries/mod-state.txt`.

## Endpoints

| Method | Path | What it does |
|---|---|---|
| `GET` | `/` | the panel (`panel.html` beside `mod.json`, or `gm.html` in the game folder to override it) |
| `GET` | `/ping` | `{ok, version, port, mod, game, fuse:{on,stamp,age,window,bytes,where}}` |
| `GET` | `/fuse` | snapshot the saves now, without running anything |
| `GET` | `/send?cmd=/pos` | run one command |
| `POST` | `/run` | same, command in the body |

Loopback only. A request whose `Host` is not `127.0.0.1:6111` is a DNS-rebinding page and gets 403; so does a `POST`
carrying a foreign `Origin`. With nobody in game, `/send` answers 503.

## The fuse

`characters.db` runs in **WAL mode**. While the server is up, `characters.db-wal` holds the last few minutes of your
progress — it was 4.7 MB when this was written. `Copy-Item characters.db` gives you a database that opens fine and is
quietly missing all of it.

So the snapshot goes through SQLite's own backup API, in `lx-backup.py`, run with the Python LocalExile already ships
(`Tools\python\python.exe`). One snapshot is reused for **10 minutes**, so a queue of twenty commands writes one file,
not twenty.

```
D:\LocalExile\data\gm-backups\20261128-134216\
  characters.db     1,789,952   (32 KB larger than the live file: the WAL is merged in)
  auth.db              24,576
  manifest.json          463   (source_bytes, bytes, integrity:"ok")
```

A failed snapshot **does not block the command** — it comes back as `"fuse":"failed"` with the reason in the log. The
panel shows which state it is in.

Restore is refused while the servers run (exit 3), because a Gateway shutdown writes its in-memory state back over the
restore. Close LocalExile first:

```powershell
cd D:\LocalExile\Mods\Binaries\gm-web-plus
Tools\python\python.exe lx-backup.py --db D:\LocalExile\data --list
Tools\python\python.exe lx-backup.py --db D:\LocalExile\data --restore 20261128-134216
```

## The panel

144 cards, one per command `GameServer.Gateway.exe commands` reports, in 12 groups, with a zh/en switch in the top
right. Save snapshots, queue commands, copy them, send them.

It talks to this mod over `:6111`, so **there is no separate bridge process and no `.cmd` to keep open**. Open
`http://127.0.0.1:6111/` and the panel is there.

The panel is served from `panel.html` beside `mod.json`, so it can be replaced without touching the mod — the same
arrangement gm-web has with `gm.html` in the LocalExile folder. To use **another** panel, put it there;
[gm-web](../gm-web/)'s own page is not one of them, because it builds its tables from `/names` and `/tp`, which this
mod does not serve.

## Settings

| Setting | Default | Does |
|---|---|---|
| `port` | 6111 | The loopback port the page is served on. Set another one to run beside `gm-web`, which serves 6111 by default, and open `http://127.0.0.1:<port>/`. |
| `fuse` | true | Snapshot the saves before the first command is run. `/fuse` snapshots on demand either way. |
| `fuseMinutes` | 10 | How long one snapshot serves the commands that follow it, so a queue of twenty writes one file. `0` snapshots before every command. |
| `keep` | 12 | Snapshots kept; older ones are removed. Each is about 1.8 MB. |

Settings are read when the mod loads: a change needs `/mod_reload gm-web-plus`, and a changed `port` needs the servers
restarted.

## Compatibility

Built and run against LocalExile `0.6.1-beta` (`60fb08af`). It uses `Program.Commands`, `Program.InGame`,
`player.Frames.Run` and `IModContext.Folder`, which is what gm-web 1.0.0 uses too, so it should survive the same
changes that mod survives.
