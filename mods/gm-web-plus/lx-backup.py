#!/usr/bin/env python3
"""Snapshot the LocalExile save databases while the server is running.

The Gateway keeps characters.db in WAL mode, so a plain file copy loses
whatever is still sitting in characters.db-wal. This uses SQLite's own
online backup API instead, which is safe against a live writer:

    python lx-backup.py --db D:\\LocalExile\\data
    python lx-backup.py --db D:\\LocalExile\\data --list
    python lx-backup.py --db D:\\LocalExile\\data --restore 20261128-121403

Taking a snapshot writes one folder:

    gm-backups/
      20261128-121403/
        characters.db
        auth.db
        manifest.json

and keeps the newest --keep snapshots (default 12), deleting the rest.

Restoring stops nothing by itself: it refuses while the servers are up, so
close Waypoint (or LocalExile.exe) first. It also moves the live files aside
into gm-backups/pre-restore-<stamp>/ before writing, and removes the -wal and
-shm files, which is what makes the restored database actually win.

Prints one line of JSON on success so the bridge can parse it.

Exit codes: 0 ok, 1 usage/setup error, 2 a database could not be read,
3 restore refused because the servers are running.
"""

import argparse
import json
import os
import shutil
import sqlite3
import subprocess
import sys
import time

# characters.db is the actual save; auth.db holds the accounts. world.db is
# configuration and is handled by lx-settings.py, so it is not copied here.
DBS = [
    ("characters.db", "saves"),
    ("auth.db", "accounts"),
]


def snapshot(src_path, dst_path):
    """Copy one live SQLite database through the backup API."""
    src = sqlite3.connect("file:%s?mode=ro" % src_path.replace("\\", "/"), uri=True, timeout=10)
    try:
        dst = sqlite3.connect(dst_path)
        try:
            src.backup(dst)
            ok = dst.execute("PRAGMA integrity_check").fetchone()[0]
        finally:
            dst.close()
    finally:
        src.close()
    return ok


def prune(out_dir, keep):
    """Delete every snapshot except the newest `keep`."""
    snaps = sorted(
        (d for d in os.listdir(out_dir)
         if os.path.isdir(os.path.join(out_dir, d)) and d[:8].isdigit()),
        reverse=True,
    )
    removed = []
    for old in snaps[max(keep, 1):]:
        shutil.rmtree(os.path.join(out_dir, old), ignore_errors=True)
        removed.append(old)
    return removed


SERVER_EXES = ("GameServer.Gateway.exe", "GameServer.Auth.exe", "GameServer.Patch.exe")


def servers_running():
    """Names of the LocalExile server processes that are up right now."""
    up = []
    for exe in SERVER_EXES:
        try:
            out = subprocess.run(
                ["tasklist", "/FI", "IMAGENAME eq %s" % exe, "/NH"],
                capture_output=True, text=True, timeout=10,
            ).stdout
        except Exception:                            # noqa: BLE001 - no tasklist, assume clear
            return []
        if exe.lower() in out.lower():
            up.append(exe)
    return up


def list_snapshots(out_dir):
    if not os.path.isdir(out_dir):
        return []
    rows = []
    for name in sorted(os.listdir(out_dir), reverse=True):
        folder = os.path.join(out_dir, name)
        if not os.path.isdir(folder):
            continue
        manifest = os.path.join(folder, "manifest.json")
        item = {"stamp": name, "restorable": name[:8].isdigit(), "label": None,
                "taken": None, "bytes": 0, "files": [], "missing": []}
        if os.path.isfile(manifest):
            try:
                with open(manifest, encoding="utf-8") as fh:
                    meta = json.load(fh)
                item["label"] = meta.get("label") or None
                item["taken"] = meta.get("taken")
                item["files"] = [f["name"] for f in meta.get("files", [])]
                item["bytes"] = sum(f.get("bytes", 0) for f in meta.get("files", []))
            except Exception:                        # noqa: BLE001 - unreadable manifest
                pass
        for name_db, _ in DBS:
            path = os.path.join(folder, name_db)
            if os.path.isfile(path):
                item["bytes"] += os.path.getsize(path)
                if name_db not in item["files"]:
                    item["files"].append(name_db)
            elif item["restorable"]:
                item["missing"].append(name_db)
        rows.append(item)
    return rows


