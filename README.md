# Important Notice
LocalExile is not an official Grinding Gear Games product, and it is not affiliated with or endorsed by Path of Exile or
Grinding Gear Games. LocalExile does not in any case sponsor nor support illegal public servers. If you use this project
to run an illegal public server and not for testing and learning it is your own personal choice.

This project is for fun and education only. It is intended for local gameplay with no internet, much like MMO solutions
(TrinityCore, AzerothCore, MaNGOS).

[Join Discord](https://discord.gg/pd2Fd5M6Hd)

# LocalExile-Mods
Official mods for [LocalExile](https://github.com/LocalExile/LocalExile), the local server for the Path of Exile
**3.29.3b** client. Every mod here is maintained with the project, and contributors and new contributors are welcome to
add their own or improve one by pull request.

A mod is a folder with a `mod.json` and either C# scripts (`.cs` files), which the LocalExile Gateway compiles when it
loads them, or a C# project, whose built `.dll` is published in the mod's releases.

## To install a mod
Use **Waypoint**, LocalExile's launcher. Its Browse panel lists this repository's mods and installs one with a click:
from its release, or from its source when it has none. Its Mods panel turns them on and off and edits their settings.
Mods are not only here: anyone can keep mods in their own repository with the same layout and a `mods.json` index, publish
their own releases, and add it to Waypoint's Browse panel by its URL. Waypoint also installs a mod folder from your disk.

By hand, without Waypoint:
1. Put the mod's folder into the `Mods/Binaries/` folder of your LocalExile (the extracted release, or a checkout), so it
   becomes `Mods/Binaries/<id>/`. For a script mod that is `mods/<id>/` from this repository; for a project mod, the
   contents of its release zip.
2. Start the game as usual with `LocalExile.exe` (or `live.ps1` in a checkout). A Gateway that is already running loads the
   mod by itself within a second.
3. In game, `/mod` lists the mods and why any did not load; `/mod_enable <id>` and `/mod_disable <id>` turn one on or off.

Each mod's own `README.md` says what it does, its chat commands, its settings and the LocalExile versions it works with.

## Releases
Each mod is released on its own, from the tag `<id>-v<version>` (for example `gm-commands-v1.0.0`), with the asset
`<id>-<version>.zip`: the folder that goes into `Mods/Binaries/<id>/`. The release notes say which LocalExile versions it
works with. The maintainers publish the releases of the mods in this repository.

## Mods

| Mod | What it does |
|---|---|
| [liveeconomy](mods/liveeconomy/) | Faustus's Currency Exchange as a live market, at the official Path of Exile leagues' prices |
| [fastcampaign](mods/fastcampaign/) | a faster campaign: 5× kill experience, +50% movement speed, `/fast` lists the commands that skip ahead |
| [gm-commands](mods/gm-commands/) | the development and testing chat commands: teleport, items, monsters, experience, test hits, protocol probes |
| [globalchat](mods/globalchat/) | talk to other LocalExile players through the LocalExile global chat server |
| [waypoint-tools](mods/waypoint-tools/) | lets Waypoint's tools reach the game without the client control bridge: the item crafter gives its items through it |
| [backpack](mods/backpack/) | an optional native 20-slot extra backpack for every class, with safe recovery after disable |
| [petloot](mods/petloot/) | optional pet pickup with per-character item filters, minimum stacks and pause/resume |
| [itemquantity](mods/itemquantity/) | 10× as many item drops |
| [pob](mods/pob/) | preview and import character builds through an external converter; requires LocalExile #639 |
| [itemrarity](mods/itemrarity/) | 10× the chance of magic, rare and unique equipment |

## Contributing
People and AI agents are both welcome. Start with [AGENTS.md](AGENTS.md): how a mod is laid out, the rules, and how to
open a pull request. How mods work (hooks, settings, reloading) is in LocalExile's
[mod guide](https://github.com/LocalExile/LocalExile/blob/main/Agents/docs/dev/modding.md).
