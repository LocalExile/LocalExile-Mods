"""The routine: what can be decided from the snapshot alone never waits for a model.

Two jobs live here.

*Compiling an intent*: a model (or the scripted brain) says "engage the monster with id 134217965"; this module
turns that into the primitives a character can actually perform — ask the gate whether the target can be used
from here, walk to the cell the gate says to stand on when it cannot, then swing — and stops after a few
primitives rather than looping forever. The model never sees a keystroke; the hands never see a judgement.

*The routine loop*: for a goal whose next step is a pure function of the snapshot (clear the area, pick up
drops, walk to a cell, take an exit) this module can act with no model call at all. `routine_step` returns the
intent it would send, so the caller decides whether to execute it directly or hand over to the model.

Every primitive and every refusal goes through `Records`, which is the only writer of the run's files.
"""

from __future__ import annotations

import os
import time
from dataclasses import dataclass, field


@dataclass
class Options:
    radius: float = 120.0        # how far around the character the snapshot reaches, in cells
    walk_tolerance: int = 6      # how close a walkTo has to get, in cells
    walk_timeout: float = 60.0   # seconds one walkTo may take
    max_primitives: int = 5      # primitives one intent may cost before it is reported back
    swing_reach: int = 4         # how many cells short of a target "walk closer" stops (the gate refines this)
    engage_within: int = 10      # a hostile this close is fought without asking the model (self-defence)
    crowd: int = 3               # this many hostiles on top of the character and it backs off instead
    avoid_ratio: float = 3.0     # a monster whose life is this many times the character's is not fought
    retreat_cells: int = 60      # how far back "back off" aims, before it is tried shorter
    extras: dict = field(default_factory=dict)


