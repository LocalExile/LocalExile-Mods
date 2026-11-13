"""The bridge's line protocol: one long-lived `client_control.py --stdio` process.

The hands are deliberately stupid. They send one primitive, read one answer, and hand the answer back
unchanged: every judgement about whether a primitive is a good idea belongs to the gate (`gate.py`) and
every decision about which primitive to send belongs to the routine or the model (`routine.py`,
`brain.py`). Nothing here walks the character on its own initiative.

The bridge writes one JSON line per request and answers one JSON line, shaped
`{"ok": true, "result": {...}, "elapsedMs": n}` (or `{"ok": false, "error": "..."}`).
"""

from __future__ import annotations

import json
import os
import subprocess
import sys
import time


class BridgeError(RuntimeError):
    """The bridge process died, or answered something that is not one JSON line."""


def _process_started(pid: int) -> int | None:
    """When a process started, as a Windows FILETIME (the unit the bridge's session records use)."""
    if os.name != "nt":
        return None
    import ctypes
    from ctypes import wintypes
    kernel32 = ctypes.windll.kernel32
    kernel32.OpenProcess.restype = wintypes.HANDLE
    handle = kernel32.OpenProcess(0x1000, False, int(pid))       # PROCESS_QUERY_LIMITED_INFORMATION
    if not handle:
        return None
    try:
        creation, exited, kernel, user = (wintypes.FILETIME() for _ in range(4))
        ok = kernel32.GetProcessTimes(handle, ctypes.byref(creation), ctypes.byref(exited),
                                      ctypes.byref(kernel), ctypes.byref(user))
        if not ok:
            return None
        return (creation.dwHighDateTime << 32) | creation.dwLowDateTime
    finally:
        kernel32.CloseHandle(handle)


