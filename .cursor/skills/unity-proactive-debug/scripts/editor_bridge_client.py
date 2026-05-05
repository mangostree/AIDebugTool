#!/usr/bin/env python3
"""
TCP client for Unity Editor localhost bridge (one JSON line per request/response).
Stdlib only. See ../SKILL.md and ../reference.md.
"""

from __future__ import annotations

import json
import os
import socket
import sys
import time
import uuid


DEFAULT_HOST = "127.0.0.1"
DEFAULT_PORT = 8742


def _env_host() -> str:
    return os.environ.get("UNITY_EDITOR_BRIDGE_HOST", DEFAULT_HOST).strip() or DEFAULT_HOST


def _env_port() -> int:
    raw = os.environ.get("UNITY_EDITOR_BRIDGE_PORT", str(DEFAULT_PORT)).strip()
    try:
        return int(raw, 10)
    except ValueError:
        return DEFAULT_PORT


def _env_token() -> str | None:
    t = os.environ.get("UNITY_EDITOR_BRIDGE_TOKEN", "").strip()
    return t or None


def send_payload(obj: dict) -> dict:
    host = _env_host()
    port = _env_port()
    token = _env_token()
    if token and "auth" not in obj:
        obj = {**obj, "auth": token}
    line = json.dumps(obj, ensure_ascii=False) + "\n"
    data = line.encode("utf-8")
    rid = obj.get("id", "")

    try:
        sock = socket.create_connection((host, port), timeout=30.0)
    except OSError as e:
        return {
            "id": rid,
            "ok": False,
            "error": "connect_failed",
            "message": f"{host}:{port} — {e}",
        }

    with sock:
        sock.sendall(data)
        fp = sock.makefile("r", encoding="utf-8", newline="\n")
        response_line = fp.readline()
        if not response_line:
            return {
                "id": rid,
                "ok": False,
                "error": "empty_response",
                "message": "No data from Unity bridge",
            }
        try:
            return json.loads(response_line)
        except json.JSONDecodeError as e:
            return {
                "id": rid,
                "ok": False,
                "error": "invalid_json",
                "message": str(e),
                "raw": response_line[:500],
            }


def _result_is_compiling(out: dict) -> bool | None:
    if not out.get("ok"):
        return None
    res = out.get("result")
    if not isinstance(res, dict):
        return None
    return bool(res.get("isCompiling"))


def refresh_wait(timeout_sec: float = 120.0, poll_sec: float = 0.35) -> dict:
    """Call refresh, then poll compile_status until isCompiling is false or timeout."""
    t0 = time.monotonic()
    first = send_command("refresh")
    if not first.get("ok"):
        return {
            "ok": False,
            "error": "refresh_failed",
            "message": first.get("message", first.get("error", "")),
            "refresh_response": first,
        }
    time.sleep(poll_sec)
    deadline = time.monotonic() + timeout_sec
    last: dict = {}
    while time.monotonic() < deadline:
        last = send_command("compile_status")
        err = last.get("error")
        if err in ("connect_failed", "empty_response"):
            time.sleep(max(poll_sec, 0.5))
            continue
        flag = _result_is_compiling(last)
        if flag is None:
            return {
                "ok": False,
                "error": "compile_status_failed",
                "message": "Could not read isCompiling",
                "last": last,
            }
        if not flag:
            return {
                "ok": True,
                "action": "compile_idle",
                "waited_sec": round(time.monotonic() - t0, 2),
                "last": last,
            }
        time.sleep(poll_sec)
    return {
        "ok": False,
        "error": "compile_timeout",
        "message": f"Still compiling after {timeout_sec}s",
        "waited_sec": round(time.monotonic() - t0, 2),
        "last": last,
    }


def send_command(cmd: str, args: dict | None = None, request_id: str | None = None) -> dict:
    token = _env_token()
    rid = request_id or str(uuid.uuid4())
    payload: dict = {"id": rid, "cmd": cmd}
    if args:
        payload["args"] = args
    if token:
        payload["auth"] = token
    return send_payload(payload)


def main() -> int:
    if len(sys.argv) < 2:
        print(
            "Usage: editor_bridge_client.py "
            "<ping|compile_status|refresh|refresh_wait|menu|invoke_static|enter_play|exit_play|raw JSON_LINE>",
            file=sys.stderr,
        )
        return 2

    sub = sys.argv[1].strip().lower()
    if sub == "raw":
        if len(sys.argv) < 3:
            print("Usage: editor_bridge_client.py raw '{\"cmd\":\"ping\",\"id\":\"1\"}'", file=sys.stderr)
            return 2
        raw_json = sys.argv[2]
        try:
            obj = json.loads(raw_json)
        except json.JSONDecodeError as e:
            print(json.dumps({"ok": False, "error": "invalid_arg_json", "message": str(e)}))
            return 1
        if not isinstance(obj, dict):
            print(json.dumps({"ok": False, "error": "invalid_arg_json", "message": "raw JSON must be an object"}))
            return 1
        cmd = obj.get("cmd")
        if not cmd or not isinstance(cmd, str):
            print(json.dumps({"ok": False, "error": "missing_cmd", "message": "raw JSON must include string cmd"}))
            return 1
        out = send_payload(obj)
        print(json.dumps(out, ensure_ascii=False, indent=2))
        return 0 if out.get("ok") else 1

    if sub == "menu":
        path = " ".join(sys.argv[2:]).strip() if len(sys.argv) > 2 else ""
        if not path:
            print(
                'Usage: editor_bridge_client.py menu "Window/General/Test Runner"',
                file=sys.stderr,
            )
            return 2
        out = send_command("menu", {"path": path})
        print(json.dumps(out, ensure_ascii=False, indent=2))
        return 0 if out.get("ok") else 1

    if sub == "invoke_static":
        if len(sys.argv) < 4:
            print(
                "Usage: editor_bridge_client.py invoke_static Full.Type.Name MethodName",
                file=sys.stderr,
            )
            return 2
        out = send_command(
            "invoke_static",
            {"typeName": sys.argv[2], "methodName": sys.argv[3]},
        )
        print(json.dumps(out, ensure_ascii=False, indent=2))
        return 0 if out.get("ok") else 1

    if sub == "refresh_wait":
        timeout = 120.0
        if len(sys.argv) > 2:
            try:
                timeout = float(sys.argv[2])
            except ValueError:
                print(
                    json.dumps({"ok": False, "error": "bad_timeout", "message": sys.argv[2]}),
                    file=sys.stderr,
                )
                return 2
        out = refresh_wait(timeout_sec=timeout)
        print(json.dumps(out, ensure_ascii=False, indent=2))
        return 0 if out.get("ok") else 1

    if sub not in ("ping", "compile_status", "refresh", "enter_play", "exit_play"):
        print(
            json.dumps(
                {
                    "ok": False,
                    "error": "unknown_subcommand",
                    "message": sub,
                }
            )
        )
        return 2

    out = send_command(sub)
    print(json.dumps(out, ensure_ascii=False, indent=2))
    return 0 if out.get("ok") else 1


if __name__ == "__main__":
    raise SystemExit(main())
