# 3.26 Mercenaries

Brings Path of Exile 3.26 Mercenaries of Trarthus to every class and ascendancy from level 1:

- **Permanent mercenaries for all classes:** Grants `can_have_active_mercenary_follower` and all 7 `mercenary_can_use_unique_*` stats (Rings, Amulet, Belt, Helmet, Boots, Gloves, Weapons/Shields/Quivers) to every character.
- **Unique Body Armours:** Allows permanent mercenaries to equip Unique Body Armours (enabled by default; configurable in `settings.json`).
- **Optional all-zone spawning:** When enabled, spawns a mercenary encounter in every campaign combat zone and map once the character has reached The Mud Flats (never in The Twilight Strand, nor in The Coast / The Tidal Island before reaching The Mud Flats), scaled to player level.
- **Configurable Luminary bonuses:** Independently toggle Noble Blood's `-30%` damage penalty (off by default for 3.26 parity), Oath of Fealty, Golden Glory, Loyal Bodyguard, Bestowed Knighthood, Legendary Arms' `+8%` more damage per unique item, and all 7 Luminary small passives without double-stacking on Luminary characters.

## Optional NativeMods Client Plugin

This is a **dual mod** (`nativeClient.required = false`):

- **With NativeMods (`Client/` -> `mercenaries326.Client.dll`):** Unlocks the native **Hire** button in `EncounterMercenaryLoadout` for all classes and enables native drag-and-drop / Ctrl+click equipping of **Unique Body Armours** in `MercenaryLoadout`. The plugin activates automatically when this server mod is enabled and restores stock 3.29 client behavior immediately when disabled or unloaded.
- **Without NativeMods (vanilla client):** All server mechanics work out of the box, with `/merc326 hire` and `/merc326 equip` chat commands providing graceful fallbacks for the two client-side UI gates.

## Chat commands

| Command | What it does |
|---|---|
| `/merc326` or `/merc326 status` | Shows active settings and whether the NativeMods client plugin is connected |
| `/merc326 hire [join\|reserve]` | Vanilla client fallback: starts a permanent Hire duel on the current area's open mercenary encounter (`join` by default, or `reserve`) |
| `/merc326 equip [1-3]` | Vanilla client fallback: equips an identified Unique Body Armour from your cursor or inventory onto your invited (or specified slot 1–3) permanent mercenary |

## Settings

Editable in Waypoint or `data/mods/mercenaries326.json`:

| Setting | Default | Description |
|---|---|---|
| `allowUniqueBodyArmour` | `true` | Allow permanent mercenaries to equip Unique Body Armours |
| `spawnInAllZones` | `false` | Spawn a mercenary in every combat zone and map after reaching The Mud Flats, scaled to player level |
| `enableNobleBloodDamagePenalty` | `false` | Apply Luminary Noble Blood's `-30%` less Mercenary & Minion Damage penalty |
| `enableOathOfFealty` | `true` | Link skills cost 50% less, infinite attachment duration, and you do not die when your linked permanent mercenary dies |
| `enableGoldenGlory` | `true` | Additive Light Radius modifiers apply to Link Skill buff effect on your permanent mercenary |
| `enableLoyalBodyguard` | `true` | 20% hit damage taken from nearest permanent mercenary before Life/ES (if higher life); 40% damage taken recouped as Life (if lower life) |
| `enableBestowedKnighthood` | `true` | +50% permanent mercenary non-curse aura effect and 100% chance to taunt on hit |
| `enableLegendaryArmsDamageBonus` | `true` | +8% more Mercenary & Minion Damage per equipped unique item |
| `enableLuminarySmallNodes` | `true` | Grant all 7 Luminary small passives (+30% Mercenary & Minion Life, +45% Mercenary & Minion Damage, +20% Link Cast Speed, +35% Light Radius) |

## Compatibility

Works with LocalExile **0.6.1-beta+** (requires [LocalExile PR #1639](https://github.com/LocalExile/LocalExile/pull/1639) for permanent mercenaries, Luminary mechanics, and the `CompanionRules` / `CompanionEquip` / `EncounterSpawn` hooks).