class Hands:
    def __init__(self, root: str, session: str, python: str | None = None,
                 control: str = "scripts/dev/client_control.py", timeout: float = 120.0,
                 port_base: int | None = None):
        self.root = root
        self.session = session
        self.timeout = timeout
        self.python = python or os.environ.get("AI_OPERATOR_PYTHON") or sys.executable
        self.control = control
        self.port_base = port_base
        self.proc: subprocess.Popen | None = None

    # ---- process ---------------------------------------------------------
    def start(self) -> None:
        if self.proc is not None:
            return
        target = os.path.join(self.root, self.control)
        self.proc = subprocess.Popen(
            [self.python, target, "--session", self.session, "--stdio"],
            cwd=self.root, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            text=True, encoding="utf-8", errors="replace", bufsize=1)
        # stderr is drained by a thread so a chatty bridge cannot fill its pipe and block.
        import threading
        self._err = []
        def drain():
            for line in self.proc.stderr:
                self._err.append(line.rstrip())
        threading.Thread(target=drain, daemon=True).start()

    def close(self) -> None:
        if self.proc is None:
            return
        try:
            self.proc.stdin.close()
        except Exception:
            pass
        try:
            self.proc.wait(timeout=5)
        except Exception:
            self.proc.kill()
        self.proc = None

    def __enter__(self):
        self.start()
        return self

    def __exit__(self, *exc):
        self.close()

    # ---- one request, one answer ----------------------------------------
    def ask(self, request: dict) -> dict:
        if self.proc is None:
            self.start()
        # The bridge reads the login and gateway ports out of the shared `data/stack.json` at call time, and another
        # checkout on this machine overwrites that file with its own ports whenever its `live.ps1` runs. Re-assert
        # our ports before every request: otherwise a login goes to the other server and a chat reply is looked for
        # in the wrong log lines (which looks exactly like "the mod is not loaded").
        self.pin_stack()
        line = json.dumps(request, separators=(",", ":"))
        try:
            self.proc.stdin.write(line + "\n")
            self.proc.stdin.flush()
        except Exception as exc:  # the bridge died between requests
            raise BridgeError(f"the bridge would not take a request: {exc}") from exc
        answer = self.proc.stdout.readline()
        if not answer:
            tail = "\n".join(getattr(self, "_err", [])[-5:])
            raise BridgeError(f"the bridge answered nothing (its last words: {tail!r})")
        try:
            return json.loads(answer)
        except json.JSONDecodeError as exc:
            raise BridgeError(f"the bridge answered something that is not JSON: {answer[:200]!r}") from exc

    def ok(self, request: dict) -> dict:
        """The answered `result`, or a BridgeError carrying the bridge's own error text."""
        answer = self.ask(request)
        if not answer.get("ok"):
            text = str(answer.get("error") or answer)
            # Two things that are not a failed primitive, and both can be put right without touching the client:
            # a window that went away stops the frame loop ("No client frame within 5 seconds"), and another
            # checkout's live.ps1 deletes this session's record ("Client (session X) is not running").
            healed = False
            if "No client frame" in text and self.restore_window():
                healed = True
                time.sleep(1.0)          # the frame loop needs a moment to come back
            elif "is not running" in text and self.heal_record():
                healed = True
                time.sleep(0.3)
            if healed:
                answer = self.ask(request)
                if answer.get("ok"):
                    result = answer.get("result")
                    return result if isinstance(result, dict) else {"value": result}
                text = str(answer.get("error") or answer)
            raise BridgeError(text)
        result = answer.get("result")
        return result if isinstance(result, dict) else {"value": result}

    def frames(self) -> int:
        """How many frames the client drew since the last call (0 means it is not running a loop)."""
        value = self.ask({"command": "game:frames"}).get("result") or {}
        try:
            return int(value.get("frames") or 0)
        except Exception:
            return 0

    def ensure_frames(self, tries: int = 3) -> int:
        """Make sure the client is pumping frames, restoring a window that went away.

        A session client keeps its window at `--window minimized`, and any window that loses the desktop can
        stop the frame loop; the character then stands in the world while every posted input is dropped. This
        is not input injection — it only makes the window visible again.
        """
        drawn = self.frames()
        for _ in range(tries):
            if drawn > 0:
                return drawn
            if not self.restore_window():
                break
            time.sleep(1.0)
            drawn = self.frames()
        return drawn

    # ---- the bridge's own vocabulary ------------------------------------
    def pid(self) -> int | None:
        """The client's process id, from the session record the launcher wrote (no bridge call needed)."""
        try:
            with open(os.path.join(self.root, "data", "clients", f"{self.session}.json"), encoding="utf-8") as fh:
                return int(json.load(fh).get("pid"))
        except Exception:
            return None

    def pid_from_log(self) -> int | None:
        """The client's pid as its own log names it ("[CRIT Client 29620] …").

        Another checkout's `live.ps1` deletes this checkout's session records, and the bridge then answers
        "Client (session X) is not running" while the client is very much alive. The client's log survives
        that, and it names the pid, so the record can be put back (see `heal_record`).
        """
        path = os.path.join(self.root, "data", "clients", self.session, "logs", "Client.txt")
        try:
            with open(path, "rb") as fh:
                fh.seek(0, os.SEEK_END)
                fh.seek(max(0, fh.tell() - 200_000))
                text = fh.read().decode("utf-8", "replace")
        except OSError:
            return None
        found = None
        for chunk in text.split("Client ")[1:]:
            digits = "".join(c for c in chunk[:10] if c.isdigit())
            if digits:
                found = int(digits)
        return found

    def heal_record(self) -> int | None:
        """Put back `data/clients/<session>.json` if another checkout deleted it.

        The bridge identifies a session's client by that record: the pid, its creation time (matched to within
        two seconds) and the exe path. The client's own log names the pid, and Windows knows when the process
        started, so the record can be rebuilt without touching the client.
        """
        record = os.path.join(self.root, "data", "clients", f"{self.session}.json")
        if os.path.exists(record):
            return None
        pid = self.pid_from_log()
        started = _process_started(pid) if pid else None
        if not pid or not started:
            return None
        base = int(self.port_base or 0)
        login_port = base + 2 if base else None
        if login_port is None:
            try:
                with open(os.path.join(self.root, "data", "stack.json"), encoding="utf-8") as fh:
                    login_port = int((json.load(fh).get("ports") or {}).get("login"))
            except Exception:
                login_port = None
        data = {
            "pid": pid,
            "started": started,
            "launcher": 0,
            "cwd": os.path.join(self.root, "data", "clients", self.session),
            "settings": f"localexile-local exile-{self.session}_Config.ini",
            "loginPort": login_port,
        }
        try:
            os.makedirs(os.path.dirname(record), exist_ok=True)
            with open(record, "w", encoding="utf-8") as fh:
                json.dump(data, fh, indent=4)
        except OSError:
            return None
        return pid

    def restore_window(self) -> bool:
        """Un-minimise the client's window.

        A minimised client runs no frames, and the bridge then answers "No client frame within 5 seconds" to
        everything: the character is still in the world but nothing is pumped. Restoring the window is not input
        injection — it is making the window visible so the client's own frame loop runs again.
        """
        if os.name != "nt":
            return False
        pid = self.pid()
        if not pid:
            return False
        import ctypes
        from ctypes import wintypes
        user32 = ctypes.windll.user32
        found = []

        @ctypes.WINFUNCTYPE(ctypes.c_bool, wintypes.HWND, wintypes.LPARAM)
        def visit(hwnd, _):
            owner = wintypes.DWORD()
            user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
            if owner.value == pid and user32.IsWindowVisible(hwnd):
                found.append(hwnd)
            return True

        user32.EnumWindows(visit, 0)
        if not found:
            return False
        hwnd = max(found, key=lambda h: user32.GetWindowRect(h, ctypes.byref(wintypes.RECT())) or 1)
        user32.ShowWindow(hwnd, 9)          # SW_RESTORE
        return True

    def pin_stack(self) -> bool:
        """Write this rig's ports into `data/stack.json`.

        The bridge reads the stack's gateway port from that file to tell one client's chat replies from another's.
        Two checkouts on one machine share `data/`, so another checkout's `live.ps1` overwrites the file with its
        own ports and every chat reply silently comes back empty — the server did answer, the bridge looked at the
        wrong log lines. Re-asserting our ports before a reply-collecting call is the workaround; `port_base` None
        leaves the file alone.
        """
        if not self.port_base:
            return False
        base = int(self.port_base)
        ports = {"patch": base, "patchWeb": base + 1, "login": base + 2, "control": base + 3, "gateway": base + 4}
        path = os.path.join(self.root, "data", "stack.json")
        try:
            with open(path, "w", encoding="utf-8") as fh:
                json.dump({"portBase": base, "ports": ports}, fh, indent=4)
            return True
        except OSError:
            return False

    def status(self) -> dict:
        try:
            return self.ok({"command": "game:status"})
        except BridgeError as exc:
            if "No client frame" in str(exc) and self.restore_window():
                time.sleep(1.0)             # the frame loop needs a moment to come back
                return self.ok({"command": "game:status"})
            raise

    def in_game(self) -> bool:
        try:
            return bool(self.status().get("screen") == "game")
        except BridgeError:
            return False

    def login(self, account: str, password: str, wait: float = 3.0) -> dict:
        return self.ok({"command": "login", "account": account, "password": password, "wait": wait})

    def play(self, name: str, wait: float = 5.0) -> dict:
        return self.ok({"command": "character:play", "name": name, "wait": wait})

    def chat(self, text: str, wait: float = 2.0) -> list[str]:
        """Send a chat line and return the replies addressed to this client."""
        self.pin_stack()          # the reply filter keys on the stack file (see pin_stack)
        result = self.ok({"command": "command", "text": text, "wait": wait})
        replies = result.get("replies") or []
        return [str(r) for r in replies]

    def act(self, kind: str, **kwargs) -> dict:
        """One primitive: walkTo/useSkill/loot/interact/stop. Returns the bridge's result verbatim."""
        request = {"command": "act", "kind": kind}
        request.update({k: v for k, v in kwargs.items() if v is not None})
        started = time.monotonic()
        result = self.ok(request)
        result = dict(result)
        result.setdefault("elapsedMs", int((time.monotonic() - started) * 1000))
        return result

    def click(self, x: int, y: int, **kwargs) -> dict:
        """Press a pixel of the client's own window: an interface button, not a world object."""
        return self.act("click", x=int(x), y=int(y), **kwargs)

    def screen(self, out: str, timeout: float = 8.0) -> dict:
        """Save the client's own frame to a PNG without taking the focus, and return where it is.

        Windows Graphics Capture asks DWM for the window's composited surface, so this works while the game is
        neither focused nor in front (Agents/docs/dev/client-control.md, "Seeing the screen without taking the focus").
        """
        self.pin_stack()
        return self.ok({"command": "game:screen", "out": out, "timeout": timeout})

    # ---- convenience the routine and the model both need ----------------
    def position(self) -> tuple[int, int] | None:
        """The character's cell as the *client* reports it (the server's own view is in /aiop world)."""
        try:
            status = self.status()
        except BridgeError:
            return None
        pos = status.get("pos")
        return (int(pos[0]), int(pos[1])) if isinstance(pos, (list, tuple)) and len(pos) == 2 else None

    def area(self) -> str | None:
        try:
            return self.status().get("area")
        except BridgeError:
            return None
