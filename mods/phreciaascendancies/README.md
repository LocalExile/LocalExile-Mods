# Phrecia ascendancies

Legacy of Phrecia's alternate ascendancy classes (3.25), and the Phrecia league. The league is Standard's, with these
classes and the [Idols](../idols/).

It was LocalExile's Legacy module `PhreciaAscendancies` and its league `Phrecia`, and is the same code. Path of Exile
3.29 has no Phrecia classes, so they live here now. The engines the classes use (Ward, Rampage, Recoup, link skills and
the rest) stay in LocalExile, where ordinary classes and items use them too.

## Turning it on

Install this mod and [idols](../idols/), then restart the servers. The Phrecia league comes with this mod
(`Leagues/Phrecia/`), and lists both: without the Idols the league does not load, and its characters are listed but
cannot be entered. It is a league's mod (`"leagues": "listed"`), so it runs only in the leagues that list it; for
another league, tick **Phrecia ascendancies** in its list in Waypoint's Leagues section, or add it in
`data/league-modules.json` of your LocalExile:

```json
{ "Standard": { "add": ["phreciaascendancies"] } }
```

A league that listed the old module (`Legacy/PhreciaAscendancies` or `PhreciaAscendancies`) lists this mod: the id is
the same in any case. A Phrecia character keeps its class and progress: the save names the family, not the module.

## The classes

Each class is off until its opt-in is set where the Gateway starts, and needs the reviewed 3.29.3b combined passive
graph, which it checks read-only and never installs:

| Class | Opt-in |
|---|---|
| Gambler | `Game_VERIFIED_GAMBLER=1` |
| Antiquarian | `Game_VERIFIED_ANTIQUARIAN=1` |
| Aristocrat | `Game_VERIFIED_ARISTOCRAT=1` |
| Ancestral Commander | `Game_VERIFIED_ANCESTRAL_COMMANDER=1` |
| Paladin | `Game_VERIFIED_PALADIN=1` |
| Polytheist | `Game_VERIFIED_POLYTHEIST=1` |
| Surfcaster | `Game_VERIFIED_SURFCASTER=1` |
| Behemoth | `Game_VERIFIED_BEHEMOTH=1` |
| Whisperer | `Game_VERIFIED_WHISPERER=1` |
| Harbinger | `Game_VERIFIED_HARBINGER=1` |

The other Phrecia classes are refused before they are saved. Each class's checklist and limits are in LocalExile's
[Agents/docs/game/](https://github.com/LocalExile/LocalExile/tree/main/Agents/docs/game) (`ascendancies.md`,
`phrecia-modules.md` and one file per class).

## LocalExile versions

Needs a LocalExile with league mods ([LocalExile #1588](https://github.com/LocalExile/LocalExile/pull/1588)): works with
LocalExile v0.6.1-beta. While a LocalExile still has the Legacy module, that module runs and this mod waits
("LocalExile has a module of that id"), and LocalExile's own Phrecia league is the one used.

`tests/` holds its tests from LocalExile; they need LocalExile's test project and do not run here yet.
