---
name: unity-visualstudio-breakpoint-debug
description: Automates Unity C# breakpoint debugging through Visual Studio Tools for Unity and the Unity Editor bridge. Use when Cursor must force Unity compilation, set Visual Studio breakpoints, guide VSTU Attach to Unity, enter Play via the bridge, capture locals at a breakpoint, then compare against Editor.log.
---

# Unity + Visual Studio 断点调试

## 何时使用

- 用户希望 Cursor 帮忙做 Unity C# 断点调试、抓取局部变量、继续运行。
- 用户提到 Visual Studio 附加 Unity、`F5`、断点、Locals、随机数/日志对比验证。
- 需要把现有 `unity-proactive-debug` 的 Unity bridge 操作和 Visual Studio 调试器自动化串起来。

## 强制顺序

断点调试必须按下面顺序分步执行。不要用一个长命令把步骤合并到一起；每一步都要检查结果，再进入下一步。

1. **修改代码后，必须通过调试桥主动发起编译。** 只要本轮改过 `Assets/**/*.cs`、`.asmdef` 或会影响脚本编译的文件，就先运行：

```powershell
python .cursor/skills/unity-proactive-debug/scripts/editor_bridge_client.py refresh_wait 180
python .cursor/skills/unity-proactive-debug/scripts/scan_unity_editor_log.py --tail 800 --json
python .cursor/skills/unity-proactive-debug/scripts/editor_bridge_client.py ping
```

如果 `refresh_wait` 因 domain reload 断开，等待 Unity 重新加载后重试 `ping` 和 `compile_status`。只有确认 `isCompiling=false` 且 `Editor.log` 没有新的 C# 编译错误，才能继续。

2. **确认 Unity 编译结束后，添加断点。** 此时 Visual Studio 和 Unity 使用的脚本/PDB 才有机会对应。断点用 helper 的 `set-breakpoint`，它会打开文件、跳到目标行并执行 VS 的 `Debug.ToggleBreakpoint`，不要用裸 `Debugger.Breakpoints.Add(...)`：

```powershell
powershell -ExecutionPolicy Bypass -File .cursor/skills/unity-visualstudio-breakpoint-debug/scripts/vs_unity_debug.ps1 set-breakpoint -File "Assets/Scripts/Debug/CursorBreakpointDebugProbe.cs" -Line 8
```

3. **Visual Studio 附加到 Unity 进程。** 必须使用 Visual Studio Tools for Unity 的 `Debug.AttachUnityDebugger` 命令，等价于用户在 VS 里点击“附加到 Unity 进程”。该命令会弹出 Unity 实例选择窗口；agent 不能把普通 `Process.Attach()` 当成等价替代。不要用普通 `Process.Attach()` 做断点调试；它会附加到普通进程调试路径，而不是 VSTU 的 `Unity` AD7 调试引擎，常见结果是 VS 显示“不会命中该断点，没有为该文档加载任何符号”。

```powershell
powershell -ExecutionPolicy Bypass -File .cursor/skills/unity-visualstudio-breakpoint-debug/scripts/vs_unity_debug.ps1 attach-unity
```

如果弹出 Unity 实例选择窗口，用户需要选择当前工程对应的 Unity 实例。只有选择完成、VS 真正进入 Unity 调试会话后，才能继续。

4. **确认断点可以命中。** 在 VS 里检查断点不是空心/警告状态；如果 VS 显示“无法命中断点”，不要调用目标函数。先确认第 3 步不是 generic attach；然后回到第 1 步重新触发 Unity 编译，或让用户手动重新执行 VS 的“附加到 Unity 进程”，再重新设置断点。

5. **通过调试桥执行相应动作，等待断点命中。** 对挂在场景物体上的 `MonoBehaviour` probe，用 bridge 进入 Play，让 `Start()` 触发断点：

```powershell
python .cursor/skills/unity-proactive-debug/scripts/editor_bridge_client.py enter_play
powershell -ExecutionPolicy Bypass -File .cursor/skills/unity-visualstudio-breakpoint-debug/scripts/vs_unity_debug.ps1 wait-break -TimeoutSec 30
```

如果目标不是场景生命周期，而是静态方法，也可以用 `trigger-static` 异步调用。

6. **命中断点后，读取需要观察的局部变量。** 读取完成后再继续运行：

```powershell
powershell -ExecutionPolicy Bypass -File .cursor/skills/unity-visualstudio-breakpoint-debug/scripts/vs_unity_debug.ps1 dump-locals
powershell -ExecutionPolicy Bypass -File .cursor/skills/unity-visualstudio-breakpoint-debug/scripts/vs_unity_debug.ps1 continue
```

## 前置检查

- 先读取并遵循 `.cursor/skills/unity-proactive-debug/SKILL.md`。
- Unity Editor 必须打开当前工程，bridge `ping` 成功。
- Visual Studio 必须打开当前 Unity 解决方案，且安装 Visual Studio Tools for Unity。
- `run-session` 只作为实验性快捷命令保留；演示和排错时必须使用上面的分步流程。
- `-UseGenericAttach` 只用于诊断，不用于验证 Unity C# 断点命中。

## 验证随机数

推荐测试脚本是 `Assets/Scripts/Debug/CursorBreakpointDebugProbe.cs`。它必须在非 `Editor/` 目录中，编译进 `Assembly-CSharp`；不要放在 `Assets/Scripts/Editor/`，否则会进入 `Assembly-CSharp-Editor`，VSTU 断点符号绑定可能和运行时脚本不一致。

1. 把 `CursorBreakpointDebugProbe` 挂到场景中的 GameObject。
2. 把断点打在 `Debug.Log(...)` 行，使 `randomValue` 已经生成但还没打印。
3. bridge 执行 `enter_play`。
4. `dump-locals` 输出里读取 `randomValue`。
5. `continue` 后扫描新的 `Editor.log`，找到 `[CursorBreakpointDebugProbe] randomValue=...`。
6. 对比断点抓到的 `randomValue` 和日志打印值；一致则验证通过。

## 失败处理

- `no_dte`: 让用户打开 Visual Studio 2022 和当前 Unity 解决方案后重试。
- `no_unity_process`: 让用户打开 Unity Editor 当前工程后重试。
- `attach_failed`: 可先在 VS 手动执行 `Debug > Attach Unity Debugger`，再继续 `wait-break`。
- `wait_timeout`: 断点行没有被执行，检查文件/行号、是否已附加、bridge 调用是否成功。
- `locals_failed`: 调试器未处于 break mode，先运行 `wait-break` 或确认 VS 是否停在断点。
- 断点提示未加载符号：确认脚本不在 `Editor/` 目录，且 `.csproj` 中属于 `Assembly-CSharp`；如果属于 `Assembly-CSharp-Editor`，优先把测试脚本移到运行时目录。

更多 EnvDTE 细节见 [reference.md](reference.md)。
