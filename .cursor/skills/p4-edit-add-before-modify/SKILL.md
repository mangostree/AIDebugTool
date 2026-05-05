---
name: p4-edit-add-before-modify
description: >-
  Before editing or adding code in a Perforce workspace, asks for the pending
  changelist number; verifies the server is reachable (asks for P4PORT if not);
  resolves P4CLIENT from the file paths being opened (not a stale shell
  client), then opens files in the CL (edit/add + reopen). Use when modifying
  Engine/project source under P4, when files are read-only, or when the user
  wants edits tracked in a specific CL.
---

# P4: open files before edit, add new files

**Project copy:** `.cursor/skills/p4-edit-add-before-modify/` in the Engine repo (same P4 conventions as `p4-changelist-cpp-if-braces`).

## Why `p4 edit -c CL` alone often fails

If a file is **already opened** (same workspace) on the **default changelist** or **another pending CL**, running `p4 edit -c YOUR_CL` again usually **errors** instead of moving the file. The correct move is:

```bash
p4 reopen -c YOUR_CL "path/to/file"
```

The helper script implements the full decision tree (not opened → `edit`/`add`; same CL → skip; otherwise → `reopen`).

## P4PORT / server reachability (before client or opens)

**Do not** treat “could not discover client” as the only failure mode when the real problem is **no connection to the server**.

1. Run **`p4 info`** (or rely on the helper script, which calls it first). If you see messages such as **`Connect to server failed`**, **`check $P4PORT`**, **`TCP connect` … `failed`**, **DNS / connection refused**, or **`p4 info` exits non-zero**:
   - **Stop** and **ask the user once** for the correct **`host:port`** (and whether **VPN** or **`p4 login`** is required).
   - After they confirm, use e.g. **`p4 set P4PORT=HOST:PORT`**, **`p4 login`**, then **`p4 info`** until it succeeds **before** running the helper script or interpreting client discovery errors.

2. Only after the server responds should you troubleshoot **P4CLIENT** / workspace mapping.

## P4CLIENT: workspace from the **file paths** you open (default)

Wrong or missing **`P4CLIENT`** (e.g. shell default points at a non-existent client) makes every `p4` fail. **Do not rely on `-c` on every command** unless the user overrides.

**Workspace rule:** use the Perforce **client (workspace) whose Root contains the files you are opening**—i.e. derived from the **directories of those paths**, not from habit or an unrelated shell **`P4CLIENT`**.

**Preferred:** run the script **without** `-c`. It will:

1. Fail fast if **`p4 info`** cannot reach the server (see previous section); the error text tells the agent to **ask for P4PORT** / network / login.
2. Use **`-c NAME`** if you passed it (and validate it).
3. Else **discover from the file paths**: for each path, take its directory (or the path if it is a folder), run **`p4 clients -u <User from p4 info>`**, pick the client whose **Root** is the **longest** prefix of that directory. **All paths in one invocation must resolve to the same client**; otherwise the script errors (pass **`-c`** or split runs per workspace).
4. Else if **`P4CLIENT`** is set and **valid** on the server, use it (fallback when paths do not sit under any client root).
5. Else **discover from `cwd`** the same way (longest Root prefix).

Then it sets **`os.environ["P4CLIENT"]`** for all `p4` subprocesses in that run and prints to stderr:

```text
P4CLIENT=resolved_client_name
```

So a single script invocation self-heals the client for checkout.

**Optional — persist for new shells (Windows):** add **`--persist-p4client`** so the script runs **`p4 set P4CLIENT=...`** (registry / user default for `p4`). On Linux/macOS there is no equivalent single command; use `export P4CLIENT=...` in the shell profile or session.

**If the agent runs more `p4` commands in the same shell after the script:** either run them in the **same** Python invocation (not applicable), or set the shell once, e.g. PowerShell:

```powershell
$env:P4CLIENT = 'resolved_client_name'
```

(copy the name from the script’s stderr line), or use **`--persist-p4client`** on Windows.

## When this applies

- You are about to **change existing files** or **create new source files** in a **Perforce-managed** tree.
- Depot files are often **read-only on disk** until opened for edit; writes fail without opening first.
- **New files** are not in the depot until `p4 add`.

