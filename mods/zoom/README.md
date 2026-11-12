# Zoom

A wider view in normal play: the camera stands further back, everywhere, and the mouse wheel keeps working. Turned all
the way out the wheel now reaches the wider view; turned in it comes as close as before.

On once installed, at level 1.6. Level 1 is the game's own view; 3 is the furthest.

## Commands

- `/zoom`: the level now, and what can be typed. It also sets the view again, for when a boss's own camera script has reset it.
- `/zoom 2`: a level from 1 to 3.
- `/zoom out`, `/zoom in`: one step further back or closer.
- `/zoom off`: the game's own view (level 1).
- `/zoom default`: back to the server's level (the setting).

A player's level is their own: it is kept with their account and used in every area.

| Setting | Default | |
|---|---|---|
| `level` | 1.6 | the level for players who have not picked one (1 to 3) |
| `step` | 0.2 | how much `/zoom out` and `/zoom in` change it |

## How it works, and what to expect

The game has camera zoom points of its own: places where it pulls the view back, as at the Beacon's lighthouse or in
some boss rooms. The mod adds one more to each area, in a far corner and reaching all of it. No client file is changed.

- **The game's own zoom points still work.** The client uses the nearest point that reaches the player, so inside one of
  the game's own the view is the one the game sets there, which can be closer than the mod's level.
- A few boss scripts clear every zoom point when their scene ends. The wider view is then gone until the next area;
  `/zoom` brings it back at once.
- Monsters far from the player are asleep on the client as in the real game, so at high levels the edge of the view can
  show them standing still, or not at all, until the player comes closer.
- After `/mod_disable zoom` the view returns to the game's own in the next area.

Works with: 1.0.0 needs LocalExile v0.5.1-beta or later.
