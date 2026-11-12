# GM Commands

The development and testing chat commands that used to be LocalExile's `Commands` module: teleport, items, monsters,
character progress, test hits, protocol probes and the campaign driver's `/state`. GGG's servers have none of them, so
LocalExile keeps only `/oos` and `/passives`, and `/reload` and `/lastpackets` are the Gateway's own. In game,
`/commands` (`/com <name>` for one) lists every command with its usage.

The mod uses the server and the Game module directly, as a module does, and lists `Game` in `depends`: it reloads
whenever the Game module does.

| Group | Commands |
|---|---|
| Movement | `/tp`, `/goto`, `/tour` (with `live.ps1 -Walkmaps`), `/pos` |
| Monsters and NPCs | `/spawn_mob`, `/mobs`, `/despawn`, `/kill`, `/mob_mods`, `/mob_tp`, `/mob_move`, `/mob_walk`, `/anim`, `/ai`, `/statue`, `/npc`, `/npcs`, `/npc_place`, `/npc_face` |
| Items | `/item`, `/item_give` (Waypoint's item crafter), `/item_tab`, `/gem`, `/gem_xp`, `/adorned`, `/unique_jewel`, `/radius_jewel`, `/impossible_escape`, `/timeless`, `/voices`, `/cluster_jewel`, `/corrupt`, `/item_mod`, `/item_max`, `/item_mods`, `/roll`, `/base_defence`, `/item_state`, `/rarity`, `/sockets` |
| Character | `/set_level`, `/add_xp`, `/gold`, `/add_passive_points`, `/add_refund_points`, `/add_passive`, `/reset_passives`, `/ascendancy`, `/ascendancy_points`, `/reset_ascendancy`, `/atlas_points`, `/waypoints`, `/quest_flag`, `/league_flag`, `/trials`, `/quest_preset` |
| Stats | `/stat`, `/set_stat`, `/set_speed`, `/set_aoe`, `/god`, `/charges`, `/auras`, `/resistances`, `/crit`, `/block`, `/accuracy`, `/defences`, `/suppression` |
| Test hits | `/hit`, `/spellhit`, `/blockhit`, `/blind`, `/heal`, `/death_screen`, `/block_luck`, `/suppression_luck` |
| Areas and objects | `/stash_place`, `/guild_stash_place`, `/stash_add`, `/stash`, `/wp_place`, `/wp_state`, `/transition`, `/map_portal`, `/portal_rotation`, `/cosmetic`, `/hideout_keys`, `/atlas`, `/atlas_tree`, `/lab_layout` |
| Trials of Ascendancy tuning | `/trial_spike_hit`, `/trial_spike_step`, `/trial_spike_anim`, `/trial_roomba_damage`, `/trial_saw_damage`, `/trial_dart_poison`, `/trial_lava` |
| Protocol | `/raw`, `/ui` |
| Campaign driver | `/state`, `/kill`, `/quest_preset` (LocalExile's `scripts/dev/campaign/`) |

**Top rolls.** `/item <name> max` gives the item as well rolled as it can be (`/item Mageblood max`,
`/item Vaal Regalia rare max`), and `/item_max` does the same to the item on the cursor. Every implicit and explicit
value is at the top of its range (a unique's own range where it has one), and armour gets the top base defence roll. On a
magic or rare item each affix also becomes its best tier for the item level; which affixes it has is still rolled. `/item`
makes an item at the character's level: for the best tiers on a low character, hold it, `/roll rare 86`, then `/item_max`.
A value that picks something stays (a Timeless Jewel's seed, the support gem of a Forbidden Shako). The best value is
the highest number, or the lowest where less is better: damage taken, costs, flask charges used, requirements, ailments
and curses on the character. That is told by the stat's name; for another value, `/item_mod set <mod> <values>`.

LocalExile's own dev scripts (the campaign driver, the level generator's tour loop, `client_control.py`) and Waypoint's
item crafter send these commands: install this mod for them.

Works with: 1.1.3 works with LocalExile v0.4.3-beta and v0.5.0-beta; 1.1.2 with v0.4.3-beta, the first with the client-table
API it reads (LocalExile/LocalExile#862). Mods reach what modules reach from v0.4.0-beta (LocalExile/LocalExile#815);
not v0.3.0-beta or earlier.