## Prerequisites (check before any `p4` command)

1. `p4` on `PATH`; **`p4 info` succeeds** (correct **P4PORT**, **P4USER**, **login** / VPN if needed). If not, **ask the user** for connection details—do not guess.
2. Local paths must sit **under some client’s Root** that appears in **`p4 clients -u YOUR_USER`** for the matching machine (see `p4 where` if a path is rejected).

## Mandatory first step: changelist

1. If the user has **not** given a **pending changelist number** for this session/task, **ask once** before touching files:  
   *「请提供本次修改要使用的 Perforce pending changelist 号码（例如 `12345`）。」*
2. Use that CL for all opens in this edit session unless the user specifies a different CL later.
3. If the user says **default / new CL**: create or use their usual workflow (`p4 change`, or `p4 change -o | p4 change -i`); only proceed after you have a numeric **pending** CL.
4. If a file is **already opened in another changelist** and moving it would require `p4 reopen -c OTHER_CL`, ask the user first. Do not silently change the file’s changelist.

## Preferred: helper script (existing + new files)

From the **repository root** (or set paths so cwd / file dirs match the real client root):

**Existing depot files:**

```bash
python .cursor/skills/p4-edit-add-before-modify/scripts/ensure_open_in_changelist.py CHANGELIST path/to/file [more/files...]
```

**New files** (not yet in depot):

```bash
python .cursor/skills/p4-edit-add-before-modify/scripts/ensure_open_in_changelist.py CHANGELIST path/to/newfile --add
```

**Overrides:**

- **`-c YOUR_P4_CLIENT`** — force a client; skip discovery.
- **`--no-auto-client`** — require `-c` or a valid **`P4CLIENT`**; no discovery.
- **`--persist-p4client`** — Windows: run **`p4 set P4CLIENT=...`** after resolve.

On failure, print the script’s stderr/stdout and stop — do not assume a Git-only workflow without confirmation. If the failure is **server unreachable**, follow **P4PORT / server reachability** and **ask the user** before retrying.

## Manual workflow (if you cannot run Python)

1. **Reach server:** `p4 info` must work; if not, **ask for P4PORT** / login, then fix **`p4 set`** as needed.
2. **Resolve client from paths:** `p4 info` → User name → `p4 clients -u USER` → for **each file’s directory**, find the row whose **root** is the **longest** prefix of that directory; ensure all files agree on one client; set **`$env:P4CLIENT`** (PowerShell) or **`export P4CLIENT`** (bash), or **`p4 set P4CLIENT=...`** (Windows).
3. For each **existing depot** path: **`p4 opened`**, then **`p4 edit`** / **`p4 reopen`** as in the earlier sections of this skill (default CL uses **`edit default change`** in `p4 opened` output).
4. For **new** paths: **`p4 add -c CHANGELIST`**, with **`p4 reopen`** if opened elsewhere.

## Agent workflow (checklist)

1. **Get CL** from user if missing.
2. **`p4 info`**: if the server is unreachable, **ask the user** for **`host:port`**, login, VPN; fix **`P4PORT`** and retry until **`p4 info`** succeeds.
3. **List files** you will modify or create (these paths define the intended **workspace** via their directories).
4. Run **`ensure_open_in_changelist.py`** with those **absolute or repo-relative paths**, **without `-c`** unless the user named a specific client or paths span workspaces; use **`--add`** for new depot paths.
5. If you need **`p4`** again in the **same shell**, set **`P4CLIENT`** from the script’s stderr line or use **`--persist-p4client`** (Windows).
6. Then apply code edits (patch, write, etc.).
7. On **write failure** (access denied / read-only): run the script again for that path, then retry once.

## Related

- C++ brace audit on a named CL: `.cursor/skills/p4-changelist-cpp-if-braces/SKILL.md`.

## Notes

- Perforce “checkout” here means **`p4 edit`** (open for edit), not a separate checkout command.
- Do not open **read-only or generated artifacts** the user asked not to version; follow their scope.
- Binary or special file types: if `p4 add` prompts for type, use team defaults or ask the user.
