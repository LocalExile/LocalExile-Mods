# Third Ring Slot

Gives every character the third ring slot — `enable_ring_slot_3`, the stat The Unseen Hand ascendancy passive grants —
for every class and league, without taking the ascendancy.

Install with Waypoint or copy this folder into `Mods/Binaries/thirdringslot`, then `/mod_enable thirdringslot`. The
server adds the stat whenever it rebuilds the character's stats (on login, an area change or a Gateway restart), and the
client shows the third ring socket beside the two ring slots.

No chat commands and no settings; it changes no items, quests or client files.

Take the ring out of the third slot before you disable or remove the mod: `/mod_disable thirdringslot` drops the stat, and
the client closes the slot, so a ring left there is out of reach until the mod is enabled again.

1.0.0 requires LocalExile v0.6.0-beta or later (the `uint` `Stat.*` API).