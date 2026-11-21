# Exciting Items

Rare chances on magic, rare and unique drops, each with its own chance and all able to land on the same item:

- **Unique implicits.** The item gets one, two or three modifiers of unique items as extra implicits. A modifier of a
  rarer unique comes up as seldom as that unique drops.
- **An empowered modifier.** One of its modifiers, implicit or explicit (a unique implicit from above included), rolls 10-100% higher than it did (a random amount in the
  range the settings give), past the top of its tier. A modifier with a downside, or a switch such as "has no
  sockets", is never chosen.
- **An extra fractured modifier.** The item gets one modifier more than it could have: a rare is filled to three
  prefixes and three suffixes and gets a seventh (a rare jewel a fifth), a magic item a third, a unique one rare-style
  prefix or suffix. Every modifier of that side, prefixes or suffixes, is fractured (on a unique, the one added).
  These modifiers can also be ones only an influenced item rolls (one influence the item does not have, at the tiers its
  item level allows), without the item becoming influenced.

They apply to influenced items too: the game's crafting rules against fracturing influenced or synthesised items are
for currency, not for drops. An item with any of them drops synthesised, so it looks special on the ground, and, with
`showInFilters` on, a rule put at the top of your loot filters shows it with a beam and a sound. Chat names which effects a drop got, not what
they are: identify it to see.

## Which unique modifiers can come

`implicits.mode` picks one:

| Mode | Gets |
|---|---|
| Wild (default) | any unique modifier on any gear |
| Sanity | only modifiers of uniques of the same kind of item: swords' on swords, belts' on belts |
| Thematic | offence on weapons, quivers and gloves; defence on body armours and shields; speed and ailments on boots; defence and scaling (per charge, per stat, auras, minions) on helmets; utility and odd unique effects on rings, amulets and belts |

In every mode a modifier is welcome on its own kind of item (a helmet unique's on helmets), and every modifier not blacklisted can come somewhere: one the chosen mode leaves without a slot that drops follows Wild's rules, and one of a kind of item that never drops (a fishing rod's) can go on any gear. Rare and magic jewels get unique jewels' modifiers in every mode, and gear never gets those. A modifier that names a
kind of item or is local to it (adds to the item's own armour, its sockets) stays on that kind of item. Cluster jewels
and Timeless Jewels get nothing.

`blacklist` lists modifiers never used: a Mods id or its start, `stat:` and a piece of a stat id, or `class:` and a
kind of item whose uniques' modifiers stay with it. The defaults are the modifiers tied to their own item: radius
jewels' and Timeless Jewels' tree effects, Thread of Hope's ring, granted supports chosen by index (Forbidden Shako),
effects only that unique has (`local_unique_` stats), Tabula Rasa's and level-requirement modifiers, and flasks',
tinctures', maps', idols' and Sanctum relics' modifiers (`class:`). Every other unique modifier can come on some kind
of item in every mode.

## Commands

| Command | Does |
|---|---|
| `/exciting` | the chances and the mode |
| `/exciting next implicit [1-3]` | the next magic, rare or unique drop gets that many unique implicits |
| `/exciting next empowered` | the next one gets an empowered modifier |
| `/exciting next extra` | the next one gets an extra fractured modifier |
| `/exciting next all` | the next one gets all three |

## Settings

| Setting | Default | Does |
|---|---|---|
| `implicits.chance`, `chanceTwo`, `chanceThree` | 1000, 100000, 1000000 | one, two or three unique implicits: 1 in this many drops |
| `implicits.mode` | wild | wild, sanity or thematic (above) |
| `implicits.rarityWeights` | on | rarer uniques' modifiers come up less often |
| `implicits.rarities`, `empowered.rarities`, `extraModifier.rarities` | magic, rare, unique | which drops can get each effect |
| `empowered.chance` | 500 | 1 in this many drops |
| `empowered.percent` | 10-100 | how much stronger, picked at random |
| `extraModifier.chance` | 1000 | 1 in this many drops |
| `extraModifier.influencedMods` | on | the extra modifier can also be an influenced item's modifier (the item stays uninfluenced) |
| `blacklist` | (above) | modifiers never used as implicits |
| `mark`, `showInFilters` | on, off | the synthesised look, and the loot filter rule |
| `announce` | on | the chat line |
| `filterFolder` | (empty) | the loot filter folder; empty is Documents\My Games\Path of Exile\LocalExile |

With `showInFilters` on, the loot filter rule goes between two marker lines at the top of every `.filter` file in the
folder, and comes out again when the setting is turned off. Without a filter of your own the mod writes one that holds
only the rule.

Only new drops are changed: an item a player drops from the inventory is never rolled again.

Works with LocalExile 0.5.2-beta and its main branch; it needs no change in LocalExile.
