# Extra Backpack

An opt-in 4×5 native extra backpack for every class and league. It uses the client's retained Wildwood Rucksack
interface; it does not grant an ascendancy, spend passive points or require a Wildwood quest. No client files change.

Install with Waypoint or copy this folder into `Mods/Binaries/backpack`. Enable with `/mod_enable backpack`, then press
`I`: the extra grid appears to the left of the normal inventory, without relogging. Use the normal item pickup,
placement, stacking and splitting controls. The grid accepts equipment, gems, currency and other ordinary inventory
items, with their original footprints and stack limits. Contents belong to the character and survive area changes
and restarts. No backpack items count as equipped items.

`/mod_disable backpack` stops new deposits. When it still contains items, the grid stays visible in recovery mode:
take the items out, and the empty grid disappears. Re-enabling restores deposits immediately. The same recovery
applies after uninstalling the mod, so saved items are not hidden. This protection is provided by the companion
LocalExile change, even when the mod is gone. No scripts or background tasks remain running after unload.

Requires a LocalExile build containing [#769](https://github.com/LocalExile/LocalExile/pull/769) for the hot toggle and withdrawal recovery. The change is on main. The manifest lists 0.5.0-beta as the catalogue placeholder until the first release that includes #769.

The normal inventory remains the default destination for pickups. This mod adds no automatic pickup or automatic
transfer to the extra grid. Pet Loot's item filters and pickup controls remain independent.
