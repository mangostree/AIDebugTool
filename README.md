# AIDebugTool

这是一个用于展示 Cursor 协同 Unity 开发工作流的 Demo 工程。重点不是游戏内容本身，而是演示 Cursor Agent 如何结合项目内的 `.cursor/skills`，完成 Perforce 文件管理、Unity Editor 编译验证、Play 模式控制和日志诊断等自动化协作任务。

## 当前 Skills

本工程当前包含以下项目级 skill：

- `.cursor/skills/p4-edit-add-before-modify`
- `.cursor/skills/unity-proactive-debug`
- `.cursor/skills/unity-visualstudio-breakpoint-debug`

这些 skill 会在合适的任务中指导 Agent 使用项目约定的流程，而不是临时拼命令。

## 推荐演示流程

演示前，先用 Unity Editor 打开本工程。之后可以按下面的顺序向 Agent 发出请求：

1. 「帮我启用 `.cursor/skills/unity-proactive-debug`，测试当前 Unity 调试桥连接状态。」
2. 「使用 CL 107，帮我修改一个 Unity 脚本，并在修改后检查 Unity 编译结果。」
3. 「调用 Play()，让编辑器跑起来，跑 3 秒后关闭。」
4. 「调用 Play()，执行README中的按钮日志测试用例，依次按下场景里的 5 个按钮，然后检查 `Editor.log`，确认 5 个按钮的日志都收到后结束 Play。」
5. 「执行 README 中的 Visual Studio 断点调试测试用例，进入 Play 后命中 `CursorBreakpointDebugProbe.Start()`，读取 `randomValue`，并和 `Editor.log` 对比。」

## Unity 主动调试桥

Skill 路径：`.cursor/skills/unity-proactive-debug`

这个 skill 用于让 Cursor Agent 与正在运行的 Unity Editor 建立本地 TCP 调试桥。桥接脚本位于：

```text
Assets/Scripts/Editor/CursorEditorDebugBridge.cs
```

调试桥监听本机地址 `127.0.0.1:8742`。脚本编译并被 Unity Editor 加载后，Agent 就可以通过本地客户端向 Unity 发送命令。

主要功能：

- 自动确保调试桥脚本存在。
- `ping` Unity Editor，确认工程、Unity 版本、Play 状态和编译状态。
- 主动触发 `AssetDatabase.Refresh` 和脚本编译。
- 等待 Unity 编译结束，并在结束后再次确认桥接状态。
- 扫描 Unity `Editor.log`，辅助诊断编译错误、异常和桥接连接失败。
- 进入或退出 Play 模式。
- 执行 Unity 菜单项。
- 调用自定义 Editor 静态方法。

## 按钮日志测试用例

场景中放置了 5 个按钮，每个按钮挂载 `DebugLogButton`。按钮会在点击时输出各自的 `logMsg`，预期日志为：

```text
Button 1 On Click
Button 2 On Click
Button 3 On Click
Button 4 On Click
Button 5 On Click
```

测试目标：

1. 通过调试桥进入 Play 模式。
2. 等待桥在 Play 模式中恢复，并确认 `isPlaying=true`。
3. 调用 `ButtonDebugList.GetDebugButtonCount()`，确认返回 `5`。
4. 使用 `invoke_static_json` 依次调用 `ButtonDebugList.ClickDebugButton(0)` 到 `ButtonDebugList.ClickDebugButton(4)`。
5. 读取本次 Play 开始后的 `Editor.log`，确认 5 条按钮日志都出现。
6. 退出 Play 模式，并最终确认 `isPlaying=false`。

执行前先记录当前 `Editor.log` 的最后行号或时间戳，避免把人工点击或旧日志算入结果。本 Demo 验证时，以重新 `enter_play` 前的日志行作为基准，只统计基准之后新增的日志。

一次成功结果应满足：

- `GetDebugButtonCount()` 返回 `5`。
- 5 次 `ClickDebugButton` 响应均为 `ok: true`。
- `Editor.log` 中基准行之后出现 `Button 1 On Click` 到 `Button 5 On Click`。
- 每条日志调用栈中能看到 `ButtonDebugList:ClickDebugButton (int)`，说明是调试桥反射调用触发，而不是手动点击。

首次创建桥接脚本后，需要让 Unity Editor 获得一次编译机会。桥接脚本只有在 Unity 编译并完成 Domain Reload 后，才会启动 TCP listener。

## Visual Studio 断点调试测试用例

Skill 路径：`.cursor/skills/unity-visualstudio-breakpoint-debug`

场景中放置一个挂载 `CursorBreakpointDebugProbe` 的 GameObject。脚本位于：

```text
Assets/Scripts/Debug/CursorBreakpointDebugProbe.cs
```

该脚本必须位于非 `Editor/` 目录，确保编译进运行时程序集 `Assembly-CSharp`。本 Demo 曾验证过：如果测试脚本放在 `Assets/Scripts/Editor/`，会进入 `Assembly-CSharp-Editor`，Visual Studio Tools for Unity 附加后可能出现“不会命中该断点，没有为该文档加载任何符号”。

测试目标：

1. 通过调试桥主动触发 Unity 编译，并确认 `isCompiling=false`。
2. 在 Visual Studio 中给 `CursorBreakpointDebugProbe.Start()` 的 `Debug.Log(...)` 行打断点。
3. 使用 Visual Studio Tools for Unity 的“附加到 Unity”流程附加当前 Unity 实例。
4. 确认断点是实心可命中状态。
5. 通过调试桥执行 `enter_play`。
6. 等待 Visual Studio 命中断点。
7. 在断点处读取局部变量 `randomValue`。
8. 继续运行后扫描 `Editor.log`，确认日志里的随机数与断点读取值一致。

