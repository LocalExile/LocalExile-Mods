# ai-operator

Operate one LocalExile character from an LLM's intents, through the client's own input, with the server judging
every step. This is dev/acceptance tooling for the `ai-operator` mod; it is not shipped game content.

The design is in [LocalExile `Agents/docs/dev/ai-operator.md`](https://github.com/LocalExile/LocalExile/blob/main/Agents/docs/dev/ai-operator.md)
(read §g for the routine/model split and §h for the acceptance run). Three layers, and this tool is the top one:

| Layer | Who | Does |
|---|---|---|
| Hands | `scripts/dev/client_control.py` (+ `client-act.js`) in the LocalExile checkout | walks, uses a skill, loots, interacts — through the client's real input; it never writes a game packet of its own |
| World model + gate | the `ai-operator` mod (`/aiop world`, `/aiop reach`, `/aiop item`, `/aiop why`) | the server's view of the world, and a verdict on each intent |
| Brain | this tool | decides one intent at a time, compiles it into primitives, records everything |

## Files

| File | Is |
|---|---|
| `hands.py` | the bridge client: one long-lived `client_control.py --stdio` process, line-delimited JSON in and out; heals the two shared-`data/` races (`data/stack.json` port re-pin, `data/clients/<session>.json` rebuild) and restores a minimised window |
| `gate.py` | the mod's chat commands: `world`, `reach`, `route`, `item`, `why`, `can_stand` |
| `routine.py` | the deterministic loop: the routine's opening moves and the compiler that turns one intent into primitives |
| `brain.py` | the model adapter (OpenAI-compatible), the intent schema, and a scripted brain for mechanics tests |
| `session.py` | the records: `intents.jsonl`, `actions.jsonl`, `refusals.jsonl`, `events.jsonl`, `snapshots.jsonl` |
| `run.py` | the CLI that ties them together |
| `world.py` | prints one compact world snapshot (the rig's eyes; use it before guessing what to do) |

## Running it

1. Start the stack and a **framed** client. `live.ps1 -Agent` starts the session client minimised, and a minimised
   client pumps no frames, so every click is dropped. Start one with a normal window instead:
   `/d/Python/python scripts/dev/nullifier.py --cwd "<root>/data/clients/<session>" --record "<root>/data/clients/<session>.json"
   --patch-port 21950 --login-port 21952 --renderer null --window normal`.
2. Start a model. Any OpenAI-compatible endpoint works:
   `F:/LocalAI/llamacpp/cuda/llama-server.exe -m F:/LocalAI/models/Qwen3.5-4B-Q4_K_M.gguf --host 127.0.0.1 --port 8123 -c 4096 -ngl 99`
3. Point the tool at it and run. `AI_OPERATOR_THINKING=0` matters for thinking models (Qwen3.5): with thinking on the
   whole context goes to `reasoning_content` and the answer never starts.

```bash
cd <mods>/tools/ai-operator
AI_OPERATOR_BASE_URL=http://127.0.0.1:8123/v1 \
AI_OPERATOR_MODEL=F:/LocalAI/models/Qwen3.5-4B-Q4_K_M.gguf \
/d/Python/python run.py --session lab --root "F:/local exile" --port-base 21950 \
    --brain openai --radius 1000 --max-rounds 60 --goal "..."
```

Useful flags: `--routine-only` (no model at all: a mechanics test), `--hunt <name>`, `--clear`, `--cell x,y`,
`--exit <index>`, `--until-area <id>`, `--no-loot`, `--port-base <n>` (required when another checkout shares `data/`).

Mechanics first, model second: `--routine-only --cell`, `--routine-only --exit 0 --until-area 1_1_2` and
`--brain scripted --hunt "Skulking Wretch"` prove the hands and the routine without spending a model call.

## What a run leaves behind

One directory per run, `runs/<YYYYmmdd-HHMMSS>-<brain>/`:

- `intents.jsonl` — one line per round: the intent JSON, whether the routine or the model produced it, and the area/pos
  it was decided from.
- `actions.jsonl` — one line per primitive: kind, arguments, `ok`, the client's own `order`, `elapsedMs`.
- `refusals.jsonl` — every gate refusal with its reason. **A refusal is the gate working**, not the run failing.
- `events.jsonl`, `snapshots.jsonl` — what happened between rounds, and what the world looked like.

`session.summary()` (printed at the end) counts rounds, actions, actions that moved the world, refusals and seconds.

Nothing but `session.py` writes those files, so a run can be read back exactly as it happened.

## What the tool will not do

- It never writes a game packet and never teleports the character `/tp` or `/goto` the brain's way. Those are the
  human's debug commands; a run that uses them must say so in its report as setup.
- It never guesses an id or a coordinate: every id in an intent comes from the snapshot the mod sent.
- It never stands in for a failed step. Two failures of the same kind hand the reason back to the model; three stop
  the run.
- A gate "no route" is **advice**: the server's walk grid is a capture, and the client is authoritative for its own
  walking, so the routine records the refusal and lets the client try (see `Agents/docs/dev/ai-operator.md` §g).
