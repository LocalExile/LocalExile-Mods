# Skip Campaign

Adds an opt-in **Skip Campaign** toggle to Path of Exile 3.29 character creation so new characters can spawn directly in **Karui Shores** at endgame level with campaign quest flags, passive skill books, Kitava resistance penalties, waypoints, refund points, Labyrinth ascendancy trials, and starting gold automatically applied.

## Optional NativeMods Client Plugin

This is a **dual mod** (`nativeClient.required = false`, zero `rawAccess` required):

- **With NativeMods (`Client/` -> `skip-campaign.Client.dll` + `client-assets/`):**
  - Injects a **Skip Campaign** checkbox directly into every League Selection card (`LeagueOverlay` via `ScreenSlot.LeagueCardModes`) and keeps a synchronized mirror checkbox on the Create Character bar (`ScreenSlot.CreateCharacterBar`).
  - Tags outgoing `C→S 0x12` (`CreateCharacter.S78`) with `|nm:skip_campaign=1` when checked, resets the checkbox on successful creation, and preserves it if creation fails (for example, name already taken).
  - Spawns the new character directly in Karui Shores at the configured level with `SuppressStartKit = true` (no Twilight Strand flash), applies all one-time progression on first login, and shows a green **Campaign Skipped** HUD toast.
- **Without NativeMods (vanilla client):**
  - The server mod works cleanly out of the box, with `/skipcampaign apply` providing the exact same one-time progression and teleport to Karui Shores.

## Chat commands

| Command | What it does |
|---|---|
| `/skipcampaign` or `/skipcampaign status` | Shows active settings, whether this character has skipped the campaign, and whether the NativeMods client plugin is connected |
| `/skipcampaign apply` | Vanilla-client fallback: applies the configured campaign skip progression to the current character (respecting `enabled` and `requireLeagueCompletion`) and travels to the starting town |

## Settings

Editable in Waypoint or `data/mods/skip-campaign.json`:

| Setting | Default | Description |
|---|---|---|
| `enabled` | `true` | Enable or disable campaign skipping on character creation and `/skipcampaign apply` |
| `requireLeagueCompletion` | `false` | Require at least one living character in the exact same league (respecting SSF and Hardcore death isolation) at or above Lv.68 or in Karui Shores |
| `startingAreaId` | `2_11_endgame_town` | Starting town `WorldAreas` Id assigned before first login (Karui Shores) |
| `startingLevel` | `68` | Starting character level (`1`–`100`) |
| `grantCampaignPassiveBooks` | `true` | Grant all Acts 1–10 campaign Book of Skill quest flags (`+23` passive points; Bandits are left open so you can still choose) |
| `grantAscendancyTrials` | `true` | Award Normal, Cruel, and Merciless Labyrinth ascendancy points (`6` points) on first login |
| `startingGold` | `50000` | Gold credited to the league stash on first login (`0`–`10000000`) |
| `refundPoints` | `20` | Minimum passive refund points granted on first login (`0`–`255`) |

## Compatibility

Works with LocalExile **0.6.1-beta+** (requires the `GameEvents.CharacterCreated` and `IPlayerWorld` progression hooks merged in [LocalExile PR #1620](https://github.com/LocalExile/LocalExile/pull/1620)). For the in-client character-creation checkbox, enable NativeMods in [Waypoint](https://github.com/LocalExile/Waypoint/pull/54).
