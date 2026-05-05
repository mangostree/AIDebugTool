#!/usr/bin/env python3
"""
Ensure local paths are opened for edit (or add) in a given pending changelist.

Handles:
  - not opened -> p4 edit -c CL (or p4 add -c CL with --add)
  - already in target CL -> no-op
  - opened in another pending CL -> p4 reopen -c CL

Client resolution (in order):
  1) -c / --p4-client
  2) P4CLIENT env if that client is valid for the server
  3) Auto: pick the client whose Root is the longest prefix of cwd (or paths)

When a client is resolved, this process sets os.environ["P4CLIENT"] so all p4
child processes use it. Optional --persist-p4client runs `p4 set` on Windows.
"""

from __future__ import annotations

import argparse
import os
import re
import subprocess
import sys
from pathlib import Path


def p4_run(args: list[str], cwd: str | None = None) -> subprocess.CompletedProcess[str]:
    """Invoke p4; uses P4CLIENT from the environment (no -c)."""
    return subprocess.run(
        ["p4", *args],
        cwd=cwd,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        shell=False,
        env=os.environ.copy(),
    )


CHANGE_RE = re.compile(r"\bchange\s+(\d+)\b", re.IGNORECASE)
# Helix formats include both "change default" and "edit default change (text)".
DEFAULT_RE = re.compile(
    r"\b(?:change\s+default|default\s+change)\b",
    re.IGNORECASE,
)

CLIENT_LINE_RE = re.compile(
    r"^Client\s+(\S+)\s+\d{4}/\d{2}/\d{2}\s+root\s+(.+?)\s+'.*'\s*$",
)


def _norm_key(p: str) -> str:
    p = os.path.normpath(p)
    if sys.platform == "win32":
        return os.path.normcase(p)
    return p


def parse_p4_user(info_stdout: str) -> str | None:
    for line in info_stdout.splitlines():
        line = line.strip()
        if line.startswith("User name:"):
            return line.split(":", 1)[1].strip()
    return None


def client_is_valid(client: str) -> bool:
    r = subprocess.run(
        ["p4", "-c", client, "info"],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
    )
    out = (r.stdout or "") + (r.stderr or "")
    if "Client unknown" in out:
        return False
    if r.returncode != 0:
        return False
    return "Client root:" in out or "Client name:" in out


def parse_client_roots(clients_stdout: str) -> list[tuple[str, str]]:
    """Return list of (client_name, root_path) from `p4 clients -u user` output."""
    rows: list[tuple[str, str]] = []
    for raw in clients_stdout.splitlines():
        line = raw.strip()
        m = CLIENT_LINE_RE.match(line)
        if not m:
            continue
        name, root = m.group(1), m.group(2).strip()
        rows.append((name, root))
    return rows


def discover_client_for_anchor(anchor: Path) -> str | None:
    """
    Pick a client whose Root is a prefix of anchor (longest root wins).
    anchor should be absolute and resolved.
    """
    r = p4_run(["info"])
    info_out = (r.stdout or "") + (r.stderr or "")
    user = parse_p4_user(info_out)
    if not user:
        return None
    r2 = p4_run(["clients", "-u", user])
    if r2.returncode != 0:
        return None
    anchor_s = _norm_key(str(anchor.resolve()))
    best: tuple[int, str] | None = None  # (len root, client name)
    for name, root in parse_client_roots(r2.stdout or ""):
        root_n = _norm_key(root)
        if anchor_s == root_n or anchor_s.startswith(root_n + os.sep):
            ln = len(root_n)
            if best is None or ln > best[0]:
                best = (ln, name)
    return best[1] if best else None


def resolve_p4_client(
    explicit: str | None,
    cwd: str,
    path_hints: list[str],
) -> tuple[str | None, str | None]:
    """
    Returns (client_name, error_message). error_message set if unresolved.
    """
    if explicit:
        if not client_is_valid(explicit):
            return None, f"P4 client invalid or unknown: {explicit}"
        return explicit, None

    env_client = os.environ.get("P4CLIENT")
    if env_client and client_is_valid(env_client):
        return env_client, None

    anchors: list[Path] = [Path(cwd).resolve()]
    for hp in path_hints:
        p = Path(hp).resolve()
        anchors.append(p if p.is_dir() else p.parent)

    tried: list[str] = []
    for a in anchors:
        c = discover_client_for_anchor(a)
        if c:
            return c, None
        tried.append(str(a))

    return None, (
        "Could not discover P4 client for cwd or paths; "
        "try `p4 clients -u YOUR_USER`, then pass -c CLIENT or set P4CLIENT. "
        f"Tried anchors: {tried}"
    )