class Routine:
    def __init__(self, hands, gate, records, opts: Options | None = None):
        self.hands = hands
        self.gate = gate
        self.records = records
        self.opts = opts or Options()
        self.round = 0
        self.primitives = 0

    # ---- watching --------------------------------------------------------
    def snapshot(self) -> dict:
        """The server's world model, plus what the client claims, so the two can be compared in one record."""
        # A client that stopped drawing frames still answers the chat channel, so the world model would look
        # healthy while every click is dropped. Check the frame loop first, once per round.
        self.hands.ensure_frames()
        world = self.gate.world(self.opts.radius)
        try:
            status = self.hands.status()
        except Exception as exc:  # a bridge that died is part of the picture, not a crash here
            status = {"error": str(exc)}
        world["client"] = {k: status.get(k) for k in ("screen", "area", "instanceState", "pid")}
        return world

    # ---- the routine loop ------------------------------------------------
    def routine_step(self, snapshot: dict, plan: dict | None = None) -> dict | None:
        """The next intent if it follows from the snapshot alone, else None (the model must decide).

        `plan` names what the run wants: `{"exit": 0}` to leave through an exit, `{"cell": [x, y]}` to walk
        somewhere, `{"hunt": "Hillock"}` to go and kill a named monster, `{"clear": True}` to fight what is here.
        """
        plan = plan or {}
        if (snapshot.get("life") or [1, 1])[0] <= 0:
            # The client is showing its death panel and waiting for a click, so the routine can do what a player does:
            # press RESURRECT IN TOWN. /heal is not a substitute (it restores the server's life and leaves the panel
            # up, after which every press goes to a dead character and nothing reaches the gateway).
            return {"goal": "the character is dead: click RESURRECT IN TOWN",
                    "next": {"kind": "resurrect"}}

        if plan.get("cell"):
            x, y = plan["cell"]
            here = snapshot.get("pos") or [0, 0]
            if abs(here[0] - x) <= self.opts.walk_tolerance and abs(here[1] - y) <= self.opts.walk_tolerance:
                return None                      # arrived: the plan needs a new decision, i.e. the model
            return {"goal": "walk to the cell", "next": {"kind": "goto", "x": int(x), "y": int(y)}}

        hunting = (plan.get("hunt") or "").lower()
        monsters = [m for m in snapshot.get("monsters") or [] if m.get("hostile") and not m.get("dead")]
        if hunting:
            wanted = [m for m in monsters if hunting in str(m.get("name", "")).lower()]
            if wanted:
                nearest = min(wanted, key=lambda m: m.get("dist", 1e9))
                return {"goal": f"kill {nearest.get('name')}",
                        "next": {"kind": "engage", "target": nearest["id"]}}
            # Nothing named like the target is in sight: walk to the place the plan names, if it names one.
            if plan.get("cell"):
                return {"goal": "look for the target",
                        "next": {"kind": "goto", "x": int(plan["cell"][0]), "y": int(plan["cell"][1])}}
            return None

        if plan.get("clear") and monsters:
            nearest = min(monsters, key=lambda m: m.get("dist", 1e9))
            return {"goal": "clear what is here", "next": {"kind": "engage", "target": nearest["id"]}}

        # Self-defence is a pure function of the snapshot: a hostile standing on top of the character is not a
        # decision the model has to make (a person swats what is biting them without asking). Two rules keep it
        # from being a death sentence: a badly hurt character backs off instead of trading blows, and it does not
        # pick a fight with a crowd. The run only lets this happen a few rounds in a row before the model is asked.
        life = snapshot.get("life") or [1, 1]
        hurt = life[1] and life[0] * 2 <= life[1]
        close = [m for m in monsters if float(m.get("dist") or 1e9) <= self.opts.engage_within]
        if close:
            nearest = min(close, key=lambda m: m.get("dist", 1e9))
            ours = float(life[1] or 1)
            outmatched = float(nearest.get("maxLife") or 0) > self.opts.avoid_ratio * ours
            if hurt or outmatched or len(close) >= self.opts.crowd:
                away = self._back_off(snapshot, nearest)
                if away is not None:
                    why = "hurt" if hurt else ("outmatched" if outmatched else "outnumbered")
                    return {"goal": f"back off from the {nearest.get('name')} ({why}; life {life[0]}/{life[1]})",
                            "next": {"kind": "goto", "x": away[0], "y": away[1]}}
            return {"goal": f"fight off the {nearest.get('name')} on top of me",
                    "next": {"kind": "engage", "target": nearest["id"]}}

        ground = snapshot.get("ground") or []
        if ground and plan.get("loot", True):
            nearest = min(ground, key=lambda g: g.get("dist", 1e9))
            return {"goal": "take what is on the ground", "next": {"kind": "loot", "object": nearest["id"]}}

        if plan.get("exit") is not None:
            exits = snapshot.get("exits") or []
            row = next((e for e in exits if e.get("index") == plan["exit"]), None)
            if row:
                return {"goal": "leave through the exit", "next": {"kind": "take", "exit": plan["exit"]}}
        return None

    # ---- executing an intent --------------------------------------------
    def execute(self, intent: dict, snapshot: dict) -> dict:
        """One intent, at most `max_primitives` primitives. Returns {"ok", "reason", "primitives"}."""
        self.primitives = 0
        kind = (intent.get("next") or {}).get("kind")
        try:
            if kind == "stop":
                return self._done(True, str((intent.get("next") or {}).get("reason") or "the model said stop"))
            if kind == "wait":
                return self._done(True, "waited")
            if kind == "engage":
                return self._engage(intent["next"]["target"], snapshot)
            if kind == "loot":
                return self._loot(intent["next"]["object"], snapshot)
            if kind == "goto":
                return self._goto(int(intent["next"]["x"]), int(intent["next"]["y"]))
            if kind == "take":
                return self._take(int(intent["next"]["exit"]), snapshot)
            if kind == "resurrect":
                return self._resurrect(snapshot)
            return self._done(False, f"unknown intent kind {kind!r}")
        except Exception as exc:  # a bridge or gate failure is a reported failure, never a silent retry
            self.records.event("intent raised", error=str(exc), kind=kind)
            return self._done(False, f"{type(exc).__name__}: {exc}")

    # ---- the primitives --------------------------------------------------
    def _walk(self, x: int, y: int, tolerance: int | None = None) -> dict:
        result = self.hands.act("walkTo", x=int(x), y=int(y),
                                tolerance=self.opts.walk_tolerance if tolerance is None else tolerance,
                                timeout=self.opts.walk_timeout)
        self.primitives += 1
        self.records.action(self.round, "walkTo", {"x": int(x), "y": int(y)}, result,
                            int(result.get("elapsedMs") or 0))
        return result

    def _back_off(self, snapshot: dict, monster: dict) -> list[int] | None:
        """A cell away from a monster the character should not be trading blows with, or None if there is none.

        The direction is the line from the monster through the character, so the cell is "behind" the character.
        It is asked of the gate (a route exists, and the cell can be stood on) rather than assumed, because a wall
        behind the character is exactly where a straight-line retreat would aim.
        """
        pos = snapshot.get("pos") or [0, 0]
        mx, my = monster.get("pos") or pos
        dx, dy = pos[0] - mx, pos[1] - my
        span = max(1.0, (dx * dx + dy * dy) ** 0.5)
        for distance in (self.opts.retreat_cells, 40, 25, 12):
            x = int(round(pos[0] + dx / span * distance))
            y = int(round(pos[1] + dy / span * distance))
            if abs(x - pos[0]) + abs(y - pos[1]) < 4:
                continue
            try:
                ok, _why, aim = self.gate.can_stand(x, y)
            except Exception:
                continue
            if ok and aim:
                return [int(aim[0]), int(aim[1])]
        return None

    def _walk_near(self, x: int, y: int, kind: str, arguments: dict, tolerance: int | None = None) -> tuple[dict, dict]:
        """Walk as close to a cell as the gate allows, along the route the gate's walk grid knows.

        The gate answers from the *server's* walk grid. The client keeps its own grid and is authoritative for
        its own walking — the two are known to disagree (a town corridor the server's capture calls a wall is
        walkable for the client) — so a gate "no route" does not stop the run: it is recorded as an override and
        the client is allowed to try. What the gate is trusted for is the arithmetic it owns, the pickup range
        and the attack reach, which is where it stops a target forty cells away from being clicked at.

        The walk itself follows the gate's own corners rather than a straight line to the goal. A click only
        reaches about fifteen cells, and a straight line over a headland or a stretch of water is a run of clicks
        the client refuses one by one — measured on `1_1_1`: a direct walk to a monster 79 cells away moved the
        character six cells in five clicks; the same walk along the route's corners arrived. Each corner is its
        own recorded primitive, and one intent stops after `max_primitives` of them.
        """
        ok, reason, aim = self.gate.can_stand(x, y)
        overruled = None
        if not ok:
            overruled = reason
            aim = (int(x), int(y))
            self.records.event("the gate has no route; the client is trying anyway",
                               kind=kind, arguments=arguments, gate=reason)
            walked = self._walk(*aim, tolerance=tolerance)
            return walked, {"gateOverruled": True, "gateReason": overruled, "aim": list(aim)}

        aim = (int(aim[0]), int(aim[1]))
        route, blocked = self.gate.route(*aim)
        if not route:
            walked = self._walk(*aim, tolerance=tolerance)
            return walked, {"gateOverruled": False, "gateReason": blocked or None, "aim": list(aim)}

        here = self.hands.position()
        walked = {"reached": False, "reason": "no corner was walked yet", "cell": list(here) if here else None}
        for index, cell in enumerate(route):
            if self.primitives >= self.opts.max_primitives:
                walked = {"reached": False, "cell": list(here) if here else None,
                          "reason": f"the walk reached corner {index} of {len(route)}; "
                                    "the next snapshot is needed to plan the rest"}
                break
            if here and max(abs(here[0] - cell[0]), abs(here[1] - cell[1])) <= self.opts.walk_tolerance:
                continue                                  # already standing on this corner
            walked = self._walk(*cell, tolerance=tolerance)
            got = walked.get("cell")
            if isinstance(got, (list, tuple)) and len(got) == 2:
                here = (int(got[0]), int(got[1]))
            if not walked.get("reached"):
                walked = dict(walked)
                walked["reason"] = (f"{walked.get('reason') or 'the walk stopped short'} "
                                    f"(corner {index + 1} of {len(route)})")
                break
        return walked, {"gateOverruled": False, "gateReason": blocked or None, "aim": list(aim),
                        "corners": len(route)}

    def _approach(self, at, what: str, snapshot: dict | None = None) -> tuple[bool, str, dict]:
        """A cell the character can stand on to reach `at`, or why the gate says there is none.

        A monster stands on the cell the character wants to hit, so "walk onto the target's cell" is a route the gate
        refuses on purpose — it will not walk onto a body, and it answers `steps: 0`. What it can name is a cell
        beside it, so the ring around the target is asked about nearest first. When nothing in the ring is reachable
        the caller has to *report* that: pressing on anyway is what walked 4 clicks into a wall 34 cells short.
        """
        x, y = int(at[0]), int(at[1])
        reachable, blocked, aim = self.gate.can_stand(x, y)
        if reachable:
            return True, "", {"x": aim[0], "y": aim[1], "how": "the gate's own stand cell"}
        # `reachable: false` does not mean "no route": when a monster *stands on* the goal cell the gate refuses the
        # last step but still answers with the corners it would have walked (`steps: 3`). Walking to the last corner is
        # how the character gets in range of a boss standing in a walled arena, which no ring around it can reach.
        route, _why = self.gate.route(x, y)
        if route:
            corner = route[-1]
            return True, "", {"x": corner[0], "y": corner[1],
                              "how": f"the gate's route, {len(route)} corner(s) in, its last one"}
        # Each ask is a chat round trip (2-4 s), so the ring is small and nearest first: the four sides at 3 and at 5
        # cells, which is where a body's neighbours are. A wider search is the model's job, not the routine's.
        for radius in (3, 5):
            for dx, dy in ((radius, 0), (-radius, 0), (0, radius), (0, -radius)):
                ok, _note, spot = self.gate.can_stand(x + dx, y + dy)
                if ok:
                    return True, "", {"x": spot[0], "y": spot[1], "how": f"a cell {radius} away from it"}
        # Last resort, and the one that actually gets a character to a boss in a walled arena: walk toward it in
        # stages. The gate answers "no" for the goal and for its neighbours, but a cell partway there is ordinary
        # ground and it will route to that; the next round asks again from the new position, closer.
        here = (snapshot or {}).get("pos")
        if here:
            hx, hy = int(here[0]), int(here[1])
            # A fixed-length step, not a fraction of the line: a fraction puts the aim 3 cells away and the walk's own
            # tolerance (6) then answers "already there", so the character never moves and the round repeats forever.
            span = max(abs(x - hx), abs(y - hy)) or 1
            for step in (8, 12, 16, 20, 24):
                t = min(1.0, step / span)
                mx = int(round(hx + (x - hx) * t))
                my = int(round(hy + (y - hy) * t))
                if (mx, my) == (hx, hy):
                    continue
                ok, note, _spot = self.gate.can_stand(mx, my)
                if ok:
                    # Aim at the cell that was computed, not the gate's standAt: for a short hop the standAt is a
                    # cell three cells away, and a walk that close is inside the walk tolerance and does nothing.
                    return True, "", {"x": mx, "y": my, "tight": True,
                                      "how": f"{step} cells toward it, the goal itself is shut"}
        return False, blocked or "no cell near it can be stood on", {}

    def _engage(self, target: int, snapshot: dict) -> dict:
        monster = self._monster(snapshot, target)
        why = self.gate.why(target)
        if why.get("kind") != "monster":
            return self._refuse("engage", {"target": target}, str(why.get("why")), why)
        if why.get("dead"):
            return self._refuse("engage", {"target": target}, "the target is already dead", why)
        if why.get("asleep") or why.get("invulnerable"):
            return self._refuse("engage", {"target": target}, str(why.get("why")), why)

        at = monster.get("pos") if monster else why.get("pos")
        if not why.get("canAttackNow"):
            if at is None:
                return self._refuse("engage", {"target": target}, "the snapshot does not say where the target is")
            ok, blocked, spot = self._approach(at, "engage", snapshot)
            if not ok:
                # The gate says there is no stand-on cell anywhere near the target. That is a refusal to record, not a
                # walk to attempt: the client would only press into whatever is between.
                return self._refuse("engage", {"target": target},
                                    f"the gate sees no way within reach of it: {blocked}",
                                    self.gate.reach(int(at[0]), int(at[1])))
            self.records.event("approach", target=target, aim=spot, said=blocked or None)
            walked, note = self._walk_near(int(spot["x"]), int(spot["y"]), "engage", {"target": target},
                                           tolerance=1 if spot.get("tight") else None)
            if not walked.get("reached"):
                return self._done(False, f"the walk stopped short of {spot['how']}: "
                                         f"{walked.get('reason') or walked.get('cell')}")
            why = self.gate.why(target)          # asked again: the walk may have been the whole problem
            if not why.get("canAttackNow") and not note["gateOverruled"]:
                return self._refuse("engage", {"target": target}, f"still out of reach: {why.get('why')}", why)

        at = why.get("pos") or at
        if at is None:
            return self._refuse("engage", {"target": target}, "the target has no position in the answer")
        result = self.hands.act("useSkill", x=int(at[0]), y=int(at[1]), target=int(target), slot=0)
        self.primitives += 1
        self.records.action(self.round, "useSkill", {"x": int(at[0]), "y": int(at[1]), "target": int(target)},
                            result, int(result.get("elapsedMs") or 0))
        hit = bool(result.get("ok")) and bool(result.get("sent"))
        return self._done(hit, "swung at the target" if hit else str(result.get("reason") or "the swing did not go out"))

    def _loot(self, object_id: int, snapshot: dict) -> dict:
        item = self._ground(snapshot, object_id)
        info = self.gate.item(object_id)
        at = item.get("pos") if item else info.get("pos")
        if not info.get("onGround"):
            return self._refuse("loot", {"object": object_id}, str(info.get("why")), info)
        if not info.get("ok"):
            if at is None:
                return self._refuse("loot", {"object": object_id}, "the snapshot does not say where the item is")
            walked, _note = self._walk_near(int(at[0]), int(at[1]), "loot", {"object": object_id})
            if not walked.get("reached"):
                return self._done(False, f"the walk stopped short: {walked.get('reason') or walked.get('cell')}")

        at = at or [0, 0]
        result = self.hands.act("loot", x=int(at[0]), y=int(at[1]), object=int(object_id))
        self.primitives += 1
        self.records.action(self.round, "loot", {"x": int(at[0]), "y": int(at[1]), "object": int(object_id)},
                            result, int(result.get("elapsedMs") or 0))
        taken = bool(result.get("ok")) and bool(result.get("sent"))
        return self._done(taken, "picked it up" if taken else str(result.get("reason") or "the pickup did not go out"))

    def _goto(self, x: int, y: int) -> dict:
        """Walk to a cell in stages, along the route the gate's walk grid knows.

        A click resolves within about a screen radius, and aiming straight at a far cell walks into whatever lies
        between. So the routine asks the gate for the route's corners and walks them one at a time; each walk is
        its own recorded primitive, and one intent stops after `max_primitives` of them so the model sees the
        result before the next decision.
        """
        route, blocked = self.gate.route(x, y)
        if not route:
            # The gate sees no route on the server's grid. The client keeps its own grid and is authoritative for
            # its own walking, so this is recorded as an override and the character tries the straight aim anyway.
            self.records.event("the gate has no route; the client is trying anyway",
                               kind="goto", arguments={"x": x, "y": y}, gate=blocked)
            walked = self._walk(x, y)
            if walked.get("reached"):
                return self._done(True, "arrived")
            return self._done(False, str(walked.get("reason") or blocked or "the walk stopped short"))

        here = self.hands.position()
        for index, cell in enumerate(route):
            if self.primitives >= self.opts.max_primitives:
                return self._done(False, f"the walk reached waypoint {index} of {len(route)}; "
                                         f"the next snapshot is needed to plan the rest")
            if here and max(abs(here[0] - cell[0]), abs(here[1] - cell[1])) <= self.opts.walk_tolerance:
                continue                          # already standing on this corner
            walked = self._walk(*cell)
            got = walked.get("cell")
            if isinstance(got, (list, tuple)) and len(got) == 2:
                here = (int(got[0]), int(got[1]))
            if not walked.get("reached"):
                return self._done(False, f"{walked.get('reason') or 'the walk stopped short'} "
                                         f"(waypoint {index + 1} of {len(route)})")
        near = bool(here) and max(abs(here[0] - x), abs(here[1] - y)) <= max(self.opts.walk_tolerance, 8)
        if here is None:
            return self._done(True, "the route was walked")
        return self._done(near, "arrived" if near else f"the route stopped at {here}, {(x, y)} was the goal")

    # The death panel is centred and its two buttons are at half the client width, at these fractions of its height
    # (measured on an 800x600 client: 0.283 -> y 170, 0.325 -> y 195). Agents/docs/dev/ai-operator.md.
    RESURRECT_IN_TOWN = (0.5, 0.283)
    RESURRECT_AT_CHECKPOINT = (0.5, 0.325)

    def _resurrect(self, snapshot: dict) -> dict:
        """Click the death panel's RESURRECT IN TOWN, and say whether the character came back.

        The panel is interface, not world, so there is nothing to aim at but the place it is drawn: the picture
        (`hands.screen`) is recorded for the run, and the press goes to a fraction of the client's own window, which
        is what makes it work at any resolution. "Alive again" is read back from the gate, never assumed.
        """
        status = self.hands.status()
        viewport = status.get("viewport") or [800, 600]
        width, height = int(viewport[0]), int(viewport[1])
        try:
            shot = self.hands.screen(os.path.join(self.records.dir, f"death-{self.round:03d}.png"))
            self.records.event("the death panel was photographed", path=shot.get("path"))
        except Exception as exc:                      # a picture is evidence, not a prerequisite
            self.records.event("the death panel could not be photographed", error=str(exc))
        for name, (fx, fy) in (("RESURRECT IN TOWN", self.RESURRECT_IN_TOWN),
                               ("RESURRECT AT CHECKPOINT", self.RESURRECT_AT_CHECKPOINT)):
            if self.primitives >= self.opts.max_primitives:
                break
            point = (int(width * fx), int(height * fy))
            self.primitives += 1
            answer = self.hands.click(*point)
            self.records.action(self.round, "click", {"x": point[0], "y": point[1], "button": name}, answer,
                                int(answer.get("elapsedMs") or 0))
            time.sleep(0.6)
            world = self.gate.world()
            if (world.get("life") or [0, 0])[0] > 0:
                area = (world.get("area") or {}).get("id")
                return self._done(True, f"resurrected by {name}: {area}, life {(world.get('life') or [0])[0]}")
        return self._done(False, "the death panel did not take the click: the character is still dead")

    def _take(self, index: int, snapshot: dict) -> dict:
        row = next((e for e in snapshot.get("exits") or [] if e.get("index") == index), None)
        if row is None:
            return self._refuse("take", {"exit": index}, "the snapshot has no exit with that index")
        pos = row.get("pos") or [0, 0]
        walked, _note = self._walk_near(int(pos[0]), int(pos[1]), "take", {"exit": index})
        if not walked.get("reached"):
            # A walk report is about the last click, not about where the character ended up: a transition is the
            # client's own action, so the area is read back before the walk is called a failure.
            if self._changed_area(snapshot):
                return self._done(True, "the area changed: the exit was taken even though the last click was refused")
            return self._done(False, f"the walk to the exit stopped short: {walked.get('reason') or walked.get('cell')}")
        if row.get("object") is None:
            return self._refuse("take", {"exit": index}, "the snapshot does not name the exit's object id")
        result = self.hands.act("interact", x=int(pos[0]), y=int(pos[1]), object=int(row["object"]))
        self.primitives += 1
        self.records.action(self.round, "interact", {"x": int(pos[0]), "y": int(pos[1]),
                                                    "object": int(row["object"])}, result,
                            int(result.get("elapsedMs") or 0))
        used = bool(result.get("ok")) and bool(result.get("sent"))
        return self._done(used, "took the exit" if used else str(result.get("reason") or "the exit was not taken"))

    # ---- the snapshot, read the way the answers are written --------------
    def _changed_area(self, snapshot: dict) -> bool:
        """Whether the character is somewhere else than the snapshot said (a transition, a death, a kick)."""
        was = (snapshot.get("area") or {}).get("id")
        try:
            now = self.hands.area()
        except Exception:
            return False
        return bool(now) and bool(was) and now != was

    @staticmethod
    def _monster(snapshot: dict, target: int) -> dict | None:
        return next((m for m in snapshot.get("monsters") or [] if m.get("id") == target), None)

    @staticmethod
    def _ground(snapshot: dict, object_id: int) -> dict | None:
        return next((g for g in snapshot.get("ground") or [] if g.get("id") == object_id), None)

    def _refuse(self, kind: str, arguments: dict, reason: str, answer: dict | None = None) -> dict:
        self.records.refusal(self.round, kind, arguments, reason, answer)
        return self._done(False, reason)

    def _done(self, ok: bool, reason: str) -> dict:
        return {"ok": bool(ok), "reason": reason, "primitives": self.primitives}
