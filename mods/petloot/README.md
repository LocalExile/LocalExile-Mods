# Pet Loot

An opt-in mod: a worn cosmetic pet walks to nearby selected stackable items and picks them up. It is disabled on installation;
enable it with `/mod_enable petloot` or Waypoint. `/petloot off` pauses pickup for this character, including after an
area change or restart; `/petloot on` resumes it. Removing or disabling the mod leaves normal pets following the player.

1.2.1 requires LocalExile v0.6.0-beta (`Player`, `GameEvents.AreaTick`); 1.1.0-1.2.0 required LocalExile v0.5.2-beta, the first release with the movement-speed hook
([LocalExile PR #1045](https://github.com/LocalExile/LocalExile/pull/1045)); it does not load on v0.5.1-beta, where 1.0.5
is the version to use. Pickup runs in leagues with both Game and Cosmetics.

## Movement speed

Worn pets move at **2x** their normal owner-relative speed by default, while following, collecting and returning.
Set the multiplier in Waypoint or with `/petloot speed 2` (0.25–10); `/petloot speed 1` restores normal speed.
The multiplier belongs to the character's saved profile, and existing profiles without a speed value start at 2x.
`/petloot off` pauses collection while keeping this movement setting. Disabling or removing the mod restores ordinary
pet speed on the next player tick, including a walk already in progress. Other entities and the player's speed are unchanged.

## Filters

Defaults: gold and 20 common currencies, within 60 cells. The preset includes Chaos, Divine, Exalted and Annulment
Orbs, Mirrors, Alteration, Alchemy, Augmentation, Chance, Transmutation, Fusing, Chromatic, Jeweller's, Regret, Scouring,
Regal, Blessed and Vaal Orbs, Gemcutter's Prisms and Glassblower's Baubles. Scrolls, shards, fragments, essences, cards
and scarabs are left down unless enabled by category or explicitly allowed. Waypoint can enable all stackable currency,
divination cards, and map fragments independently. Existing profiles keep the common-currency preset and saved overrides. Equipment, gems, quest items and hand-dropped items are never taken.

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
- `/petloot range 40`: collection radius, 5–100 cells.
- `/petloot speed 2`: pet movement multiplier, 0.25–10 (default 2).
- `/petloot nearby`: up to twelve nearby names, stack sizes and metadata ids.
- `/petloot reset`: discard this character's overrides and use server defaults.

Waypoint edits the defaults and rules declared in `settings.json`. User changes are saved by LocalExile in
`data/mods/petloot.json`, so updating this mod preserves them. Chat commands save separate profiles keyed by account,
league and character; another player's choices are unaffected. A character's first command snapshots the current
defaults. Reset that profile to follow future default changes again. Keep this user's settings file private.

The server checks the pet and the item again when collecting. The same pet then goes straight to the closest eligible drop
from its current position, within the character's pickup radius. If another player picks up the target first, the pet can
switch to another drop. Once no eligible drop remains, it returns to ordinary pet following.
