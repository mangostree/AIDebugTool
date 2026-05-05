#!/usr/bin/env python3
"""
Ensure Assets/Scripts/Editor/CursorEditorDebugBridge.cs exists in a Unity project.
If missing, copy from templates/CursorEditorDebugBridge.cs. Stdlib only.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path

BRIDGE_REL = Path("Assets") / "Scripts" / "Editor" / "CursorEditorDebugBridge.cs"
MARKER = "public static class CursorEditorDebugBridge"


def find_unity_project_root(start: Path) -> Path | None:
    for p in [start.resolve(), *start.resolve().parents]:
        if (p / "Assets").is_dir() and (p / "ProjectSettings").is_dir():
            return p
    return None


def template_path() -> Path:
    return Path(__file__).resolve().parent.parent / "templates" / "CursorEditorDebugBridge.cs"


def main() -> int:
    parser = argparse.ArgumentParser(description="Ensure Unity Editor TCP bridge script exists.")
    parser.add_argument(
        "project_root",
        nargs="?",
        default=None,
        help="Unity project root (folder containing Assets/). Default: UNITY_PROJECT_ROOT, cwd, then walk upward.",
    )
    parser.add_argument(
        "--force",
        action="store_true",
        help="Overwrite existing file if it does not contain the bridge marker (dangerous).",
    )
    args = parser.parse_args()

    root_hint: Path | None = None
    if args.project_root:
        root_hint = Path(args.project_root).resolve()
    elif os.environ.get("UNITY_PROJECT_ROOT", "").strip():
        root_hint = Path(os.environ["UNITY_PROJECT_ROOT"].strip()).resolve()
    else:
        root_hint = Path.cwd().resolve()

    root = find_unity_project_root(root_hint)
    if root is None:
        print(
            json.dumps(
                {
                    "ok": False,
                    "error": "no_unity_project",
                    "message": "No Unity project found (need Assets/ and ProjectSettings/).",
                    "hint": root_hint.as_posix(),
                },
                ensure_ascii=False,
                indent=2,
            )
        )
        return 1

    target = root / BRIDGE_REL
    tpl = template_path()
    if not tpl.is_file():
        print(
            json.dumps(
                {
                    "ok": False,
                    "error": "template_missing",
                    "message": tpl.as_posix(),
                },
                ensure_ascii=False,
                indent=2,
            )
        )
        return 2

    if target.is_file():
        try:
            text = target.read_text(encoding="utf-8")
        except OSError as e:
            print(
                json.dumps(
                    {
                        "ok": False,
                        "error": "read_failed",
                        "message": str(e),
                        "path": target.as_posix(),
                    },
                    ensure_ascii=False,
                    indent=2,
                )
            )
            return 1
        if MARKER in text:
            print(
                json.dumps(
                    {
                        "ok": True,
                        "action": "already_present",
                        "path": target.as_posix(),
                        "project_root": root.as_posix(),
                    },
                    ensure_ascii=False,
                    indent=2,
                )
            )
            return 0
        if not args.force:
            print(
                json.dumps(
                    {
                        "ok": False,
                        "error": "unexpected_file",
                        "message": "File exists but does not contain CursorEditorDebugBridge; use --force to replace.",
                        "path": target.as_posix(),
                    },
                    ensure_ascii=False,
                    indent=2,
                )
            )
            return 1

    try:
        target.parent.mkdir(parents=True, exist_ok=True)
        body = tpl.read_text(encoding="utf-8")
        target.write_text(body, encoding="utf-8", newline="\n")
    except OSError as e:
        print(
            json.dumps(
                {
                    "ok": False,
                    "error": "write_failed",
                    "message": str(e),
                    "path": target.as_posix(),
                },
                ensure_ascii=False,
                indent=2,
            )
        )
        return 1

    print(
        json.dumps(
            {
                "ok": True,
                "action": "created",
                "path": target.as_posix(),
                "project_root": root.as_posix(),
            },
            ensure_ascii=False,
            indent=2,
        )
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
