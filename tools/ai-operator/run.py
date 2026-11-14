"""Run the operator: one character, one goal, one intent per round.

    python tools/ai-operator/run.py --session lab --goal "kill Hillock, take his drop, then walk to town" \
        --brain openai --model <id from the endpoint>

    python tools/ai-operator/run.py --session lab --routine-only --clear     # mechanics test, no model call

The loop is the one the design fixes: a compressed snapshot in, exactly one intent out, the previous intent's
result shown before the next one is asked for, at most a few primitives per intent, and every step recorded.
When a primitive fails twice the same way the loop stops handing the same thing to the hands and gives the
failure — with the state — to the model, which must choose something else; if the model asks for the same
intent a third time the run stops and says so rather than letting it grind.

Nothing here presses a key. The model never names a key or a packet: it names a target, and the routine asks
the gate whether that target can be used from where the character stands.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import time
from datetime import datetime

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from brain import BrainError, make_brain, validate          # noqa: E402
from gate import Gate, GateError                            # noqa: E402
from hands import BridgeError, Hands                        # noqa: E402
from routine import Options, Routine                        # noqa: E402
from session import Records                                 # noqa: E402

DEFAULT_ROOT = os.environ.get("LOCALEXILE_ROOT") or "F:/local exile"

# How many rounds in a row the routine may act before the model is asked again. The routine is the fast local
# loop; this keeps it from quietly driving the whole run (Agents/docs/dev/ai-operator.md §g).
ROUTINE_ROUNDS_MAX = 4


def parse_args(argv=None):
    p = argparse.ArgumentParser(description="Operate one LocalExile character from an LLM's intents.")
    p.add_argument("--root", default=DEFAULT_ROOT, help="the LocalExile checkout holding scripts/dev")
    p.add_argument("--session", default="lab", help="the bridge's client session name")
    p.add_argument("--goal", default="", help="what the run is for, in words; it is recorded and shown to the model")
    p.add_argument("--brain", default="openai", choices=("openai", "scripted"))
    p.add_argument("--routine-only", action="store_true", help="no model at all: only the deterministic loop")
    p.add_argument("--account", default=os.environ.get("AI_OPERATOR_ACCOUNT", ""))
    p.add_argument("--password", default=os.environ.get("AI_OPERATOR_PASSWORD", ""))
    p.add_argument("--character", default=os.environ.get("AI_OPERATOR_CHARACTER", ""))
    p.add_argument("--radius", type=float, default=120.0, help="how far the snapshot reaches, in cells")
    p.add_argument("--max-rounds", type=int, default=60)
    p.add_argument("--runs-dir", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "runs"))
    p.add_argument("--until-area", default="", help="stop once the character is in this area")
    p.add_argument("--exit", type=int, default=None, help="the routine's plan: leave through this exit index")
    p.add_argument("--cell", default="", help="the routine's plan: walk to x,y")
    p.add_argument("--hunt", default="", help="the routine's plan: go and kill a monster by name")
    p.add_argument("--clear", action="store_true", help="the routine's plan: fight what is here")
    p.add_argument("--heal", action="store_true",
                   help="dev setup: /heal to full life at the start and revive after a death (declared in the report)")
    p.add_argument("--no-loot", action="store_true", help="the routine's plan: do not pick ground items up")
    p.add_argument("--quiet", action="store_true")
    p.add_argument("--port-base", type=int, default=int(os.environ.get("AI_OPERATOR_PORT_BASE", "0")) or None,
                   help="this rig's stack port base (21950 for the shared-data rig); it keeps data/stack.json on "
                        "our ports, which the bridge needs to tell our chat replies from another checkout's")
    return p.parse_args(argv)


def plan_of(args) -> dict:
    plan = {}
    if args.exit is not None:
        plan["exit"] = args.exit
    if args.cell:
        x, y = args.cell.replace(",", " ").split()[:2]
        plan["cell"] = (int(x), int(y))
    if args.hunt:
        plan["hunt"] = args.hunt
    if args.clear:
        plan["clear"] = True
    if args.no_loot:
        plan["loot"] = False
    return plan


def main(argv=None) -> int:
    args = parse_args(argv)
    stamp = datetime.now().strftime("%Y%m%d-%H%M%S")
    brain_name = "routine" if args.routine_only else args.brain
    directory = os.path.join(args.runs_dir, f"{stamp}-{brain_name}")
    goal = args.goal or "mechanics test" if args.routine_only else (args.goal or "no goal given")
    records = Records(directory, args.session, goal, brain_name)
    gate_errors = 0

    hands = Hands(args.root, args.session, port_base=args.port_base)
    hands.start()
    try:
        if args.account and args.character and not hands.in_game():
            hands.login(args.account, args.password)
            hands.play(args.character)
            records.event("logged in", account=args.account, character=args.character)

        gate = Gate(hands)
        if args.heal:
            hands.chat("/heal", wait=2.0)
            records.event("healed to full life at the start (dev setup)")
            time.sleep(1.0)
        routine = Routine(hands, gate, records, Options(radius=args.radius))
        brain = None if args.routine_only else make_brain(
            args.brain, exit_index=args.exit if args.exit is not None else None)
        if brain is not None:
            records.event("brain ready", brain=getattr(brain, "name", args.brain),
                          base_url=getattr(brain, "base_url", None), model=getattr(brain, "model", None))

        plan = plan_of(args)
        last = None
        attempts: list[dict] = []
        repeats: dict[str, int] = {}
        routine_streak = 0
        round_no = 0
        # A client whose main loop has stopped drawing is not a client that "cannot find a path": its readbacks are
        # stale, so every press lands where things used to be. Checked before the first round rather than discovered
        # through a misleading walk failure ten seconds later.
        try:
            drawn = hands.frames()
            count = drawn.get("frames") if isinstance(drawn, dict) else int(drawn or 0)
            if not count:
                # Measured on 3.29.3c: the counter reads 0 even on a client that is drawing and walking, so it is a
                # note, not a gate. What it *is* good for is the log: a run that then fails every walk shows here first.
                print("note: the client's frame counter reads 0 (it does in this build even while healthy)",
                      file=sys.stderr)
                records.event("the client's frame counter reads 0", frames=count)
        except BridgeError as exc:
            records.event("could not read the client's frame counter", error=str(exc))

        while round_no < args.max_rounds:
            round_no += 1
            routine.round = round_no
            try:
                snapshot = routine.snapshot()
            except (GateError, BridgeError) as exc:
                gate_errors += 1
                records.event("snapshot failed", error=str(exc))
                # An area change takes the client out of the world for a moment ("Chat requires an active
                # area"), so a snapshot is retried rather than counted as a failure.
                if gate_errors >= 6:
                    print(f"the gate stopped answering: {exc}", file=sys.stderr)
                    break
                time.sleep(2.0)
                continue
            gate_errors = 0
            records.snapshot(round_no, snapshot)
            area = (snapshot.get("area") or {}).get("id")
            life = snapshot.get("life") or [0, 0]
            if int(life[0]) <= 0 and not any((i.get("next") or {}).get("kind") == "resurrect" for i in attempts[-1:]):
                # The client is waiting on its death panel; the routine clicks RESURRECT IN TOWN like a player
                # (routine.routine_step asks for it), and only a declared --heal falls back to the dev command when
                # that click did not take. /heal alone leaves the panel up, so it is never the first move.
                intent = {"goal": "the character is dead: click RESURRECT IN TOWN",
                          "next": {"kind": "resurrect"}}
                records.intent(round_no, intent, "routine", snapshot)
                result = routine.execute(intent, snapshot)
                records.action(round_no, "resurrect", {}, result, 0)
                if result.get("ok"):
                    continue
                if args.heal:
                    hands.chat("/heal", wait=2.0)
                    records.event("the death panel did not take the click; /heal (dev setup)", area=area)
                    time.sleep(1.0)
                    continue
                print(f"the character is dead and the panel did not take the click: {result.get('reason')}",
                      file=sys.stderr)
                break
            if args.until_area and area == args.until_area:
                records.event("goal area reached", area=area)
                break

            source = "routine"
            intent = None
            if args.routine_only or brain is None:
                intent = routine.routine_step(snapshot, plan)
                if intent is None:
                    records.event("the routine has nothing to do", area=area, plan=plan)
                    break
            else:
                # The routine owns whatever is a pure function of the snapshot: fighting off what is standing on
                # top of the character, picking up what is underfoot, and walking a plan's corners. It yields to
                # the model after a few rounds in a row, so the model still does the deciding.
                routine_intent = routine.routine_step(snapshot, plan)
                if routine_intent is not None and routine_streak < ROUTINE_ROUNDS_MAX:
                    intent = routine_intent
                    source = "routine"
                    routine_streak += 1
                    if routine_streak == 1:
                        records.event("the routine can act", intent=routine_intent.get("next"), area=area)
                else:
                    source = "model"
                    routine_streak = 0
                    try:
                        intent = brain.decide(snapshot, goal, last, attempts)
                    except BrainError as exc:
                        records.event("the model gave no usable intent", error=str(exc))
                        print(f"the model failed: {exc}", file=sys.stderr)
                        break

            ok, why = validate(intent)
            if not ok:
                records.event("the model's intent was rejected", error=why, intent=intent)
                last = {"ok": False, "reason": f"your intent was rejected: {why}"}
                continue

            key = json.dumps(intent.get("next"), sort_keys=True)
            records.intent(round_no, intent, source, snapshot)
            attempts.append(intent)
            kind = (intent.get("next") or {}).get("kind")
            args_of_next = intent.get("next") or {}
            if not args.quiet:
                print(f"[{round_no}] {source}: {json.dumps(args_of_next, ensure_ascii=False)} "
                      f"area={area} pos={snapshot.get('pos')}")

            if kind == "stop":
                records.event("the run stopped", reason=args_of_next.get("reason"))
                break

            result = routine.execute(intent, snapshot)
            last = {"intent": args_of_next, "ok": result["ok"], "reason": result["reason"],
                    "primitives": result["primitives"], "pos": snapshot.get("pos")}
            if not args.quiet:
                print(f"      -> ok={result['ok']} {result['reason']}")
            if not result["ok"]:
                # A repeat only counts when nothing moved. An intent that fails but leaves the character closer is
                # progress, not a loop: the walk towards a walled boss takes several rounds by nature, and stopping
                # after two of them ends the run before the corridor is walked.
                moved = snapshot.get("pos") != (last.get("pos") if isinstance(last, dict) else None)
                if moved and isinstance(last, dict):
                    last = dict(last)
                    last["pos"] = snapshot.get("pos")
                repeats[key] = repeats.get(key, 0) + (0 if moved else 1)
                if repeats[key] >= 2 and (args.routine_only or brain is None):
                    records.event("the routine cannot do this", intent=args_of_next, reason=result["reason"])
                    break
                if repeats[key] >= 3:
                    records.event("the same intent failed three times", intent=args_of_next,
                                  reason=result["reason"])
                    print("stopping: the same intent failed three times", file=sys.stderr)
                    break
            else:
                repeats.pop(key, None)
        summary = records.summary()
        print(json.dumps(summary, ensure_ascii=False))
        return 0
    finally:
        hands.close()


if __name__ == "__main__":
    raise SystemExit(main())
