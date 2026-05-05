# Agent Context

## Project Background

This Unity project is a demo for presenting a Cursor-assisted Unity development workflow. The goal is to show how Cursor Agent can work with a running Unity Editor and a Perforce workspace, not to showcase final gameplay content.

The project demonstrates:

- Project-level Cursor skills stored under `.cursor/skills`.
- Persistent Cursor rules stored under `.cursor/rules`.
- Perforce-aware file editing through a specified pending changelist.
- Unity Editor automation through a local TCP debug bridge.
- Script refresh, compile verification, Editor log scanning, Play mode control, and JSON-based static reflection calls from the agent.
- A repeatable button-click Play mode test that validates five UI button logs through `Editor.log`.
- A Unity + Visual Studio breakpoint debugging workflow that captures a local variable at a managed C# breakpoint and verifies it against `Editor.log`.

## Demo Goal

When this project is opened in Cursor, the agent should understand that it is helping with a Unity tooling demo. Prefer workflows that make collaboration visible and repeatable:

- Explain the next automation step briefly before running it.
- Use the project skills instead of ad hoc commands when they apply.
- Keep changes scoped and easy to demonstrate.
- Verify Unity script changes through the debug bridge when possible.
- Keep Perforce changelist handling explicit.
- For README/demo documentation, present the recommended demo flow as user prompts first, then Unity bridge details, then P4 details.

Recommended demo prompts:

1. Ask the agent to enable `.cursor/skills/unity-proactive-debug` and test the Unity bridge connection.
2. Ask the agent to use CL `107`, modify a Unity script, and check Unity compilation after the change.
3. Ask the agent to call Play, let the editor run for 3 seconds, then stop.
4. Ask the agent to restart Play, click the five scene buttons, check `Editor.log`, and stop after all five logs are confirmed.
5. Ask the agent to run the Visual Studio breakpoint debug test: set a breakpoint in `CursorBreakpointDebugProbe.Start()`, attach Visual Studio to Unity, enter Play through the bridge, capture `randomValue`, continue, and compare it with `Editor.log`.

## Active Project Skills

### `.cursor/skills/p4-edit-add-before-modify`

Use this before modifying or adding files in the Perforce workspace.

Expected flow:

1. Ask for or reuse the pending changelist number for the current task.
2. Check `p4 info` before interpreting workspace errors.
3. Resolve the correct `P4CLIENT` from the target file paths.
4. Open existing files with `p4 edit -c <CL>`.
5. Add new files with `p4 add -c <CL>`.
6. Reopen files into the target CL when they are already opened elsewhere, but ask before moving files from another user-selected CL.

Current demo context:

- The project workspace root is `D:\unityproj\AIDebugTool`.
- The matching Perforce client observed in this demo is `zhaoxiaoyuan_AIDebugTool`.
- Recent demo CL used by the user: `107`.

### `.cursor/skills/unity-proactive-debug`

Use this for proactive Unity Editor verification.

Expected flow:

1. Ensure the bridge exists with `ensure_unity_editor_bridge.py`.
2. If the bridge file was just created, ask the user to let Unity compile it before TCP commands can work.
3. Once the bridge is reachable, use `editor_bridge_client.py ping` to confirm Unity state.
4. After editing `Assets/**/*.cs`, run `refresh_wait`, scan `Editor.log`, then run `ping` again.
5. Use `enter_play` and `exit_play` when visual Play mode validation is part of the task.
6. Use `invoke_static_json` when a test needs to call a static method with JSON-described parameters.

Current demo context:

- The bridge script path is `Assets/Scripts/Editor/CursorEditorDebugBridge.cs`.
- The bridge listens on `127.0.0.1:8742` after Unity compiles and loads it.
- This demo has successfully pinged Unity `2022.3.62f2c1`.
- The bridge supports `invoke_static_json` for static reflection calls with primitive parameters and common Unity structs such as `Vector2`, `Vector3`, `Vector4`, and `Color`.
- The scene button test calls `ButtonDebugList.GetDebugButtonCount()` and then `ButtonDebugList.ClickDebugButton(0)` through `ButtonDebugList.ClickDebugButton(4)`.
- For the button test, record the `Editor.log` baseline before entering Play and only count logs after that baseline. Expected logs are `Button 1 On Click` through `Button 5 On Click`, with stack traces showing `ButtonDebugList:ClickDebugButton (int)`.
- If Play mode or script compilation triggers Domain Reload, wait for the bridge to recover and confirm with `ping` before continuing.

### `.cursor/skills/unity-visualstudio-breakpoint-debug`

Use this for Unity C# breakpoint debugging through Visual Studio Tools for Unity and the Unity bridge.

Expected flow:

1. After any Unity script change, force Unity compilation through the bridge, scan `Editor.log`, and confirm `ping` reports `isCompiling=false`.
2. Set the Visual Studio breakpoint only after Unity compilation is complete.
3. Use Visual Studio Tools for Unity's Attach to Unity flow, not generic `Unity.exe` process attach.
4. Confirm the breakpoint is bound and does not show an unloaded-symbol warning before triggering code.
5. Enter Play or invoke the target method through the Unity bridge.
6. Wait for Visual Studio break mode, dump locals, then continue execution.
7. Compare captured locals with fresh `Editor.log` output when the test expects logged data.

Current demo context:

- The verified breakpoint probe is `Assets/Scripts/Debug/CursorBreakpointDebugProbe.cs`.
- The probe must stay outside `Assets/**/Editor/` so it compiles into `Assembly-CSharp`; an Editor-only probe compiles into `Assembly-CSharp-Editor` and may show unloaded-symbol warnings in this demo.
- The probe is a `MonoBehaviour`; attach it to a scene GameObject, set a breakpoint on its `Debug.Log(...)` line in `Start()`, then enter Play through the bridge.
- Use the helper's `set-breakpoint` command, which opens the file, jumps to the line, and runs Visual Studio's `Debug.ToggleBreakpoint`.
- Visual Studio Tools for Unity's attach command may open a Unity instance picker. The user must select the current Unity instance for this project.
- A successful verified run captured `randomValue` at the breakpoint and matched the same value in `[CursorBreakpointDebugProbe] randomValue=...` in `Editor.log`.

## Default Agent Behavior

- Treat this as a Unity project with Perforce integration.
- Follow the C# brace rule in `.cursor/rules/csharp-if-braces.mdc`.
- Do not revert user changes unless explicitly requested.
- Prefer `README.md` for user-facing documentation and this file for project-agent context.
- For persistent Cursor behavior, mirror important guidance into `.cursor/rules/*.mdc` with `alwaysApply: true`.
