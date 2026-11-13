"""The model adapter: one compressed snapshot in, exactly one intent out.

The model is slow and the world is not, so it is asked on four events only (a routine goal finished, an
unclassifiable change, the same primitive failing twice, the character dying) and never per frame. What it
returns is an *intent* — what should be true next — not a keystroke and not a coordinate it invented: every
number in an intent has to come from the snapshot it was just given, and the local loop compiles the intent into
the primitives (`routine.py`).

The transport is one OpenAI-compatible chat-completions call, configured entirely from the environment, so no
key is ever committed:

    AI_OPERATOR_BASE_URL   e.g. http://127.0.0.1:3110/v1   (the Local AI Hub's OpenAI proxy, or any endpoint)
    AI_OPERATOR_MODEL      e.g. the model id the endpoint advertises
    AI_OPERATOR_API_KEY    optional; a local endpoint usually needs none

`ScriptedBrain` exists for mechanics tests of the loop (does the routine execute, do the records fill, does the
gate refuse). It is never the brain of an acceptance run, and `run.py` says which brain it used in the records.
"""

from __future__ import annotations

import json
import os
import re
import urllib.error
import urllib.request

KINDS = ("engage", "loot", "goto", "take", "wait", "stop")
# `resurrect` is the routine's own: the model is never asked to click a death panel, and a brain invented
# "resurrect" is rejected. run.py lets the routine's own intents through validate().
ROUTINE_ONLY = ("resurrect",)

SYSTEM = """You are the operator of one character in a Path of Exile server. You do not press keys and you do
not teleport: you say what should happen next, and a local loop turns it into the character's own actions.

You are given one snapshot of the world the server knows (positions in cells, distances in cells, monster and
item ids, exits). Answer with exactly one JSON object and nothing else:

  {"goal": "<what you are trying to achieve now, a few words>",
   "next": {"kind": "engage", "target": <monster id>}          kill or attack that monster
         | {"kind": "loot", "object": <ground item id>}         pick that item up
         | {"kind": "goto", "x": <int>, "y": <int>}             walk to that cell
         | {"kind": "take", "exit": <exit index>}               use that area transition
         | {"kind": "wait"}                                     let the world settle
         | {"kind": "stop", "reason": "<why>"}                  nothing more to do}

Rules that are checked, not trusted:
  * exactly one intent, and you must be shown the result of the previous one before you send the next;
  * every id and coordinate must appear in the snapshot you were just given — never invent one;
  * you cannot walk through walls or monsters; the gate will tell you if a cell is unreachable, and then you
    should pick a nearer, reachable cell (walk in stages if the target is far);
  * if the same intent failed twice, do not repeat it: choose something else or stop with the reason.

Finding a target that is not in the snapshot yet:
  * a snapshot only reaches so far. If the thing your goal names is missing, do NOT stop and do NOT invent its
    id: pick a cell from the snapshot in its direction (an exit, or a cell a few hundred cells that way), walk
    there with "goto", and look at the next snapshot — the target appears once you are close enough;
  * "stop" is for a finished goal or a real impossibility, never for "not in range yet";
  * the exits list always names a real place in this area: walking toward the exit of the area you must leave
    through is the useful move when nothing else is in sight.
Answer with the JSON object only."""


class BrainError(RuntimeError):
    """The model answered something that is not a valid intent."""


def validate(intent) -> tuple[bool, str]:
    if not isinstance(intent, dict):
        return False, "the intent is not a JSON object"
    nxt = intent.get("next")
    if not isinstance(nxt, dict):
        return False, "the intent has no 'next' object"
    kind = nxt.get("kind")
    if kind in ROUTINE_ONLY:
        return True, ""
    if kind not in KINDS:
        return False, f"'kind' must be one of {KINDS}, not {kind!r}"
    if kind == "engage" and not isinstance(nxt.get("target"), int):
        return False, "'engage' needs an integer 'target' (a monster id from the snapshot)"
    if kind == "loot" and not isinstance(nxt.get("object"), int):
        return False, "'loot' needs an integer 'object' (a ground item id from the snapshot)"
    if kind == "goto" and not (isinstance(nxt.get("x"), int) and isinstance(nxt.get("y"), int)):
        return False, "'goto' needs integer 'x' and 'y'"
    if kind == "take" and not isinstance(nxt.get("exit"), int):
        return False, "'take' needs an integer 'exit' (the exit index from the snapshot)"
    return True, ""


def extract(text: str) -> dict:
    """The first balanced JSON object in the model's answer (models like to wrap it in prose)."""
    start = text.find("{")
    while start != -1:
        depth = 0
        for i in range(start, len(text)):
            if text[i] == "{":
                depth += 1
            elif text[i] == "}":
                depth -= 1
                if depth == 0:
                    try:
                        return json.loads(text[start:i + 1])
                    except json.JSONDecodeError:
                        break
        start = text.find("{", start + 1)
    raise BrainError(f"no JSON object in the model's answer: {text[:300]!r}")


def _env_flag(name: str) -> bool:
    return os.environ.get(name, "").strip().lower() in ("1", "true", "yes", "on")


