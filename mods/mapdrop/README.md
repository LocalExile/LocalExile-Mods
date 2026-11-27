# Map Drop

Add one bonus roll for a map of the current Atlas tier when a monster dies. The four chance settings apply by monster
rarity. Turn on **Bonus Tier 1 maps in Acts 8 to 10** to extend the roll to the late campaign. Bonus maps come from the
server's generic map-key catalog and are independent of regular loot and the Atlas drop roll.

The server already rolls a generic map on every kill: a map of the current tier in an Atlas map, and Tier 1 everywhere
in the campaign. Its rates are about the same as this mod's defaults (0.4%, 1.2%, 5% and 20% by rarity), so the defaults
roughly double map drops in maps. Lower the chances for a smaller increase.

Enable with Waypoint or `/mod_enable mapdrop`. It starts disabled and adds no chat commands. Set the odds in
Waypoint or in `data/mods/mapdrop.json` in the server folder. The JSON values are fractions. For example, `0.05`
is a 5% chance on each eligible kill.

Inspired by the map-drop settings in [mingyue8621/LocalExile-Mods](https://github.com/mingyue8621/LocalExile-Mods).
1.0.2 works with LocalExile v0.6.0-beta and v0.6.1-beta (the same code as 1.0.1, which requires v0.6.0-beta for `DropRoll.Player`); 1.0.0 worked with v0.5.2-beta.
