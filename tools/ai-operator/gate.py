"""The gate: `/aiop` over the chat channel, parsed back into dictionaries.

The gate is the server's answer to the questions an outside operator would otherwise guess at: what is around
me, can I walk there, is that item in reach, and why can I not use that target right now. It never changes the
world — every command behind it is read-only — so calling it is always free of consequences, and the routine
must call it *before* acting rather than after failing.

Replies arrive on chat lines: the first starts with `AI `, the continuations with `AI+ `. A reply may also be
the mod's usage line, which is not JSON; that is reported as a gate error rather than parsed.
"""

from __future__ import annotations

import json

PREFIX = "AI "
CONTINUATION = "AI+ "


class GateError(RuntimeError):
    """The gate did not answer with JSON (the mod is not loaded, the command name collided, bad arguments)."""


class Gate:
    def __init__(self, hands, wait: float = 2.0):
        self.hands = hands
        self.wait = wait
        self.calls = 0

    # ---- the wire --------------------------------------------------------
    def _ask(self, text: str) -> dict:
        self.calls += 1
        replies = self.hands.chat(text, wait=self.wait)
        chunks = []
        for line in replies:
            if line.startswith(CONTINUATION):
                chunks.append(line[len(CONTINUATION):])
            elif line.startswith(PREFIX):
                chunks.append(line[len(PREFIX):])
        if not chunks:
            raise GateError(f"{text!r} answered no AI line (is ai-operator loaded? replies={replies!r})")
        body = "".join(chunks)
        try:
            return json.loads(body, strict=False)
        except json.JSONDecodeError as exc:
            raise GateError(f"{text!r} answered something that is not JSON: {body[:200]!r}") from exc

    # ---- the four questions ---------------------------------------------
    def world(self, radius: float = 60) -> dict:
        return self._ask(f"/aiop world {radius:g}")

    def reach(self, x: int, y: int) -> dict:
        return self._ask(f"/aiop reach {int(x)} {int(y)}")

    def item(self, object_id: int) -> dict:
        return self._ask(f"/aiop item {int(object_id)}")

    def why(self, object_id: int) -> dict:
        return self._ask(f"/aiop why {int(object_id)}")

    # ---- helpers the routine uses ---------------------------------------
    def route(self, x: int, y: int) -> tuple[list[tuple[int, int]], str]:
        """The corners of the server's own walk route to a cell, and why there is none.

        A click only resolves within a screen radius, so a far goal has to be walked in stages. The gate answers
        with the route its walk grid knows, which is how the character goes around a headland instead of into it.
        An empty list means the gate sees no route: the client keeps its own grid and may still get there, so the
        caller should fall back to aiming straight at the cell.
        """
        answer = self.reach(x, y)
        raw = answer.get("waypoints") or []
        route = [(int(p[0]), int(p[1])) for p in raw if isinstance(p, (list, tuple)) and len(p) == 2]
        if not route and answer.get("reachable"):
            at = answer.get("standAt")
            route = [(int(at[0]), int(at[1]))] if isinstance(at, (list, tuple)) else [(int(x), int(y))]
        return route, str(answer.get("blockedBy") or "")

    def can_stand(self, x: int, y: int) -> tuple[bool, str, tuple[int, int]]:
        """(can get near it, why not, the cell to aim at)."""
        answer = self.reach(x, y)
        if answer.get("reachable"):
            at = answer.get("standAt")
            aim = (int(at[0]), int(at[1])) if isinstance(at, (list, tuple)) else (int(x), int(y))
            return True, "", aim
        return False, str(answer.get("blockedBy") or "not reachable"), (int(x), int(y))
