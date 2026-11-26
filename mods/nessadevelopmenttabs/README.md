# Nessa's development tabs

Nessa sells Awakened Gems, Exceptional Gems, Maps, Originator Maps and Unique Maps, in the leagues that list this mod.
Path of Exile 3.29 has no such shop: LocalExile's Nessa sells what she sells there. These tabs bypass ordinary
progression, which is what they are for.

It was LocalExile's Custom module `NessaDevelopmentTabs`, and is the same code.

## Turning it on

It is a league's mod (`"leagues": "listed"`): it runs only in the leagues that list it, and no league lists it as it
comes. After installing it, tick **Nessa's development tabs** in a league's list in Waypoint's Leagues section, or add it
in `data/league-modules.json` of your LocalExile:

```json
{ "Standard": { "add": ["nessadevelopmenttabs"] } }
```

Then restart the servers: they read which leagues list a mod when they start.

A league that listed the old module (`Custom/NessaDevelopmentTabs` or `NessaDevelopmentTabs`) lists this mod: the id is
the same in any case. The setting below moves with it, from `data/modules/NessaDevelopmentTabs.json` to
`data/mods/nessadevelopmenttabs.json`, at the first start.

| Setting | Default | |
|---|---|---|
| `mode` | `add` | `add` puts the tabs after her progression tabs; `replace` shows the full development layout, the custom colour tabs too, instead of them |

The mod gives its leagues their rules: Standard's, with these tabs. A league takes the rules of one module or mod, so it
does not go with Ruthless in one league.

## LocalExile versions

Needs a LocalExile with league mods ([LocalExile #1588](https://github.com/LocalExile/LocalExile/pull/1588)): the first
release after 0.6.0-beta. While a LocalExile still has the Custom module, that module runs and this mod waits
("LocalExile has a module of that id").
