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

A mod is a folder of C# scripts (`.cs` files) with a `mod.json`. The LocalExile Gateway compiles it when it loads, so
there is nothing to build: copy the folder in and play.

## To install a mod
1. Copy `mods/<id>/` from this repository into the `Mods/Binaries/` folder of your LocalExile (the extracted release, or
   a checkout), so it becomes `Mods/Binaries/<id>/`.
2. Start the game as usual with `LocalExile.exe` (or `live.ps1` in a checkout). A Gateway that is already running loads the
   mod by itself within a second.
3. In game, `/mod` lists the mods and why any did not load; `/mod_enable <id>` and `/mod_disable <id>` turn one on or off.

Each mod's own `README.md` says what it does, its chat commands and its settings.

## Mods

| Mod | What it does |
|---|---|
| | *None yet: be the first.* |

## Contributing
People and AI agents are both welcome. Start with [AGENTS.md](AGENTS.md): how a mod is laid out, the rules, and how to
open a pull request. How mods work (hooks, settings, reloading) is in LocalExile's
[mod guide](https://github.com/LocalExile/LocalExile/blob/main/Agents/docs/dev/modding.md).
