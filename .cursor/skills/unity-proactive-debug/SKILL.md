---
name: unity-proactive-debug
description: >-
  Unity Editor TCP bridge: ensure bridge; first-time file create needs Editor compile before
  ping (user must focus Unity — no TCP until then); after .cs edits run refresh_wait, scan
  Editor.log, then ping; Play, ExecuteMenuItem, static invoke over localhost. Use for proactive
  Editor debugging, compile verification, menu automation, or connect_failed diagnosis.
---

# Unity proactive debug (Editor bridge + Play)

## When to use

- User wants **Play 模式**验证，且需要 **看得见 Game 视窗**（Unity 以**正常带界面**方式已打开工程）。
- User asks the agent to **发起 Play / 停止 Play**、确认 Editor 是否就绪。
- After editing Unity scripts, run a **quick bridge check** before deeper tests.

Do **not** use this skill for headless `-batchmode -nographics` CI; that is a separate workflow.

## Mandatory order (every debugging / Play verification session)

**Rule:** `ping`, `refresh_wait`, `enter_play`, and other bridge commands require the **TCP listener inside Unity**. That code runs only **after** the bridge `Editor` assembly has **compiled** and domain reload / `InitializeOnLoad` has run. **Never assume `ping` succeeds immediately** after writing the bridge file or after editing scripts — **compile first, then `ping`**.

1. **Ensure the bridge script exists** (run `ensure_unity_editor_bridge.py` below). Read JSON **`action`**: `created` | `already_present`.
2. **Unity Editor** open with that **same project** (user responsibility).
3. **Record the current `Editor.log` baseline** before starting Play/debug actions: read the log path and remember the current line count (or byte offset if line count is impractical). Later log checks for this session must inspect only lines after this baseline, so stale errors or manual clicks from before the test are not counted. On Windows, Unity may hold `Editor.log` with sharing flags that make line-by-line .NET reads fail; in that case use the file length / byte offset (for example Python `os.path.getsize`) and later read from that byte offset.

### A) Cold start — `ensure` just wrote the file (`action: "created"`)

The agent **cannot** call `refresh_wait` or any TCP command yet: Unity has **no listener** until it **imports and compiles** the new `CursorEditorDebugBridge.cs`.

- **Tell the user** to **switch to Unity Editor** with this project, let Unity **detect the new script**, and **wait until script compilation finishes** (progress bar / bottom status). If Unity was on another project or closed, they must **open this project** first. The agent **cannot** trigger Domain Reload from outside in this state.
- **Wait for compile**, then verify:
  - Prefer gentle **`ping`** retries with short backoff (e.g. a few seconds apart, bounded attempts), **or** inspect **`scan_unity_editor_log.py --json`** for fresh **`error CS`** / bridge **`Listening`** lines.
- **Only after** compilation succeeds (no blocking script errors): run **`ping`**. If `connect_failed`, **`scan_unity_editor_log.py --json`**, fix issues, ask the user to let Unity recompile, retry.

### B) Warm bridge — `action: "already_present"` and you only need to confirm Editor

- If Unity should already have the bridge loaded: run **`ping`** after you are confident the project has finished compiling (e.g. user has Unity focused, or prior step succeeded).
- If `connect_failed`, **`scan_unity_editor_log.py --json`**, then align with **A)** (user must get Unity to compile / reload) or **After editing Unity `.cs`** (below) if you just changed scripts.

### C) After the agent edits any `Assets/**/*.cs`

The bridge **must** already be reachable for `refresh_wait` (otherwise you are still in **A)** — user must compile in Unity first).

1. **`refresh_wait`** — forces refresh + compile on the main thread.
2. **`scan_unity_editor_log.py --json`** (large tail, e.g. `--tail 800`).
3. **`ping`** — confirms the bridge is up **after** compile / domain reload (not optional for this workflow).

Then `enter_play` / `menu` / etc. as needed. If `connect_failed` on `refresh_wait`, treat as bridge down → **A)** + log scan.

## Ensure bridge script (auto-create if missing)

- **Target path** (inside the Unity project): `Assets/Scripts/Editor/CursorEditorDebugBridge.cs`  
- **Why under `Editor/`**: uses `UnityEditor`; must not ship to player builds.

Run from shell (replace project root if needed):

```bash
python .cursor/skills/unity-proactive-debug/scripts/ensure_unity_editor_bridge.py
python .cursor/skills/unity-proactive-debug/scripts/ensure_unity_editor_bridge.py /path/to/UnityProject
```

- **Resolution order** for project root: optional CLI argument → env `UNITY_PROJECT_ROOT` → current working directory → walk upward until both `Assets/` and `ProjectSettings/` exist.
- **Output**: one JSON object; `action` is `created` | `already_present`.
- **Conflict**: if that path exists but is not the bridge (missing marker `public static class CursorEditorDebugBridge`), the script exits with error unless you pass **`--force`** (overwrites — use only when intentional).

