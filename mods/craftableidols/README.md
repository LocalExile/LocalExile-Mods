# Craftable Idols

Lets crafting currencies modify Legacy of Phrecia Idols (`AtlasRelic`) on the server, bypassing their `Unmodifiable` class restriction.

It is a script mod that depends on `Game` and requires no changes to LocalExile. It starts disabled: turn it on in
Waypoint or with `/mod_enable craftableidols`.

Idols cannot be crafted on the official servers, so this is a custom rule, not a replica: the Vaal and Mirror rules
and odds below are the mod's own.

## How it works

On the client, the red **Unmodifiable** line on an Idol's tooltip is read directly from the client's local `Data/ItemClasses.dat` table (`AtlasRelic`, row 94), not from a server packet, so a server-side mod cannot hide that tooltip line. However, the client still sends currency application packets (`C→S 0x0071` and Shift-repeat `0x0072`) when you right-click an orb and click an Idol.

Craftable Idols intercepts those packets for `AtlasRelic` items across your main inventory, stash tabs (including the Currency Stash Tab crafting slot) and the Atlas Idol side panel, and applies the server's own Idol modifier pool (`Mods` domain 37) with Idol affix limits (1 prefix / 1 suffix on magic, 2 prefixes / 2 suffixes and 3–4 affixes on rare).

### Supported currencies

Enabled by default:
- **Orb of Transmutation** — upgrades a normal Idol to magic (1–2 affixes)
- **Orb of Alteration** — rerolls a magic Idol's explicit affixes
- **Orb of Augmentation** — adds an explicit affix to a magic Idol with an open prefix or suffix
- **Regal Orb** — upgrades a magic Idol to rare, adding one explicit affix and a rare name
- **Orb of Alchemy** — upgrades a normal Idol to rare (3–4 affixes, up to 2 prefixes / 2 suffixes)
- **Chaos Orb** — rerolls a rare Idol's unprotected explicit affixes
- **Exalted Orb** — adds an explicit affix to a rare Idol with an open prefix or suffix
- **Orb of Annulment** — removes one random unprotected explicit affix from a magic or rare Idol
- **Orb of Scouring** — strips unprotected explicit affixes from a magic or rare Idol
- **Divine Orb** — rerolls explicit modifier values within their tiers (including on unique Idols)
- **Blessed Orb** — rerolls implicit modifier values within their tiers

Optional (off by default in `settings.json`):
- **Fracturing Orb** (`allowFracturing`) — fractures one random explicit affix on a rare Idol with 4 explicit modifiers; fractured modifiers are preserved by subsequent orbs
- **Vaal Orb** (`allowVaal`) — corrupts an Idol (normal/magic: 50% unchanged / 50% rare; rare: 50% unchanged / 25% replace 1–2 affixes / 25% reroll; unique: 75% unchanged / 25% brick to rare), and enables **Tainted Chaos Orb** and **Tainted Exalted Orb** on corrupted rare Idols
- **Mirror of Kalandra** (`allowMirror`) — places a mirrored copy of a non-unique, uncorrupted Idol on the cursor

Unidentified Idols must be identified with a Scroll of Wisdom first (except when using a Vaal Orb). Unique Idols refuse rarity/affix-rolling orbs and accept only Divine, Blessed, and (when enabled) Vaal Orbs. Corrupted and mirrored Idols refuse ordinary crafting orbs.

## Chat commands

None. Use crafting currencies on Idols directly in game (single-click or Shift-click).

## Settings

Configured in Waypoint or `data/mods/craftableidols.json`:

| Setting | Default | What it does |
|---|---|---|
| `allowFracturing` | `false` | Allows Fracturing Orbs on 4-mod rare Idols |
| `allowVaal` | `false` | Allows Vaal Orbs on Idols, plus Tainted Chaos and Tainted Exalted Orbs on corrupted rare Idols |
| `allowMirror` | `false` | Allows Mirrors of Kalandra on non-unique, uncorrupted Idols |

## Compatibility

- LocalExile: `0.5.3-beta`, the first release with the Phrecia `Idols` module (`Modules/Legacy/Idols`).
