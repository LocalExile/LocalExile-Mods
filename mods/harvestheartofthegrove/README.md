# Heart of the Grove

The Heart of the Grove from Path of Exile 3.13 (3.14 removed it): on rare occasions a map's Sacred Grove has no crops,
and Oshabi's encounter instead, a soul tree at the grove's centre. The rewards are Oshabi's 3.29 ones, not 3.13's, and
there is no Groveseed Altar (bloodlines are 3.29).

It was LocalExile's Legacy module `HarvestHeartOfTheGrove`, and is the same code. Path of Exile 3.29 has no Heart of the
Grove, so it lives here now. The 3.29 Atlas passive of the same name and Oshabi through the Sacred Blossom are part of
LocalExile and do not need this mod.

## Turning it on

It is a league's mod (`"leagues": "listed"`): it runs only in the leagues that list it, and no league lists it as it
comes. After installing it, tick **Heart of the Grove** in a league's list in Waypoint's Leagues section, or add it in
`data/league-modules.json` of your LocalExile:

```json
{ "Standard": { "add": ["harvestheartofthegrove"] } }
```

Then restart the servers: they read which leagues list a mod when they start.

A league that listed the old module (`Legacy/Content/HarvestHeartOfTheGrove` or `HarvestHeartOfTheGrove`) lists this mod:
the id is the same in any case. The setting below moves with it, from `data/modules/HarvestHeartOfTheGrove.json` to
`data/mods/harvestheartofthegrove.json`, at the first start.

| Setting | Default | |
|---|---|---|
| `chancePercent` | 2 | chance in percent that a map's Sacred Grove is the Heart of the Grove, before `map_harvest_chance_for_oshabi_+%` (3.13 called it very rare and published no rate) |

## LocalExile versions

Needs a LocalExile with league mods ([LocalExile #1588](https://github.com/LocalExile/LocalExile/pull/1588)): works with
LocalExile v0.6.1-beta. While a LocalExile still has the Legacy module, that module runs and this mod waits
("LocalExile has a module of that id").