成功时，`dump-locals` 能读到类似：

```text
randomValue = 611296
```

继续运行后，`Editor.log` 中应出现对应日志：

```text
[CursorBreakpointDebugProbe] randomValue=611296
```

一次成功结果应满足：

- Visual Studio 断点不显示未加载符号。
- `wait-break` 返回 `debuggerMode: "break"`。
- `dump-locals` 中存在 `randomValue`，类型为 `int`。
- `continue` 后，`Editor.log` 中最新的 `[CursorBreakpointDebugProbe] randomValue=...` 与断点读取值一致。

注意：Visual Studio 的“附加到 Unity”不是普通 `Unity.exe` 进程附加。它使用 Visual Studio Tools for Unity 的 Unity 调试引擎，并可能弹出 Unity 实例选择窗口；演示时需要选择当前工程对应的 Unity 实例。

成功时会返回类似信息：

```json
{
  "ok": true,
  "result": {
    "unityVersion": "2022.3.62f2c1",
    "isPlaying": false,
    "isCompiling": false
  }
}
```

连接测试、主动刷新编译、日志扫描、Play 模式控制、菜单执行和静态方法调用都由 skill 内部处理；演示者只需要向 Agent 提出目标。

## 调试桥核心逻辑

调试桥由 `Assets/Scripts/Editor/CursorEditorDebugBridge.cs` 提供，属于 Editor-only 脚本，不会进入 Player 构建。它使用 `[InitializeOnLoad]` 在 Unity Editor 域加载后启动本地 TCP listener：

```text
127.0.0.1:8742
```

Agent 侧发送一行 JSON 请求，Unity 侧返回一行 JSON 响应。所有实际 Unity API 调用都会被投递回 Editor 主线程执行，避免在 socket 后台线程里直接访问 Unity 对象。

核心数据流：

```text
Cursor Agent / Python client
  -> localhost TCP JSON
  -> CursorEditorDebugBridge socket thread
  -> PendingWork queue
  -> EditorApplication.update
  -> Unity main thread command execution
  -> JSON response
```

当前桥接命令包括：

- `ping`：读取 Unity 版本、Play 状态和编译状态。
- `refresh` / `refresh_wait`：触发 `AssetDatabase.Refresh` 和 `CompilationPipeline.RequestScriptCompilation`，并等待编译空闲。
- `enter_play` / `exit_play`：进入或退出 Play 模式。
- `menu`：执行 Unity 菜单项。
- `invoke_static`：调用无参静态方法。
- `invoke_static_json`：通过 JSON 描述参数并反射调用静态方法。

`invoke_static_json` 用于更通用的调试入口，例如调用 `ButtonDebugList.ClickDebugButton(int)`：

```json
{
  "cmd": "invoke_static_json",
  "args": {
    "typeName": "ButtonDebugList",
    "methodName": "ClickDebugButton",
    "parameters": [
      { "type": "int", "value": "0" }
    ]
  }
}
```

目前支持的参数类型：

- `int`、`long`、`float`、`double`、`bool`、`string`、`null`
- `Vector2`、`Vector3`、`Vector4`、`Color`
- 基础类型的一维数组

Play 模式和脚本编译都可能触发 Domain Reload。桥接脚本会在 Assembly Reload、进入 Play、回到 Edit Mode 等生命周期点重新安排 listener 启动；测试时仍建议在关键动作后执行一次 `ping`，确认桥已恢复。

## P4 文件打开与新增

Skill 路径：`.cursor/skills/p4-edit-add-before-modify`

这个 skill 用于 Perforce 工作区里的文件修改和新增。它的核心目标是：在写入文件前，先确保目标文件已经被正确打开到指定 pending changelist 中。

主要功能：

- 修改已有 depot 文件前，执行 `p4 edit -c <CL>`。
- 新增文件时，执行 `p4 add -c <CL>`。
- 如果文件已经打开在目标 CL 中，则跳过。
- 如果文件已经打开在其他 CL 或 default changelist 中，则使用 `p4 reopen -c <CL>` 移动。
- 在打开文件前先检查 `p4 info`，避免把连接失败误判成 workspace 问题。
- 根据目标文件路径解析正确的 `P4CLIENT`，避免使用当前 shell 中不匹配的默认 client。

常见使用案例：

```text
用户：帮我修改 Assets/Scripts/Foo.cs，使用 CL 107。
Agent：
1. 检查 P4 连接状态。
2. 根据 Foo.cs 所在路径解析正确 workspace。
3. 将 Foo.cs 打开到 CL 107。
4. 再应用代码修改。
```

本 Demo 中已经验证过 `p4 info` 可以连接到 Perforce Server，并且本工程对应的 workspace 为 `zhaoxiaoyuan_AIDebugTool`。

## 使用边界

- `unity-proactive-debug` 面向正常带界面的 Unity Editor，不用于 headless batchmode CI。
- 首次创建桥接脚本时，Agent 不能在桥尚未编译加载前主动触发 Unity 编译，需要 Unity Editor 自己先导入并编译该脚本。
- `refresh_wait` 依赖调试桥已经可连接，适用于桥已经加载后的后续脚本修改。
- `p4-edit-add-before-modify` 需要一个明确的 pending changelist 号码。
- 如果 P4 连接失败，应先确认 `P4PORT`、VPN 和 `p4 login` 状态，再处理 workspace 或 client 映射问题。
