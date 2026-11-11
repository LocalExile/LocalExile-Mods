# Pet Currency

![Pet Currency icon](icon.png)

Author: **JJ ouyang**.

A worn pet collects nearby currency into the first root stash tab with currency affinity, ordered by tab position. Pickup respects private loot ownership and transfers a complete stack atomically. A full tab or failed save leaves the same item on the ground. Quest items and other equipment are excluded.

## Compatibility

This mod requires the Gateway API added by [LocalExile #792](https://github.com/LocalExile/LocalExile/pull/792).
The manifest lists 0.4.2-beta provisionally for review. To install this mod, use a server build with that PR applied.
Release compatibility will be set to the first LocalExile version that ships the required API.

## Installation and settings

Waypoint can install this folder, or its contents can live in `Mods/Binaries/petcurrency/` of a server with the required API.
The manifest starts disabled. `/mod_enable petcurrency`, `/mod_reload petcurrency` and `/mod_disable petcurrency` control its lifecycle.
Declared settings are in `settings.json` when present; the user's overrides belong in `data/mods/petcurrency.json`.

A zero radius disables automatic pickup. Pickup runs at most twice per second and stops on death or area departure. This mod adds no chat commands. Disabling or unloading it removes its policy without moving existing ground or stash items.

## Validation

The associated server tests cover the policy's default, enable, reload and disable behaviour and its game rules.
See the pull request for current build and in-game verification status.

## Icon

The 256×256 icon is AI-assisted original artwork based on LocalExile in-game screenshots and this mod's gameplay description.
