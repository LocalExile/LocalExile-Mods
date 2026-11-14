# POB

Optional Path of Building import front end for LocalExile. It is a normal global mod: `IMod`, `mod.json`, host-managed settings and hot reload. It uses the public SDK and Gateway `GameEvents`; it is absent from server modules, league lists and normal server releases.

The strict converter requires the companion LocalExile server update. The manifest lists 0.5.1-beta as a placeholder until that update ships; the unchanged frontend contract remains compatible with that SDK.

## Installation and commands

Install with Waypoint after the required server update, or build `Pob.csproj` with `-p:LocalExileRoot=<checkout>` and put its output in `Mods/Binaries/pob/`.

- `/pob <code>`: prepare a build using the configured external converter.
- `/pob file build.txt`: read a longer code from the operator-managed `data/pob-import/inbox/`.
- `/pob file build.json`: read a native converter input snapshot or a prepared version-1 result from that inbox. A prepared result needs no converter executable.
- `/pob confirm` or ordinary chat `Y`: accept the current preview within five minutes. After the three-second countdown, the server closes the old session, finishes its writers and atomically replaces the build. Re-enter the character afterward.
- `/pob cancel` or ordinary chat `N`: cancel. Whispers are never consumed as confirmation. A new request, reload or disable also retires an unaccepted preview.
- `/mod_disable pob`, `/mod_enable pob`, `/mod_reload pob`: standard host lifecycle commands.

Native chat still limits typed text to 512 UTF-16 units. Inbox filenames are simple leaf names, limited to .txt/.json; files are capped at 2 MiB and symbolic-link files are rejected. The inbox is managed by the server operator, not an upload endpoint.

## Settings

Declare settings in `settings.json`; personal overrides live in `data/mods/pob.json`, managed by Waypoint or the host. Settings changes reload the mod.

- `converterExecutable`: path to a separately installed, trusted converter executable. Empty by default; raw code conversion then reports a configuration error.
- `converterArguments`: fixed argument array for that executable. Player input is JSON on stdin, never a command or shell argument.

Existing module users can copy their chosen converter settings from `data/modules/pob.json` to the mod override after checking them. There is no implicit migration or bundled converter. The server remains responsible for validation, character ownership, confirmation expiry, admission exclusion and atomic replacement.

## Converter contract

The executable receives one UTF-8 JSON request on stdin:

```json
{ "Version": 1, "Code": "<share code>", "Name": "<current character>", "Data": "<absolute server data folder>", "Record": { "...": "current native character record" }, "SourceFile": null }
```

For a native input snapshot, `SourceFile` is its validated inbox path. The converter returns one UTF-8 JSON document:

```json
{ "Version": 1, "Record": { "...": "same identity, desired class and level" }, "Save": { "Inventories": {}, "Experience": 0, "Passives": [] }, "Summary": "Preview text", "Warnings": [] }
```

`Record` and `Save` follow LocalExile's current native character/save JSON schema. This versioned transport does not promise that a converter targeting another server save schema is compatible. Keep character/account IDs, name and league unchanged. The server accepts build inventory rows 1-11, 14, 15, 23 and 102; at most 1,024 materializable native items, level 1-100, summary up to 800 characters, and at most 100 warnings of up to 800 characters each. The server independently checks the whole result before returning a preview.

There is one converter process at a time, a 90-second timeout, an 8 Mi-character stdout limit and an 8 Ki-character stderr limit. Timeout, unload, malformed output, identity mismatch or conversion failure leave the old build intact. No account credentials or writable save database are passed to the process. This is trusted operator software, not an operating-system sandbox.

Import replaces class, level, experience, passives/masteries/tattoos, main and secondary ascendancy, equipment, swapped weapons, flasks, gems and jewels. It preserves the latest quest/waypoint progress, other inventories, cosmetics and unrelated module state. An exported bandit choice updates only its module-owned quest rewards. Old aura/stance/golem intent and derived vitals are cleared; the server retires stale inventory writers and gem/equipment identities after a successful commit. Already accepted imports finish under server ownership even if the mod then unloads.

No converter executable, client changes, credentials or game assets ship with the frontend release.

## Strict converter source

`Converter/PobImport.csproj` builds the separate, operator-configured .NET 10 converter from source.
It requires the companion LocalExile update for native item properties and game-owned import validation.
The compatibility version in the manifest is a placeholder pending that server update.
The frontend release does not automatically build, install or run this source project.

From this repository root, a build for an existing LocalExile checkout is:

```powershell
dotnet build mods/pob/Converter/PobImport.csproj -c Release -p:LocalExileRoot=C:/LocalExile
```

`converterExecutable` points to the resulting `PobImport.exe` on Windows, or the platform's generated
`PobImport` executable. The converter process runs from the LocalExile checkout, with its `data/client.db`
and read-only `data/world.db`. It returns the version-1 prepared build; it never opens a character save store.
The separate converter's upstream source attribution and license are in `Converter/NOTICE.txt` and `Converter/LICENSE`.

The converter preserves native modifiers, physical gem sockets, inactive gems, influences, alternate quality,
Intangibility, Memory Strands, foil/vestigial state, level requirements, ordinary/cluster passives,
secondary ascendancy, bandit intent and native passive overrides. Unresolvable item/gem/passive data fails
preparation instead of producing a partial character or substituting maximum rolls.
Native properties and selected effects being retained does not establish complete combat parity.

PoB text sometimes omits raw affix rolls, physical gem positions or transformed item state.
`/pob file build.json` accepts the original native poe.ninja snapshot, including its matching `pob` code,
for those cases. An optional operator-maintained `data/pob-import/source-index.json` maps the SHA-256
of a complete share code to that snapshot's path. An optional `native-items.db` supplies exact item
fingerprints for items retained across edited exports. A same-UID item with different text/rolls does not
reuse cached native rolls. Neither snapshots nor indexes, client tables, game files or personal builds
ship with the mod. Existing `converterExecutable` settings and raw-code/file/confirmation commands remain valid.
