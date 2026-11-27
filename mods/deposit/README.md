# Deposit All

Stand at your stash and type `/deposit`: everything in your main inventory that belongs in a special stash tab goes
there in one go, the same as a Ctrl + click on each item. That covers currency, fragments (scarabs included), divination
cards, essences, gems, flasks and the league tabs (Delve, Blight, Delirium, Ultimatum).

- Gear, and anything no chosen special tab holds, stays in your inventory.
- Items on the keep list stay (by default Portal Scroll and Scroll of Wisdom, so you keep your scrolls).
- A stack that doesn't fit stays, or the part of it that doesn't fit.
- Items go to the first special tab of their kind. Affinity tags set on normal tabs in the tab settings are not used.

Enable it with Waypoint or `/mod_enable deposit`.

## Chat commands

| Command | What it does |
|---|---|
| `/deposit` | At your stash: sends the items to their special tabs |
| `/deposit keep` | Lists the items `/deposit` leaves in your inventory |
| `/deposit keep add <item>` | Keeps this item too (exact name, e.g. `Orb of Transmutation`) |
| `/deposit keep remove <item>` | Takes it off the keep list |

## Settings

Editable in Waypoint's Mods panel, or in `data/mods/deposit.json`:

| Setting | Default | What it does |
|---|---|---|
| Items to keep (`keep`) | Portal Scroll, Scroll of Wisdom | Exact item names or metadata ids that stay in your inventory |
| Tabs to fill (`tabs`) | all | Which special tabs it fills: currency, fragments, cards, essences, gems, flasks, league |

## LocalExile versions

0.6.0-beta and 0.6.1-beta.
