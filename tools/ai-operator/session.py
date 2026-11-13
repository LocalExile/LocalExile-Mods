"""The run's records: the three things the acceptance has to be able to read afterwards.

Nothing else in this tool writes these files. Every decision the model made, every primitive the hands
executed (with its duration and the client's own answer), and every refusal the gate handed back land here, in
time order, so a stalled run can be told apart as "the hands failed", "the gate refused" or "the model chose
wrong" without re-running it.

A refusal is not a failure: it is the gate doing its job. It is kept in its own file because the acceptance
requires at least one, and because a refusal followed by a different approach is the loop working as designed.
"""

from __future__ import annotations

import json
import os
import time
from datetime import datetime, timezone


class Records:
    def __init__(self, directory: str, session: str, goal: str, brain: str):
        self.dir = directory
        os.makedirs(self.dir, exist_ok=True)
        self.session = session
        self.goal = goal
        self.brain = brain
        self.started = time.monotonic()
        self.counts = {"intent": 0, "action": 0, "refusal": 0, "event": 0, "snapshot": 0}
        self._files = {}
        self.event("run started", session=session, goal=goal, brain=brain, cwd=os.getcwd())

    # ---- the shapes ------------------------------------------------------
    def _append(self, name: str, row: dict) -> dict:
        path = os.path.join(self.dir, f"{name}s.jsonl")
        row = {"at": datetime.now(timezone.utc).astimezone().isoformat(timespec="milliseconds"),
               "t": round(time.monotonic() - self.started, 3), **row}
        with open(path, "a", encoding="utf-8") as fh:
            fh.write(json.dumps(row, ensure_ascii=False) + "\n")
        self.counts[name] = self.counts.get(name, 0) + 1
        return row

    def intent(self, round_no: int, intent: dict, source: str, snapshot: dict | None = None) -> dict:
        """One decision: what the model (or, in a mechanics test, the scripted brain) chose, and why it was
        asked. The snapshot is the exact picture the decision was made from."""
        return self._append("intent", {"round": round_no, "source": source, "intent": intent,
                                       "area": (snapshot or {}).get("area", {}).get("id"),
                                       "pos": (snapshot or {}).get("pos"),
                                       "trigger": (snapshot or {}).get("_trigger")})

    def action(self, round_no: int, kind: str, arguments: dict, result: dict, ms: int) -> dict:
        order = result.get("order") if isinstance(result, dict) else None
        return self._append("action", {"round": round_no, "kind": kind, "arguments": arguments,
                                       "ok": bool(result.get("ok", True)) if isinstance(result, dict) else False,
                                       "order": order, "elapsedMs": ms,
                                       "reason": (result or {}).get("reason") or (result or {}).get("error")})

    def refusal(self, round_no: int, kind: str, arguments: dict, reason: str, answer: dict | None = None) -> dict:
        return self._append("refusal", {"round": round_no, "kind": kind, "arguments": arguments,
                                        "reason": reason, "answer": answer})

    def event(self, what: str, **fields) -> dict:
        return self._append("event", {"what": what, **fields})

    def snapshot(self, round_no: int, snapshot: dict) -> dict:
        return self._append("snapshot", {"round": round_no, "snapshot": snapshot})

    def summary(self) -> dict:
        accepted = refused = 0
        actions_path = os.path.join(self.dir, "actions.jsonl")
        refusals_path = os.path.join(self.dir, "refusals.jsonl")
        for path, key in ((actions_path, "ok"), (refusals_path, None)):
            if not os.path.exists(path):
                continue
            with open(path, encoding="utf-8") as fh:
                for line in fh:
                    if key is None:
                        refused += 1
                    elif json.loads(line).get(key):
                        accepted += 1
        return {"dir": self.dir, "session": self.session, "goal": self.goal, "brain": self.brain,
                "rounds": self.counts.get("intent", 0), "actions": self.counts.get("action", 0),
                "actionsOk": accepted, "refusals": refused, "seconds": round(time.monotonic() - self.started, 1)}
