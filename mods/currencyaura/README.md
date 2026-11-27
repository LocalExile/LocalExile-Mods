# Currency Aura

Auto-loots currency around you while you play, the same way gold is picked up: walk near it and it goes into your
inventory. That covers all stackable currency (orbs, scrolls, essences, fossils, oils, catalysts, splinters, Stacked
Decks, Lifeforce ...) and, if you want them, divination cards and scarabs.

- Gear, flasks, gems and maps are never picked up.
- A stack is picked up only when all of it fits in your inventory; with a full inventory it stays on the ground.
- Only your own or shared drops; items you dropped yourself stay where you put them.

Enable it with Waypoint or `/mod_enable currencyaura`.

## Chat commands

| Command | What it does |
|---|---|
| `/currencyaura` | Shows the current settings |
| `/currencyaura on` / `off` | Turns auto-loot on or off |
| `/currencyaura radius <5-100>` | Sets the pickup radius in cells |
| `/currencyaura cards on` / `off` | Divination cards on or off |
| `/currencyaura scarabs on` / `off` | Scarabs on or off |
| `/currencyaura skip <item>` | Never picks up this item (exact name, e.g. `Scroll Fragment`) |
| `/currencyaura unskip <item>` | Removes it from the never-pick-up list |

The commands save the settings.

## Settings

Editable in Waypoint's Mods panel, or in `data/mods/currencyaura.json`:

| Setting | Default | What it does |
|---|---|---|
| Auto-loot on (`enabled`) | on | Picks up currency near you |
| Pickup radius (`radius`) | 25 cells | 5 to 100; gold's own auto-loot reaches 15 |
| Divination cards (`cards`) | on | Also picks up divination cards |
| Scarabs (`scarabs`) | on | Also picks up scarabs |
| Never pick up (`skip`) | empty | Exact item names or metadata ids left on the ground |

## LocalExile versions

0.6.0-beta and 0.6.1-beta.
