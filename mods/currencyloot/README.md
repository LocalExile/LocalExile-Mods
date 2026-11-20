# Currency Loot

Add configurable bonus currency to eligible monster kills alongside the normal loot. Each entry uses the currency's
existing base drop level. The mod accepts supported stackable currency and skips gold and quest items.

Enable with Waypoint or `/mod_enable currencyloot`. It starts disabled and adds no chat commands. Use the
**Bonus currency per kill** setting in Waypoint, or edit `data/mods/currencyloot.json` in the server folder.
Use an exact currency name or metadata ID as the key. A value of `1.25` gives one extra item and a 25% chance
of a second. The default gives an extra Chaos Orb on 5% of eligible kills.

Inspired by the configurable currency drops in [mingyue8621/LocalExile-Mods](https://github.com/mingyue8621/LocalExile-Mods).
Compatible with LocalExile 0.5.2-beta.
