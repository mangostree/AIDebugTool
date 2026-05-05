# Unity + Visual Studio breakpoint debug reference

## Components

- Unity bridge: `.cursor/skills/unity-proactive-debug/scripts/editor_bridge_client.py`
- Visual Studio automation: `scripts/vs_unity_debug.ps1`
- Demo entry point: `Assets/Scripts/Debug/CursorBreakpointDebugProbe.cs`

The Unity bridge triggers code on the Unity main thread. Visual Studio EnvDTE controls the debugger: breakpoints, process attach, current stack frame locals, and continue.

## Visual Studio Tools for Unity attach

Manual **Attach to Unity** is implemented by Visual Studio Tools for Unity, not by generic process attach.

From the installed VSTU package registration:

- Engine name: `Unity`
- Engine GUID: `{F18A0491-A310-4822-B12F-12CC30404EEC}`
- Engine class: `SyntaxTree.VisualStudio.Unity.Debugger.UnityEngine`
- Program provider: `SyntaxTree.VisualStudio.Unity.Debugger.UnityProgramProvider`

`Debug.AttachUnityDebugger` invokes the VSTU attach command and may show a Unity instance picker. Generic `Process.Attach()` does not select this Unity debug engine and can leave Unity C# symbols unloaded.

## EnvDTE actions

- Locate Visual Studio: `Marshal.GetActiveObject("VisualStudio.DTE.17.0")`, with older-version fallbacks.
- Set breakpoint: open the source file, move the active selection to the line, then execute `Debug.ToggleBreakpoint`. This matched the successful test; raw `DTE.Debugger.Breakpoints.Add("", file, line)` returned unreliable counts and should not be the default path.
- Attach: prefer `DTE.ExecuteCommand("Debug.AttachUnityDebugger")`, the same Visual Studio Tools for Unity command as the manual "Attach to Unity" action.
- Generic attach: `DTE.Debugger.LocalProcesses` + `Process.Attach()` is only a diagnostic fallback and should not be used to validate Unity C# breakpoints.
- Wait break: poll `DTE.Debugger.CurrentMode` until break mode.
- Locals: read `DTE.Debugger.CurrentStackFrame.Locals`.
- Continue: call `DTE.Debugger.Go()`.

## Limitations

- Visual Studio must be running in the same user session as Cursor.
- Unity must be launched with script debugging support available through Visual Studio Tools for Unity.
- `Process.Attach()` can attach Visual Studio with the wrong debug engine for Unity C# scripts. If breakpoints say no symbols are loaded, use `Debug.AttachUnityDebugger` or the manual `Debug > Attach Unity Debugger` command.
- Reading large Unity objects can be slow. The helper limits nested member traversal by depth and count.
- If a breakpoint is on a line after `Debug.Log`, the log may already be written before locals are captured. For value comparison, break on the `Debug.Log` line itself.
- Runtime breakpoint probes should live outside `Assets/**/Editor/` so they compile into `Assembly-CSharp`. A probe under `Editor/` compiles into `Assembly-CSharp-Editor`; in the verified workflow, VSTU bound runtime `Assembly-CSharp` breakpoints reliably while the editor assembly probe showed unloaded-symbol warnings.

## Required debug sequence

1. If code changed, force Unity compilation through the bridge and wait until it is idle:

```powershell
python .cursor/skills/unity-proactive-debug/scripts/editor_bridge_client.py refresh_wait 180
python .cursor/skills/unity-proactive-debug/scripts/scan_unity_editor_log.py --tail 800 --json
python .cursor/skills/unity-proactive-debug/scripts/editor_bridge_client.py ping
```

2. Confirm Unity reports `isCompiling=false` and `Editor.log` has no new C# compiler errors.
3. Set a Visual Studio breakpoint at the `Debug.Log` line in `CursorBreakpointDebugProbe.Start`.
4. Attach Visual Studio to Unity using `Debug.AttachUnityDebugger`, not generic process attach.
5. Confirm the breakpoint is bound in Visual Studio. If it is hollow or says it cannot be hit, stop and return to step 1; do not invoke the target method yet.
6. Enter Play through the bridge:

```powershell
python .cursor/skills/unity-proactive-debug/scripts/editor_bridge_client.py enter_play
```

7. Wait for break mode and dump locals.
8. Continue execution.
9. Scan fresh `Editor.log` lines and compare the logged `randomValue` with the dumped local variable.

## Verified result

The verified demo used `Assets/Scripts/Debug/CursorBreakpointDebugProbe.cs`, attached through VSTU's Unity instance picker, entered Play through the Unity bridge, hit `Start()` at the `Debug.Log` line, dumped `randomValue`, continued, then matched the same value in `Editor.log`.
