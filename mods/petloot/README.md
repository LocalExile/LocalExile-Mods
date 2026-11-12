# Pet Loot

An opt-in mod: a worn cosmetic pet walks to nearby selected currency and picks it up. It is disabled on installation;
enable it with `/mod_enable petloot` or Waypoint. `/petloot off` pauses pickup for this character, including after an
area change or restart; `/petloot on` resumes it. Removing or disabling the mod leaves normal pets following the player.

Requires LocalExile PR #737 for the game's saved pickup and cosmetic pet walking methods. The manifest lists 0.5.0-beta pending the first release containing that change. Pickup runs in leagues with both Game and Cosmetics.

## Filters

Defaults: gold and 20 common currencies, within 60 cells. The preset includes Chaos, Divine, Exalted and Annulment
Orbs, Mirrors, Alteration, Alchemy, Augmentation, Chance, Transmutation, Fusing, Chromatic, Jeweller's, Regret, Scouring,
Regal, Blessed and Vaal Orbs, Gemcutter's Prisms and Glassblower's Baubles. Scrolls, shards, fragments, essences, cards
and scarabs are left down unless explicitly allowed. Equipment, gems, quest items and hand-dropped items are never taken.

An explicit rule overrides the preset: `0` blocks an item; a positive number is its minimum ground-stack size. Exact
metadata ids take precedence over display names; names are matched case-insensitively, without substring matching.
Use `/petloot nearby` for names and stable metadata ids. Rules are rechecked before pickup; changing a rule cancels the
current target. A blocked path times out without taking a remote item; a full inventory leaves the stack on the ground.
Gold disabled here means **pet pickup** disabled: the game's ordinary nearby gold pickup is unchanged.

## Commands

- `/petloot` or `/petloot list`: current settings, overrides and help.
- `/petloot on`, `/petloot off`, `/petloot toggle`: resume or pause this character.
- `/petloot block Orb of Transmutation`: exclude that item.
- `/petloot allow Chaos Orb 5`: take only ground stacks of at least five Chaos Orbs.
- `/petloot allow Stacked Deck`: opt into another stackable item, minimum one.
- `/petloot remove Chaos Orb`: remove its override and use the preset again.
- `/petloot common on|off`: common-currency preset.
- `/petloot gold on|off`: pet gold pickup.
- `/petloot range 40`: collection radius, 5–80 cells.
- `/petloot nearby`: up to twelve nearby names, stack sizes and metadata ids.
- `/petloot reset`: discard this character's overrides and use server defaults.

Waypoint edits the defaults and rules declared in `settings.json`. User changes are saved by LocalExile in
`data/mods/petloot.json`, so updating this mod preserves them. Chat commands save separate profiles keyed by account,
league and character; another player's choices are unaffected. A character's first command snapshots the current
defaults. Reset that profile to follow future default changes again. Keep this user's settings file private.

The mod orders one walk per target. The server checks the pet and the item again when collecting.
An interrupted or expired walk returns to ordinary pet following.
