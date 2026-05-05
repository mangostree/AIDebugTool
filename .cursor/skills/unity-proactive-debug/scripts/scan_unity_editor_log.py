#!/usr/bin/env python3
"""
Read the tail of Unity Editor.log and surface likely errors / bridge lines.
Stdlib only. Override path with UNITY_EDITOR_LOG.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path


def default_log_path() -> Path | None:
    override = os.environ.get("UNITY_EDITOR_LOG", "").strip()
    if override:
        p = Path(override).expanduser()
        return p if p.is_file() else None

    if sys.platform == "win32":
        local = os.environ.get("LOCALAPPDATA", "").strip()
        if local:
            p = Path(local) / "Unity" / "Editor" / "Editor.log"
            return p if p.is_file() else p
        return None

    if sys.platform == "darwin":
        p = Path.home() / "Library" / "Logs" / "Unity" / "Editor.log"
        return p if p.is_file() else p

    p = Path.home() / ".config" / "unity3d" / "Editor.log"
    if p.is_file():
        return p
    return Path.home() / ".local" / "share" / "unity3d" / "Editor.log"


def line_matches(line: str) -> bool:
    lower = line.lower()
    needles = (
        "logerror",
        "exception",
        "error cs",
        "script compilation error",
        "failed to",
        "cursoreditordebugbridge",
        "compilation failed",
        "scripts have compiler errors",
        "socket",
        "address already in use",
        "套接字",
    )
    return any(n in lower for n in needles)


def main() -> int:
    parser = argparse.ArgumentParser(description="Scan Unity Editor.log tail for errors / bridge output.")
    parser.add_argument("--tail", type=int, default=400, help="Max lines to read from end of file (default 400)")
    parser.add_argument("--json", action="store_true", help="Print JSON summary for agents")
    parser.add_argument("path", nargs="?", default=None, help="Explicit Editor.log path (optional)")
    args = parser.parse_args()

    log_path = Path(args.path).expanduser() if args.path else default_log_path()
    if log_path is None:
        msg = "Could not resolve Editor.log (set UNITY_EDITOR_LOG or pass path)."
        if args.json:
            print(json.dumps({"ok": False, "error": "no_path", "message": msg}))
        else:
            print(msg, file=sys.stderr)
        return 1

    if not log_path.is_file():
        msg = f"Editor.log not found: {log_path}"
        if args.json:
            print(
                json.dumps(
                    {
                        "ok": False,
                        "error": "missing_file",
                        "path": log_path.as_posix(),
                        "message": msg,
                    }
                )
            )
        else:
            print(msg, file=sys.stderr)
        return 1

    try:
        raw = log_path.read_text(encoding="utf-8", errors="replace")
    except OSError as e:
        if args.json:
            print(json.dumps({"ok": False, "error": "read_failed", "message": str(e)}))
        else:
            print(str(e), file=sys.stderr)
        return 1

    lines = raw.splitlines()
    tail = lines[-args.tail :] if len(lines) > args.tail else lines
    matched = [ln for ln in tail if line_matches(ln)]

    if args.json:
        print(
            json.dumps(
                {
                    "ok": True,
                    "path": log_path.resolve().as_posix(),
                    "tail_lines": len(tail),
                    "matched_count": len(matched),
                    "matched": matched[-80:],
                },
                ensure_ascii=False,
                indent=2,
            )
        )
        return 0

    print(f"Editor.log: {log_path.resolve()}")
    print(f"(last {len(tail)} lines scanned, {len(matched)} matching)")
    print("---")
    for ln in matched[-120:]:
        print(ln)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
