# Survivors

The Survivors league: survive the horde. Skills cast themselves, each run level offers an upgrade, and seven stages end
with seven gate bosses. Path of Exile has no such league, so it is a mod.

It was LocalExile's three Custom modules `SurvivorsSkills`, `SurvivorsRewards` and `SurvivorsRun` and its league
`Survivors`, and is the same code, one folder each (they load in that order); the run now checks for this mod's id in
the player's league. How a run goes, its rewards, bosses and limits: LocalExile's
[Agents/docs/game/survivors-mode.md](https://github.com/LocalExile/LocalExile/blob/main/Agents/docs/game/survivors-mode.md).

## Turning it on

Install it and restart the servers. The Survivors league comes with the mod (`Leagues/Survivors/`) and lists it; Survivors
characters can be entered again. It is a league's mod (`"leagues": "listed"`), and it allows only Cosmetics,
Chat and Commands beside it (`"only"`; the game itself always may): a league that lists anything else with it does not
load, and says so.

A league that listed the old modules (`Custom/Survivors/SurvivorsRun` and the others) does not get this mod: the ids
differ. List `survivors` there in place of all three, in the same change, as the old entries are not allowed beside it:

```json
{ "My Run": { "remove": ["SurvivorsSkills", "SurvivorsRewards", "SurvivorsRun"], "add": ["survivors"] } }
```

## In game

`/survivors start`, `stop`, `pick <1-6>` and `status` in the Survivors league, with more diagnostics (`test`, `link`,
`matrix`, `rank`, `unique`, `uniques`, `curve`). The environment variables `Game_SURVIVORS_BOSS_LIFE`,
`Game_SURVIVORS_STAGE_SECONDS`, `Game_SURVIVORS_MAX_ALIVE` and `Game_SURVIVORS_TRACE` tune and trace a run, as before.

## LocalExile versions

Needs a LocalExile with league mods ([LocalExile #1588](https://github.com/LocalExile/LocalExile/pull/1588)): the first
release after 0.6.0-beta. While a LocalExile still has the Custom modules, they run, LocalExile's own Survivors league
is the one used, and this mod is not loaded, as no league lists it.

`tests/` holds its tests from LocalExile as they were for the three modules; they need LocalExile's test project and do
not run here yet. Before they do, they need the league read from this mod's `Leagues/`, the one owner `survivors` in place
of the three module ids, and `SurvivorsSkillsModule.LoadData()` called once.
