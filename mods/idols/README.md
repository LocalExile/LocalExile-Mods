# Idols

Legacy of Phrecia's Idols (3.25): items with map modifiers that affect every map you open while they lie in the Idol
inventory, and are not used up. Monsters in maps drop them, the 41 unique ones too, and Faustus gambles them. The 3.29.3
clients have the Idols' data but no Idol panel, so the Idol inventory is the map device's storage grid in the Atlas
screen's side panel (4 by 5 cells, one loadout).

It was LocalExile's Legacy module `Idols`, and is the same code. Path of Exile 3.29 has no Idols, so it lives here now.
What it does in detail, and what the client shows: LocalExile's
[Agents/docs/game/idols.md](https://github.com/LocalExile/LocalExile/blob/main/Agents/docs/game/idols.md).

## Turning it on

It is a league's mod (`"leagues": "listed"`): it runs only in the leagues that list it. The Phrecia league of the mod
[phreciaascendancies](../phreciaascendancies/) lists it; install both for Phrecia. For another league, tick **Idols** in
its list in Waypoint's Leagues section, or add it in `data/league-modules.json` of your LocalExile:

```json
{ "Standard": { "add": ["idols"] } }
```

Then restart the servers: they read which leagues list a mod when they start.

A league that listed the old module (`Legacy/Idols` or `Idols`) lists this mod: the id is the same in any case. The
settings below move with it, from `data/modules/Idols.json` to `data/mods/idols.json`, at the first start.

| Setting | Default | |
|---|---|---|
| `enableAtlasTree` | off | off, as in Legacy of Phrecia: Idols replace the Atlas passive tree, which the client hides; on, the tree stays usable beside them |
| `dropRate` | 1 | how many times as often monsters in maps drop Idols; 0 stops them |

## The unique Idols

`sql/updates/world/` adds the 41 unique Idols to the world database, marked so they never drop as ordinary uniques:
only this mod drops them. The file is LocalExile's own, moved here unchanged. A database that already has it from
LocalExile does not run it again: the updater knows the file by its text and only renames its record.

## LocalExile versions

Needs a LocalExile with league mods ([LocalExile #1588](https://github.com/LocalExile/LocalExile/pull/1588)): works with
LocalExile v0.6.1-beta. While a LocalExile still has the Legacy module, that module runs and this mod waits
("LocalExile has a module of that id").

`tests/` holds its tests from LocalExile; they need LocalExile's test project and do not run here yet.