class OpenAIBrain:
    """One chat-completions call per decision. The key, the endpoint and the model come from the environment."""

    def __init__(self, base_url: str | None = None, model: str | None = None, api_key: str | None = None,
                 timeout: float = 120.0, temperature: float = 0.2, retries: int = 1):
        self.base_url = (base_url or os.environ.get("AI_OPERATOR_BASE_URL")
                         or "http://127.0.0.1:3110/v1").rstrip("/")
        self.model = model or os.environ.get("AI_OPERATOR_MODEL") or ""
        self.api_key = api_key if api_key is not None else os.environ.get("AI_OPERATOR_API_KEY", "")
        self.timeout = timeout
        self.temperature = temperature
        self.retries = retries
        self.thinking = _env_flag("AI_OPERATOR_THINKING")
        self.max_tokens = int(os.environ.get("AI_OPERATOR_MAX_TOKENS", "400"))
        self.name = f"openai:{self.model or '?'}"

    def models(self) -> list[str]:
        answer = self._post("/models", None, method="GET")
        return [str(m.get("id")) for m in (answer.get("data") or [])]

    def decide(self, snapshot: dict, goal: str, last: dict | None, attempts: list[dict]) -> dict:
        user = {
            "goal": goal,
            "snapshot": snapshot,
            "what happened with your previous intent": last,
            "your intents so far": attempts[-8:],
        }
        body = {
            "model": self.model,
            "temperature": self.temperature,
            "max_tokens": self.max_tokens,
            "messages": [{"role": "system", "content": SYSTEM},
                         {"role": "user", "content": json.dumps(user, ensure_ascii=False)}],
        }
        if not self.thinking:
            # A thinking model spends the whole context on reasoning_content and returns an empty
            # content when it runs out (llama.cpp: finish_reason "length", content ""). The operator
            # wants one small JSON object, so thinking is off unless AI_OPERATOR_THINKING=1.
            body["chat_template_kwargs"] = {"enable_thinking": False}
        if not self.model:
            raise BrainError("no model configured: set AI_OPERATOR_MODEL (or pass model=)")
        last_error = ""
        for _ in range(self.retries + 1):
            answer = self._post("/chat/completions", body)
            choices = answer.get("choices") or []
            if not choices:
                last_error = f"the endpoint answered no choices: {str(answer)[:200]}"
                continue
            content = (choices[0].get("message") or {}).get("content") or ""
            intent = extract(content)
            ok, why = validate(intent)
            if ok:
                return intent
            last_error = why
        raise BrainError(last_error or "the model gave no usable intent")

    def _post(self, path: str, body: dict | None, method: str = "POST") -> dict:
        request = urllib.request.Request(self.base_url + path, method=method)
        data = None
        if body is not None:
            data = json.dumps(body).encode("utf-8")
            request.add_header("Content-Type", "application/json")
        if self.api_key:
            request.add_header("Authorization", f"Bearer {self.api_key}")
        try:
            with urllib.request.urlopen(request, data=data, timeout=self.timeout) as response:
                return json.loads(response.read().decode("utf-8", "replace"))
        except urllib.error.HTTPError as exc:
            detail = exc.read().decode("utf-8", "replace")[:300]
            raise BrainError(f"the endpoint refused ({exc.code}): {detail}") from exc
        except Exception as exc:
            raise BrainError(f"the endpoint is unreachable at {self.base_url}{path}: {exc}") from exc


class ScriptedBrain:
    """A fixed policy, for mechanics tests only: walk to the goal exit, engaging what it meets on the way.

    It exists so the loop, the gate and the records can be tested without a model. `run.py` labels its records
    with the brain's name, so a run driven by this can never be mistaken for the acceptance run.
    """

    name = "scripted"

    def __init__(self, exit_index: int | None = None):
        self.exit_index = exit_index
        self._walked = 0

    def decide(self, snapshot: dict, goal: str, last: dict | None, attempts: list[dict]) -> dict:
        if (snapshot.get("life") or [1, 1])[0] <= 0:
            return {"goal": "dead", "next": {"kind": "stop", "reason": "the character died"}}
        monsters = [m for m in snapshot.get("monsters") or [] if m.get("hostile") and not m.get("dead")]
        if monsters:
            nearest = min(monsters, key=lambda m: m.get("dist", 1e9))
            return {"goal": "clear the way", "next": {"kind": "engage", "target": nearest["id"]}}
        ground = snapshot.get("ground") or []
        if ground:
            nearest = min(ground, key=lambda g: g.get("dist", 1e9))
            return {"goal": "take what is on the ground", "next": {"kind": "loot", "object": nearest["id"]}}
        exits = snapshot.get("exits") or []
        if exits:
            want = self.exit_index
            exit_row = next((e for e in exits if want is None or e.get("index") == want), exits[0])
            return {"goal": "leave through the exit", "next": {"kind": "take", "exit": exit_row["index"]}}
        return {"goal": "nothing to do", "next": {"kind": "stop", "reason": "the snapshot has no targets"}}


def make_brain(kind: str, **kwargs):
    # exit_index is the scripted policy's own knob; the model brain takes no such argument.
    if kind == "openai":
        kwargs.pop("exit_index", None)
        return OpenAIBrain(**kwargs)
    if kind == "scripted":
        return ScriptedBrain(**kwargs)
    raise BrainError(f"unknown brain {kind!r}: use 'openai' or 'scripted'")
