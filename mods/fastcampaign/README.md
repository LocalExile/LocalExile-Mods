# Fast Campaign

A faster campaign: more experience from kills, extra movement speed, and `/fast`, which lists the Gateway's own commands that skip ahead (waypoints, area travel, level). Its chat lines are in English, Simplified or Traditional Chinese.

| Command | Does |
|---|---|
| `/fast` | its settings, and the commands that skip ahead |
| `/fast_xp <1-100>` | sets the kill experience multiplier (saved) |
| `/fast_speed <0-300>` | sets the extra movement speed in %, from the next area (saved) |
| `/fast_lang <en \| zh-Hans \| zh-Hant>` | sets the language of its chat lines (saved) |

| Setting | Default | |
|---|---|---|
| `experience` | 5 | kill experience multiplier (1 to 100) |
| `speed` | 50 | extra movement speed, % (0 to 300) |
| `language` | en | `en`, `zh-Hans` or `zh-Hant` |

Off by default: `/mod_enable fastcampaign` in game, or Waypoint's Mods section.

Works with: 1.1.3 works with LocalExile v0.4.3-beta, v0.5.0-beta and v0.5.1-beta (the same code as 1.1.2); 1.1.1 with v0.4.2-beta; 1.1.0 with v0.4.1-beta, the first with the one hook system (`GameEvents`). Its 1.0.x versions work with v0.2.0-beta to v0.4.0-beta; not v0.1.0-beta.