On Windows PowerShell: `py -3` instead of `python` if needed.

## Prerequisites (agent checklist)

1. **Ensure** step completed for the correct Unity project (see above).
2. **Unity Editor** is running with the **correct project** open (user responsibility).
3. **Editor 调试桥**监听 `127.0.0.1:8742`（脚本编译成功后 `[InitializeOnLoad]` 自动启动）。协议见 [reference.md](reference.md)。
4. **Python 3** on PATH (stdlib only for scripts).

If the bridge is not running after ensure, **tell the user** to open or **focus** Unity and wait for compile. After **`action: "created"`**, spell out that **manual Editor attention** may be required the first time so Unity picks up the new file. Do not assume `-batchmode`.

## Unity Editor.log — automatic error check

When **`connect_failed`**, **`compile_timeout`**, **`refresh_failed`**, or the user asks what Unity is complaining about, **read the Editor log** before guessing.

**Session baseline rule:** At the start of every debugging / Play verification session, record the current `Editor.log` line count before triggering actions. When verifying compile errors, button logs, Play results, or bridge messages, compare only log lines written after that baseline. If the user manually interacts with Unity before the agent starts a test, take a new baseline immediately before the test begins.

**Default paths (do not hardcode `C:\Users\username\...`)**

| OS | Typical path |
|----|----------------|
| Windows | `%LOCALAPPDATA%\Unity\Editor\Editor.log` (e.g. `C:\Users\<you>\AppData\Local\Unity\Editor\Editor.log`) |
| macOS | `~/Library/Logs/Unity/Editor.log` |
| Linux | `~/.config/unity3d/Editor.log` or under `~/.local/share/unity3d/` |

**Override**: set env **`UNITY_EDITOR_LOG`** to the full file path if the log lives elsewhere.

**Agent command** (filters the tail for errors, exceptions, compile failures, and `CursorEditorDebugBridge` lines):

```bash
python .cursor/skills/unity-proactive-debug/scripts/scan_unity_editor_log.py --json
python .cursor/skills/unity-proactive-debug/scripts/scan_unity_editor_log.py --tail 800 --json
python .cursor/skills/unity-proactive-debug/scripts/scan_unity_editor_log.py "D:/path/to/Editor.log"
```

- Use **`--json`** so the agent can parse `matched` lines and `path`.
- Human-readable mode: omit `--json`.
- Optionally use the **Read** tool on the same path for a manual tail if needed.

## After editing Unity `.cs` (mandatory: compile + Editor.log)

Whenever the **agent** changes any file under the Unity project’s **`Assets/`** (especially **`*.cs`**), do **all** of the following before claiming success or running Play/menu tests — **Unity must be open** with that project:

1. **`refresh_wait`** — forces refresh + compile on the main thread (no need to focus the Editor):

```bash
python .cursor/skills/unity-proactive-debug/scripts/editor_bridge_client.py refresh_wait
python .cursor/skills/unity-proactive-debug/scripts/editor_bridge_client.py refresh_wait 180
```

Second form sets max wait **seconds** (default `120`). On success, JSON includes `action: compile_idle`.

2. **`scan_unity_editor_log.py --json`** — immediately after step 1, scan a **large tail** (e.g. `--tail 800`) for **`error CS`** / **`Script Compilation Error`** / **`LogError`** in `matched`. If any **new** compile errors reference the files you edited, **fix them and repeat** steps 1–2 until the tail is clean (ignore stale older lines above the latest successful `Listening` / domain reload block).

3. **`ping`** — **required** after a successful `refresh_wait` + log scan to confirm the bridge is up again after domain reload.

- If **`compile_timeout`**: run **`scan_unity_editor_log.py --json`** first, fix errors, then `refresh_wait` again.
- If the **bridge `.cs` itself** was changed, TCP may drop briefly during reload; retry **`refresh_wait`** then **`ping`** if needed.

Lower-level (manual): `refresh` then poll `compile_status` in a loop.

## How to initiate Play (agent must run the client)

From the **Engine repo root** (or any cwd; use absolute path to the script):

```bash
python .cursor/skills/unity-proactive-debug/scripts/editor_bridge_client.py ping
python .cursor/skills/unity-proactive-debug/scripts/editor_bridge_client.py enter_play
python .cursor/skills/unity-proactive-debug/scripts/editor_bridge_client.py exit_play
```

On Windows PowerShell, same commands if `python` is on PATH; otherwise use `py -3`.

**Important:** `enter_play` / `exit_play` requests are accepted on the Unity main thread, but Play state can change shortly **after** the command response. Do not treat the `isPlaying` value in the immediate `enter_play` response as final. After `enter_play`, run independent `ping` commands with short sleeps until `result.isPlaying == true` (or a bounded timeout), then continue with Play-mode interactions. After `exit_play`, likewise poll `ping` until `result.isPlaying == false`.