def restore(src_dir, out_dir, stamp, dry_run):
    """Put one snapshot back over the live save databases."""
    up = servers_running()
    if up:
        raise RuntimeError(
            "the servers are still running (%s); close Waypoint or LocalExile.exe first, "
            "or the Gateway will overwrite the restored saves when it exits" % ", ".join(up)
        )

    snap = os.path.join(out_dir, stamp)
    if not os.path.isdir(snap):
        raise RuntimeError("no snapshot named %s in %s" % (stamp, out_dir))

    plan = []
    for name, _ in DBS:
        src = os.path.join(snap, name)
        if os.path.isfile(src):
            plan.append((name, src))
    if not plan:
        raise RuntimeError("snapshot %s holds no database file" % stamp)

    aside = os.path.join(out_dir, "pre-restore-" + time.strftime("%Y%m%d-%H%M%S"))
    if not dry_run:
        os.makedirs(aside, exist_ok=True)

    moved = []
    for name, src in plan:
        live = os.path.join(src_dir, name)
        if not dry_run:
            if os.path.isfile(live):
                shutil.move(live, os.path.join(aside, name))
                moved.append(os.path.join(aside, name))
            # a stale -wal would be replayed over the restored file on open
            for extra in ("-wal", "-shm"):
                side = live + extra
                if os.path.isfile(side):
                    shutil.move(side, os.path.join(aside, name + extra))
                    moved.append(os.path.join(aside, name + extra))
            shutil.copy2(src, live)

    return {"aside": aside if not dry_run else None, "moved": moved,
            "restored": [n for n, _ in plan], "dry_run": bool(dry_run)}


def main():
    ap = argparse.ArgumentParser(description="Snapshot and restore the LocalExile save databases.")
    ap.add_argument("--db", required=True, help="the game's data folder (holds characters.db)")
    ap.add_argument("--out", default="", help="where to write snapshots (default <db>/gm-backups)")
    ap.add_argument("--keep", type=int, default=12, help="how many snapshots to keep (default 12)")
    ap.add_argument("--label", default="", help="why this snapshot was taken, stored in the manifest")
    ap.add_argument("--list", action="store_true", help="list the snapshots and stop")
    ap.add_argument("--restore", default="", metavar="STAMP", help="put one snapshot back")
    ap.add_argument("--dry-run", action="store_true", help="with --restore: only say what would happen")
    args = ap.parse_args()

    src_dir = os.path.abspath(args.db)
    if not os.path.isdir(src_dir):
        print("data folder not found: %s" % src_dir, file=sys.stderr)
        return 1

    out_dir = os.path.abspath(args.out) if args.out else os.path.join(src_dir, "gm-backups")

    if args.list:
        print(json.dumps({"ok": True, "dir": out_dir, "snapshots": list_snapshots(out_dir)},
                         ensure_ascii=False))
        return 0

    if args.restore:
        try:
            info = restore(src_dir, out_dir, args.restore, args.dry_run)
        except RuntimeError as exc:
            print(str(exc), file=sys.stderr)
            return 3
        print(json.dumps(dict(ok=True, stamp=args.restore, **info), ensure_ascii=False))
        return 0

    missing = [n for n, _ in DBS if not os.path.isfile(os.path.join(src_dir, n))]
    if len(missing) == len(DBS):
        print("no save database in %s (looked for %s)"
              % (src_dir, ", ".join(n for n, _ in DBS)), file=sys.stderr)
        return 1

    os.makedirs(out_dir, exist_ok=True)

    stamp = time.strftime("%Y%m%d-%H%M%S")
    snap = os.path.join(out_dir, stamp)
    n = 2
    while os.path.isdir(snap):                       # two clicks inside a second
        snap = os.path.join(out_dir, "%s-%d" % (stamp, n))
        n += 1
    os.makedirs(snap)

    files = []
    failed = []
    for name, kind in DBS:
        src = os.path.join(src_dir, name)
        if not os.path.isfile(src):
            continue
        dst = os.path.join(snap, name)
        try:
            state = snapshot(src, dst)
        except Exception as exc:                     # noqa: BLE001 - report, do not crash
            failed.append("%s: %s" % (name, exc))
            continue
        files.append({
            "name": name,
            "kind": kind,
            "bytes": os.path.getsize(dst),
            "source_bytes": os.path.getsize(src),
            "integrity": state,
        })

    if not files:
        shutil.rmtree(snap, ignore_errors=True)
        print("nothing could be copied: %s" % "; ".join(failed), file=sys.stderr)
        return 2

    manifest = {
        "stamp": os.path.basename(snap),
        "taken": time.strftime("%Y-%m-%dT%H:%M:%S"),
        "label": args.label,
        "source": src_dir,
        "files": files,
        "failed": failed,
    }
    with open(os.path.join(snap, "manifest.json"), "w", encoding="utf-8") as fh:
        json.dump(manifest, fh, ensure_ascii=False, indent=2)

    removed = prune(out_dir, args.keep)
    kept = len([d for d in os.listdir(out_dir)
                if os.path.isdir(os.path.join(out_dir, d)) and d[:8].isdigit()])

    print(json.dumps({
        "ok": True,
        "dir": snap,
        "stamp": os.path.basename(snap),
        "bytes": sum(f["bytes"] for f in files),
        "files": [f["name"] for f in files],
        "failed": failed,
        "kept": kept,
        "removed": removed,
    }, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.exit(main())
