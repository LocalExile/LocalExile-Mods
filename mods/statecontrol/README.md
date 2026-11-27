# State Control

Chat commands for testing and practice: keep a character and the stash as they are, change anything, and put them back.
Also: fill the stash's special tabs with everything they hold, add and remove tabs of any kind, empty the stash, and make
a level 100 character of every ascendancy.

| Command | Does |
|---|---|
| `/save_character` | Keeps the character you are playing as it is now: level and experience, passives, every item it carries or wears, quests, waypoints. One state per character; saving again replaces it. |
| `/save_stash` | Keeps every tab of this league's stash as it is now. One state per league's stash. |
| `/load_saves` | Puts back the saved stash and every saved character of the account. The character you are playing is loaded by returning you to the character selection; a character that is in game on another client is left alone. |
| `/load_saves undo` | Puts the stash back as it was before the last `/load_saves`, `/clear_stash` or `/remove_tab`. |
| `/clear_stash confirm` | Empties every tab of this league's stash. The tabs themselves and the gold stay. |
| `/fill_stash [amount]` | Fills each special tab with what it holds: `amount` (the `fillAmount` setting without it) in each slot of the Currency, Fragment, Essence, Divination, Delve, Blight and Delirium tabs, and one of each flask in its tab. Uniques go into the unique tab as they roll, and into two unique tabs it adds, `Unique worst rolls` and `Unique best rolls`, with every value at its worst or its best: the best is the highest number, or the lowest where less is better (damage taken, costs, requirements), as gm-commands' `/item_max` has it. Gems go into eight gem tabs it adds, named for what is in them: `Red gems`, `Red gems 20/20` (level 20, 20% quality), and the same for green, blue and white. What is already there stays. The Map, Breach and Ultimatum tabs are left alone: LocalExile does not show items in them yet. |
| `/add_tab <type> [count] [name]` | Adds stash tabs of any kind to this league's stash: `normal`, `premium`, `quad`, `currency`, `map`, `fragment`, `essence`, `divination`, `unique`, `delve`, `blight`, `ultimatum`, `delirium`, `flask`, `gem`, `breach`; 1 to 50 at a time, named `name` (or the kind) with a number when there are several. `/add_tab list` shows the kinds. Folders are made in the stash itself: a tab's settings, Folder, Create New Folder. |
| `/remove_tab <name>` | Removes the tabs of that name, with what is in them. A Map or Unique tab's pages go with it; a folder's tabs move out of it. `/load_saves undo` puts them back. |
| `/remove_tab all confirm` | Removes every tab of this league's stash, with what is in them, and leaves one empty premium tab named `Stash`. `/load_saves undo` puts them back. |
| `/make_characters [campaign]` | Makes one level 100 character of each ascendancy on the account (21, so the account needs that many free places), named `<Ascendancy>_100`, with its class's start items, the passive points of the campaign's quests and all eight ascendancy points to spend. With `campaign` they stand at the end of the story instead: the quest flags of the game's own epilogue start state, every waypoint, in Karui Shores. |

A state is kept in the save database, beside the saves, so it lasts across restarts. A loaded stash puts back only its
tabs and their items: the league's gold, its quest flags (the trials every character shares) and other mods' data stay as
they are now. The stash's change shows at once.

**Loading the character you play.** A character in game cannot be written over: its connection saves once more when it
ends. `/load_saves` therefore ends the connection, and the state goes in after that last save, at LocalExile's
`CharacterAtRest` event, before you select the character again. When that last save fails, or the connection takes more than two
minutes to end, the character is left as it is and the log says so: run `/load_saves` again.

## Settings

| Setting | Default | Does |
|---|---|---|
| `fillAmount` | 1000 | How many of each item `/fill_stash` puts in a slot. |
| `questPassivePoints` | 24 | The passive points `/make_characters` gives for the campaign's quests (22 from quests, 2 for killing the bandits). |

Works with LocalExile 0.6.0-beta and 0.6.1-beta: it needs `Player` (0.6.0-beta), the `CharacterAtRest` event
([#1172](https://github.com/LocalExile/LocalExile/pull/1172)) and stash tab removal
([#1170](https://github.com/LocalExile/LocalExile/pull/1170)).
