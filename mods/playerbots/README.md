# Player bots

Server-driven characters that make a server look lived in: bots are created over time, play through the campaign, visit
the towns where players are, can join a player's party and fight beside them, and leave again. Path of Exile has no such
thing, so it is a mod.

It was LocalExile's Custom module `PlayerBots`, and is the same code; what it saves now lives in `data/mods/playerbots/`.

## Turning it on

It is a league's mod (`"leagues": "listed"`): it runs only in the leagues that list it, and no league lists it as it
comes. After installing it, tick **Player bots** in a league's list in Waypoint's Leagues section, or add it in
`data/league-modules.json` of your LocalExile:

```json
{ "Standard": { "add": ["playerbots"] } }
```

Then restart the servers: they read which leagues list a mod when they start.

A league that listed the old module (`Custom/PlayerBots` or `PlayerBots`) lists this mod: the id is the same in any case.
At the first start its settings move from `data/modules/PlayerBots.json` to `data/mods/playerbots.json`, and the bots'
state is copied from `Modules/Custom/PlayerBots/state/bots.json` to `data/mods/playerbots/bots.json`, so the bots go on
where they were.

## Commands

- `/botinvite <bot name>`: invite a bot you can see into your party (the party panel's invite box works too).
- `/botleave`: the bots following you leave and go back to their quests.

## Settings

| Setting | Default | |
|---|---|---|
| `maxBots` | 100 | bots at most, all towns together |
| `firstSpawnSeconds` | 60 | how long after the Gateway starts the first bot is created |
| `spawnIntervalSeconds` | 60 | how often a new bot is created, while there are fewer than the most |
| `league` | Standard | the league of the bots' characters |
| `questMinutes` | 2 to 6 | how long a quest takes, out of sight |
| `townActivities` | 2 to 4 | how many places a staying visit goes to in town |
| `idleSeconds` | 8 to 75 | how long a bot stands at each place in town |
| `deathChancePerQuest` | 0.15 | a death makes the bot retry the quest |
| `statusSeconds` | 10 | how often the status file and the state are rewritten |
| `mcpEnabled` | off | external AI control of bots in your party (below) |
| `mcpPort` | 6194 | its port, on 127.0.0.1 only |
| `mcpLeaseSeconds` | 30 | seconds before an unattended bot goes back to its own play |

## What a bot is

- **An account and a character** in the save database: account `<name>_bot`, a random Russian or English character name,
  a random class. Its progress (level, last town) is written to the character record.
- **A body in towns.** A bot walks the towns where players are, but it is not an entity of their areas, so no client
  draws it. Party followers instead are players the server drives (LocalExile's `Program.DriveByServer`) on the ordinary
  combat pipeline.
- **Gear**: real base items of the game data for its class and level (weapon by class, armour by the class's
  attributes, a belt). Party builds include connected passives and compatible linked gems.
- **The route**: `story-main.md` (the main story, 52 quests) and `story-side.md` (side quests, 30), act by act, main
  quests first; the order is LocalExile's campaign driver's (`scripts/dev/campaign/order.py`). A quest is simulated out
  of sight for `questMinutes`; the zone, kills, deaths and gear changes are logged.
- **Town visits**: after a quest, a quick visit (out of its own portal by an NPC, hands the quest in, leaves by portal)
  or a stay (by the stash and the guild stash, a few things to do). Towns keep two to five residents while `maxBots`
  allows; a bot that finds no room goes on to its next quest.
- **The end**: after every quest of both guides the character and its account are deleted, and a new bot follows.

A restart resumes every bot where it was. A bot account carries a mark in place of its password hash (`PlayerBots:`,
which no real hash holds); a marked account the state file does not know is deleted, and no other account ever is,
whatever its name. Keep `data/mods/playerbots/bots.json` with your saves: without it, the next start deletes the bots.

## Files

| Path | What |
|---|---|
| `data/mods/playerbots/logs/bots-<date>.log` | one line per event: `date time \| account \| character \| what` (errors too) |
| `data/mods/playerbots/logs/status.txt` | every bot, its level, act and what it is doing, rewritten every `statusSeconds` |
| `data/mods/playerbots/bots.json` | the bots' state for a restart |
| `data/modules/PlayerBots-mcp.key` | the MCP endpoint's key, made at its first start (as before the move) |

## External control (MCP)

Off by default. With `mcpEnabled` on, the mod listens on `http://127.0.0.1:<mcpPort>/` (loopback only) for an AI client
that observes and gives leased tactical orders to the bots in a human-led party. Every request needs the bearer key from
`data/modules/PlayerBots-mcp.key`; requests from a browser origin are refused; the server sends nothing out. How to
connect, the tools and the fallback rules: [MCP.md](MCP.md); `mcp.py` is the stdio bridge for an MCP client
(`mcp-client.example.json`).

Developer tools, not part of a release: `tools/collect_economy.py` fetches a poe.ninja snapshot into `economy/` (for
trading bots to come; nothing reads it yet), and `tools/trace-stance.js` is a Frida script for the client's animation
stance. `tests/` holds the bots' tests from LocalExile; they need LocalExile's test project and do not run here yet.

## Not done yet

Party combat uses the ordinary player pipeline; offscreen quests, loot and crafting remain simulated. Trading, chat and
advanced tactical or ascendancy builds remain open.

## LocalExile versions

Needs a LocalExile with league mods ([LocalExile #1588](https://github.com/LocalExile/LocalExile/pull/1588)): the first
release after 0.6.0-beta. While a LocalExile still has the Custom module, that module runs and this mod waits
("LocalExile has a module of that id").
