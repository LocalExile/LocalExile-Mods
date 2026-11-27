# Loot Weights

One place to tune what monsters drop. Every setting is a factor on the server's own drops, so the defaults change
nothing:

- **Item quantity, Item rarity, Gold, Map drop chance:** how many items a kill drops, how often equipment is magic, rare
  or unique, how much gold, and how often a map drops.
- **Item kinds:** how often each dropped item is currency, a gem, a flask or equipment, against the others. Currency ×2
  with the rest at ×1 makes currency about twice as likely; 0 stops a kind dropping.
- **Cards and scarabs in maps:** the base divination card and scarab chance every map kill rolls. A map's own
  modifiers still add theirs.
- **Single items:** a factor for one currency, divination card or scarab against the others of its kind. Chaos Orb ×10
  makes a currency drop a Chaos Orb ten times as often as before; 0 removes Scrolls of Wisdom from the currency pool.
  A factor goes up to 1,000,000 (a Mirror of Kalandra weighs 0.01 in a pool of about 4,000). Other item types are skipped with a line in the log; a factor above the limit makes the whole list fall back to empty, which the log also says.

The factors stack with the league's rates, the `drop_multiplier_*` server settings and other mods (Item Quantity,
Item Rarity). Enable it with Waypoint or `/mod_enable lootweights`; it starts disabled and adds no chat commands. Edit the
settings in Waypoint, or in `data/mods/lootweights.json` in the server folder; a change reloads the mod.

Works with LocalExile 0.6.1-beta: it needs `DropRoll.Weights`
([LocalExile #1235](https://github.com/LocalExile/LocalExile/pull/1235)).
