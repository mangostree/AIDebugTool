# Unity Editor bridge — protocol and Unity side

## Transport

- **Bind**: `127.0.0.1` only (not `0.0.0.0`).
- **Port**: default `8742` (configurable via `EditorPrefs` or constant; must match `UNITY_EDITOR_BRIDGE_PORT`).
- **Framing**: one UTF-8 line per request, one line per response (newline `\n`), JSON objects.

## Request JSON

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `id` | string | recommended | Correlation id echoed in response. |
| `cmd` | string | yes | `ping`, `refresh`, `compile_status`, `menu`, `invoke_static`, `enter_play`, `exit_play`, … |
| `args` | object | no | Command-specific parameters. |
| `auth` | string | if enabled | Must match server token. |

## Response JSON

| Field | Type | Description |
|-------|------|-------------|
| `id` | string | Same as request when provided. |
| `ok` | bool | Whether the command completed successfully. |
| `result` | object | Optional payload (`unityVersion`, `isPlaying`, `isCompiling`, …). |
| `error` | string | Short code when `ok` is false. |
| `message` | string | Human-readable detail. |

## Commands (v1)

### `ping`

- **Unity**: No mode change.
- **result**: `unityVersion`, `isPlaying`, `isCompiling`.

### `compile_status`

- **Unity**: No mode change; read-only snapshot.
- **result**: same shape as `ping` (use `isCompiling`).

### `refresh`

- **Unity**: `AssetDatabase.Refresh(ImportAssetOptions.Default)` then `CompilationPipeline.RequestScriptCompilation(None)` on the main thread — forces asset import and script compile **without** needing Editor window focus.
- **result**: same shape as `ping` immediately after the calls (`isCompiling` may still become true on the next Editor tick).

### `enter_play`

- **Unity**: On main thread, call `EditorApplication.EnterPlaymode()` (Unity 2019.3+) or legacy equivalent; handle “already playing” as `ok: true` or `ok: false` with clear `error` — pick one convention and keep it stable.

### `exit_play`

- **Unity**: On main thread, exit Play mode if playing; if not playing, return `ok: true` (no-op).

### `menu`

- **Unity**: `EditorApplication.ExecuteMenuItem(args.path)` on the main thread. Path uses `/` (backslashes are normalized).
- **args**: `path` (string), e.g. `"Window/General/Test Runner"`.
- **result**: `menuPath`, `executed` (bool). `executed == false` means the menu path was not found or the item was disabled / context rejected it (Unity returns `false`).

### `invoke_static`

- **Unity**: Reflection — resolve `args.typeName` (prefer **full type name** `Namespace.Type`) in loaded assemblies, then invoke a **static** method `args.methodName` with **zero parameters**. Return value is ignored; exceptions become `invoke_exception`.
- **args**: `typeName`, `methodName`.
- **result**: `typeName` (resolved full name), `methodName`, `invoked: true`.
- **Security**: same trust model as the rest of the bridge (localhost + optional token). Prefer **`menu`** for built-in Editor actions; use **`invoke_static`** only for your own `Editor` static entry points when no menu exists.

## Unity implementation notes

1. **Threading**: accept sockets on a background thread; **marshal** command execution to the main thread (e.g. queue + `EditorApplication.update`).
2. **Domain reload**: stop listener or rebind after reload; avoid executing while reload is unstable. The bridge uses `EditorApplication.delayCall` before `Start()` so the previous socket can be released, and `ExclusiveAddressUse = false` plus `Dispose()` on shutdown to reduce “address already in use” on Windows.
3. **Port still in use**: only one listener per port — close duplicate Unity Editor windows or other tools bound to `8742` (`netstat -ano | findstr 8742` on Windows).
4. **Security**: localhost only; optional shared `auth` token from env / EditorPrefs.

## Editor.log

Unity writes a machine-wide Editor log (not inside the project). For failure analysis, use **`scripts/scan_unity_editor_log.py`** or read the file documented in [SKILL.md](SKILL.md) (`%LOCALAPPDATA%\Unity\Editor\Editor.log` on Windows, `UNITY_EDITOR_LOG` override).

## File placement in a game project

Default path used by this skill: **`Assets/Scripts/Editor/CursorEditorDebugBridge.cs`**.

The agent should run **`scripts/ensure_unity_editor_bridge.py`** before debugging; it creates that file from **`templates/CursorEditorDebugBridge.cs`** if missing. Keep the template in sync when extending bridge commands.

If your team uses a different folder, either move the generated file and adjust the ensure script, or add an asmdef under `Assets/Scripts/Editor` that references `UnityEditor` only.
