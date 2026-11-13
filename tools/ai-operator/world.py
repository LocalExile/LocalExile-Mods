"""Print one world snapshot from the ai-operator gate, as JSON.

    python world.py --session lab --root "F:/local exile" --port-base 21950 --radius 400

This is the rig's eyes: it opens the same bridge the operator uses, pins the
stack's ports (two checkouts on one machine share data/stack.json, and another
live.ps1 overwrites it, which silently empties every chat reply), asks the gate
for the snapshot and prints it compactly.
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from gate import Gate, GateError  # noqa: E402
from hands import BridgeError, Hands  # noqa: E402


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", default="F:/local exile")
    parser.add_argument("--session", default="lab")
    parser.add_argument("--port-base", type=int, default=None)
    parser.add_argument("--radius", type=float, default=400.0)
    parser.add_argument("--full", action="store_true", help="print the whole snapshot")
    args = parser.parse_args()

    hands = Hands(args.root, args.session, port_base=args.port_base)
    try:
        with hands:
            gate = Gate(hands)
            world = gate.world(args.radius)
    except (BridgeError, GateError) as error:
        print(f"failed: {error}", file=sys.stderr)
        return 1

    if args.full:
        print(json.dumps(world, ensure_ascii=False, indent=2))
        return 0

    monsters = sorted(
        (
            {
                "id": m["id"],
                "name": m.get("name", m.get("variety", "?")),
                "dist": round(m["dist"], 1),
                "life": m.get("life"),
                "hostile": m.get("hostile"),
                "boss": m.get("boss"),
            }
            for m in world.get("monsters", [])
        ),
        key=lambda m: m["dist"],
    )
    ground = sorted(world.get("ground", []), key=lambda g: g["dist"])
    print(
        json.dumps(
            {
                "client": world.get("client"),
                "area": world.get("area"),
                "pos": world.get("pos"),
                "life": world.get("life"),
                "exits": world.get("exits"),
                "monsters": monsters[:12],
                "monstersTotal": len(monsters),
                "ground": ground[:12],
                "groundTotal": len(ground),
            },
            ensure_ascii=False,
        )
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