def persist_p4client_windows(name: str) -> tuple[bool, str]:
    r = subprocess.run(
        ["p4", "set", f"P4CLIENT={name}"],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
    )
    text = (r.stdout or "") + (r.stderr or "")
    return r.returncode == 0, text.strip() or str(r.returncode)


def parse_opened_changelist(stdout: str) -> int | str | None:
    """Pending CL from `p4 opened`, or 'default' for the default changelist."""
    if not stdout or not stdout.strip():
        return None
    m = CHANGE_RE.search(stdout)
    if m:
        return int(m.group(1))
    if DEFAULT_RE.search(stdout):
        return "default"
    return None


def ensure_path(
    path: str,
    changelist: int,
    use_add: bool,
    cwd: str | None,
) -> tuple[bool, str]:
    """
    Returns (ok, message).
    """
    abs_path = str(Path(path).resolve())

    r = p4_run(["opened", abs_path], cwd=cwd)

    # Not opened on this client: open it
    if r.returncode != 0 or not r.stdout.strip():
        verb = "add" if use_add else "edit"
        r2 = p4_run([verb, "-c", str(changelist), abs_path], cwd=cwd)
        text = (r2.stdout or "") + (r2.stderr or "")
        if r2.returncode != 0:
            return (
                False,
                f"p4 {verb} failed ({abs_path}): {text.strip() or r2.returncode}",
            )
        return True, f"p4 {verb} -c {changelist}: {abs_path}"

    current_cl = parse_opened_changelist(r.stdout)
    if current_cl is None:
        return (
            False,
            f"Could not parse changelist from p4 opened output: {r.stdout.strip()}",
        )

    if current_cl == changelist:
        return True, f"already in changelist {changelist}: {abs_path}"

    r3 = p4_run(["reopen", "-c", str(changelist), abs_path], cwd=cwd)
    text3 = (r3.stdout or "") + (r3.stderr or "")
    if r3.returncode != 0:
        return (
            False,
            f"p4 reopen failed ({abs_path}): {text3.strip() or r3.returncode}",
        )
    return True, f"p4 reopen -c {changelist}: {abs_path}"


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("changelist", type=int, help="Pending changelist number")
    p.add_argument(
        "paths",
        nargs="+",
        help="Local file paths (absolute or relative)",
    )
    p.add_argument(
        "-c",
        "--p4-client",
        dest="p4_client",
        default=None,
        help="Force this Perforce client (skips discovery)",
    )
    p.add_argument(
        "--add",
        action="store_true",
        help="Use p4 add for unopened paths instead of p4 edit (new files)",
    )
    p.add_argument(
        "--cwd",
        default=None,
        help="Working directory for p4 (default: current directory)",
    )
    p.add_argument(
        "--persist-p4client",
        action="store_true",
        help="After resolving client, run `p4 set P4CLIENT=...` (Windows persistent)",
    )
    p.add_argument(
        "--no-auto-client",
        action="store_true",
        help="Do not discover client; require -c or valid P4CLIENT",
    )
    args = p.parse_args()

    cwd = args.cwd or os.getcwd()

    if args.no_auto_client:
        client = args.p4_client or os.environ.get("P4CLIENT")
        if not client:
            sys.stderr.write(
                "error: --no-auto-client requires -c or P4CLIENT.\n",
            )
            return 2
        if not client_is_valid(client):
            sys.stderr.write(f"error: invalid P4 client: {client}\n")
            return 2
    else:
        client, err = resolve_p4_client(args.p4_client, cwd, args.paths)
        if err:
            sys.stderr.write(f"error: {err}\n")
            return 2

    os.environ["P4CLIENT"] = client
    print(f"P4CLIENT={client}", file=sys.stderr)

    if args.persist_p4client:
        if sys.platform != "win32":
            sys.stderr.write(
                "warning: --persist-p4client only runs `p4 set` on Windows; "
                "elsewhere use: export P4CLIENT=...\n",
            )
        else:
            ok, msg = persist_p4client_windows(client)
            if ok:
                print(f"p4 set P4CLIENT={client} (persisted for this user)", file=sys.stderr)
            else:
                sys.stderr.write(f"warning: p4 set failed: {msg}\n")

    ok_all = True
    for path in args.paths:
        ok, msg = ensure_path(
            path,
            args.changelist,
            use_add=args.add,
            cwd=cwd,
        )
        print(msg)
        if not ok:
            ok_all = False

    return 0 if ok_all else 1


if __name__ == "__main__":
    sys.exit(main())