Recommended Play start pattern:

```bash
python .cursor/skills/unity-proactive-debug/scripts/editor_bridge_client.py enter_play
# sleep briefly, then repeat ping until isPlaying is true
python .cursor/skills/unity-proactive-debug/scripts/editor_bridge_client.py ping
```

Avoid hiding the transition inside one long shell command that also reads logs and invokes gameplay actions. Prefer small, observable steps: baseline → `enter_play` → poll `ping` to `isPlaying=true` → invoke Play actions → inspect post-baseline logs → `exit_play` → poll `ping` to `isPlaying=false`.

**Environment (optional)**

- `UNITY_EDITOR_BRIDGE_HOST` — default `127.0.0.1`
- `UNITY_EDITOR_BRIDGE_PORT` — default `8742`
- `UNITY_EDITOR_BRIDGE_TOKEN` — if the Unity bridge requires auth, set to the same secret as in Editor

## Editor menus and custom static entry points

**Menus (preferred)** — runs `EditorApplication.ExecuteMenuItem` with the same path string shown in Unity’s menu bar (use `/`):

```bash
python .cursor/skills/unity-proactive-debug/scripts/editor_bridge_client.py menu "Window/General/Test Runner"
```

Check JSON `result.executed`; if `false`, the path is wrong, the item is greyed out, or the menu is provided by a package that is not loaded.

**Static invoke (advanced)** — call a `public` or `private` **static** method with **no parameters** on a resolved type (full name such as `MyApp.EditorTools, Assembly-CSharp-Editor` may be required if `Type.GetType` alone fails; the bridge searches loaded assemblies by `FullName`):

```bash
python .cursor/skills/unity-proactive-debug/scripts/editor_bridge_client.py invoke_static MyNamespace.MyEditorHooks MySetupMethod
```

Prefer adding a `[MenuItem("Tools/MyProject/DoThing")]` wrapper that calls shared code, then use **`menu`** from the agent.

Full wire format for custom tools: `raw` JSON with `args` — see [reference.md](reference.md).

## Interpreting results

- Client prints **one line of JSON** per invocation (pretty-printed for humans).
- `ok: true` → command accepted on the Unity main thread.
- `ok: false` → read `error` / `message`; common causes: bridge off, wrong port, Play mode already in target state, domain reload in progress.
- For state-changing commands such as **`enter_play`** and **`exit_play`**, `ok: true` means the request was accepted, not that the Editor has already reached the target state. Confirm the final state with follow-up **`ping`** results.
- **Ping** / **compile_status** return `result.isCompiling` when bridge supports it (see reference).
- **refresh_wait** returns a single summary object (not the same shape as a raw bridge line).
- **menu** / **invoke_static** return `result` shapes documented in [reference.md](reference.md). For **`menu`**, Unity may return **`ok: true`** but **`result.executed: false`** when the path is invalid or the item cannot run — always check **`executed`**.

## Workflow after code changes

1. **`ensure_unity_editor_bridge.py`** — script on disk (create if missing). Read **`action`**.
2. **If `action` is `created`:** instruct user to **use Unity Editor** (open/focus this project, wait for compile). **Do not** call `refresh_wait` yet. After compile, **`ping`** (with bounded retries / log scan as in **Mandatory order → A**). If `connect_failed`, **`scan_unity_editor_log.py --json`**, fix, user recompiles, retry **`ping`**.
3. **Record `Editor.log` baseline** before the first compile / Play / interaction being verified.
4. **If the agent edited any `Assets/**/*.cs`:** **`refresh_wait`** → **`scan_unity_editor_log.py --tail 800 --json`** (fix **new** `error CS` lines after the baseline and repeat until clean) → **`ping`**.
5. **`enter_play`** — request Play start; then **poll `ping` until `result.isPlaying == true`** before invoking Play-mode test hooks or judging that Play started.
6. User or agent observes logs / behavior; **`exit_play`** when done, then **poll `ping` until `result.isPlaying == false`** before reporting that Play ended.
7. If verification fails (`connect_failed`, `compile_timeout`, etc.), **`scan_unity_editor_log.py --json`** and interpret only post-baseline lines; fix code, then return to step 2 or 4 depending on whether the bridge file was just created or scripts were edited.

For **Editor-only** changes, skip **`enter_play`**; still run **`refresh_wait` → scan → `ping`** when **`.cs`** under `Assets/` was edited.

**Wrong order (avoid):** `ping` immediately after **`ensure`** when **`action` is `created`**, or **`ping`** before **`refresh_wait`** when the agent has just modified scripts.

## Extending commands

New commands require **both** Unity bridge handler and (optional) client subcommands. Protocol and C# notes: [reference.md](reference.md).
