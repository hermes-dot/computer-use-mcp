<div align="center">

# codex-computer-use-mcp

**让 AI 直接操作 Windows 桌面应用 —— 基于 UI Automation 无障碍树 + SendInput + 窗口截图**

一个 Model Context Protocol (MCP) server，让模型"看得见结构、点得准位置"。

[English](README.md) · 中文

</div>

---

## 这是什么

Codex 的 computer use 之所以比"截图 + 猜坐标"可靠得多，关键在于它把 **UI Automation 无障碍树**暴露给模型：

```
0 Window "无标题 - 记事本"
	1 MenuBar "应用程序"
		2 MenuItem "文件" actions=[Expand]
		3 MenuItem "编辑" actions=[Expand]
	4 Edit "文本编辑器" value="" focusable FOCUSED actions=[SetValue, SelectText]
	5 StatusBar "第 1 行，第 1 列"
```

模型看到的是**带索引的控件清单**，而不是一张需要猜坐标的图片。于是它可以说"点击元素 2"，
而不是"点击 (412, 37) —— 希望那里是文件菜单"。

本项目是对 OpenAI Codex computer use（`@oai/sky` + `codex-computer-use` 原生组件）的
**干净重写（clean-room）实现**：只参考其公开的 API 语义与工具设计，代码全部独立编写，
不包含任何 OpenAI 的专有代码或二进制。原生层用 .NET Framework 的 UI Automation 重新实现。

## 特性

- **无障碍树优先**：每个控件带稳定索引，模型按索引寻址，不受布局微调影响
- **被遮挡窗口也能截**：优先 `PrintWindow`，失效时自动激活窗口后抓屏
- **坐标安全**：截图带 `screenshot_id`，坐标自动按缩放比例与窗口位置换算；落点若被其他窗口遮挡，**直接报错而不是点错地方**
- **中文输入**：走 `KEYEVENTF_UNICODE`，不依赖键盘布局，中文/emoji 都能输入
- **零第三方依赖**：原生层只用 .NET Framework 自带的 UI Automation 与 GDI+，不需要 NuGet、不需要 Rust/C++ 工具链
- **可测试**：自带结构已知的 WinForms 测试靶程序，30 项端到端断言覆盖点击、输入、错误契约

## 架构

```
┌─────────────────────────────┐
│  MCP 客户端                  │   DeepSeek Harness / Claude Desktop / Cursor ...
│  （模型 + 工具调度）          │
└──────────────┬──────────────┘
               │ MCP over stdio (JSON-RPC)
┌──────────────▼──────────────┐
│  MCP server (Node/TypeScript)│   src/  —— 工具 schema、参数映射、截图编码
│  list_apps / click / ...     │
└──────────────┬──────────────┘
               │ NDJSON over 子进程管道
┌──────────────▼──────────────┐
│  原生 helper (C#, .NET FW)   │   native/windows/  —— cu-helper.exe
│  ├ UI Automation → 无障碍树  │
│  ├ SendInput     → 鼠标键盘  │
│  ├ PrintWindow/  → 窗口截图  │
│  │  BitBlt                   │
│  └ DWM           → 窗口几何  │
└──────────────┬──────────────┘
               │ Win32 API
        ┌──────▼──────┐
        │  目标应用    │
        └─────────────┘
```

为什么拆成两个进程：

- UI Automation 是 COM 接口、`SendInput` 需要精确的结构体布局 —— 这些在 .NET Framework 上零依赖可用，而 Node 侧做不了；
- Node 侧负责 MCP 协议与工具编排，生态成熟；
- 进程边界让两侧解耦，helper 崩溃不会拖垮 MCP server（会自动重启并给出可读错误）。

## 安装

### 前置要求

| 组件 | 要求 | 说明 |
|---|---|---|
| Windows | 10 1809+ / 11 | 需要 UI Automation 与 Per-Monitor-V2 DPI |
| Node.js | 18+ | 运行 MCP server |
| .NET SDK | 8+ | 仅构建原生 helper 时需要（提供 Roslyn 编译器） |
| .NET Framework | 4.7.2+ | Windows 自带，提供 `UIAutomationClient` |

### 构建

```powershell
git clone <this-repo>
cd codex-computer-use-mcp

npm install          # 安装 MCP SDK 与 TypeScript
npm run build        # 编译 TypeScript -> dist/
npm run build:native # 编译原生 helper -> native/windows/bin/cu-helper.exe
```

验证原生依赖是否就绪：

```powershell
node dist/index.js --check
# helper: ...\native\windows\bin\cu-helper.exe
# ping: ok (dpi awareness = 2)
# targetable windows: 5
```

## 接入 DeepSeek Harness

DSH 通过 cordis profile 的补丁层配置 MCP server。把下面这段**追加**到
`%USERPROFILE%\.dsh\profiles\desktop\cordis.patch.yml`：

```yaml
- insert:
    - id: mcp-computer-use
      name: '@deepseek-ai/dsh-mcp-client'
      config:
        serverName: computer_use
        transport: stdio
        command: 'node'
        args:
          - 'C:\path\to\codex-computer-use-mcp\dist\index.js'
        env:
          # 截图落盘目录；省略时用 %LOCALAPPDATA%\cu-helper
          CU_HELPER_CACHE_DIR: 'C:\path\to\cache\computer-use'
        toolCallTimeoutMs: 60000
        failOnStartupError: false
```

要点：

- **必须包在 `- insert:` 里**。DSH 的补丁层把顶层裸写的 `id` 当作"覆盖已有条目"，新条目会被静默忽略。
- `serverName` 只能含 `[A-Za-z0-9_-]`，最终工具名为 `mcp__computer_use__<tool>`。
- 保存后**无需重启**，DSH 会热重载补丁文件。

## 接入其他 MCP 客户端

<details>
<summary>Claude Desktop / Cursor / 其他（stdio 配置）</summary>

```json
{
  "mcpServers": {
    "computer-use": {
      "command": "node",
      "args": ["C:\\path\\to\\codex-computer-use-mcp\\dist\\index.js"],
      "env": {
        "CU_HELPER_CACHE_DIR": "C:\\path\\to\\cache\\computer-use"
      }
    }
  }
}
```

</details>

## 工具参考

| 工具 | 作用 | 只读 |
|---|---|---|
| `list_apps` | 列出有可见窗口的应用及其窗口 | ✅ |
| `list_windows` | 列出可操作的顶层窗口 | ✅ |
| `get_window` | 按 id 重新读取窗口（找回失效绑定） | ✅ |
| `get_window_state` | **核心**：抓取无障碍树 + 截图 | ✅ |
| `get_screenshot` | 抓取整个虚拟桌面 | ✅ |
| `launch_app` | 启动应用 | |
| `activate_window` | 把窗口带到前台并交出焦点 | |
| `click` | 按元素索引或截图坐标点击 | |
| `type_text` | 输入文本（Unicode，支持中文） | |
| `press_key` | 按键或和弦，如 `Control_L+a` | |
| `scroll` | 在指定点滚动 | |
| `set_value` | 直接改写可编辑元素的值 | |
| `drag` | 拖拽 | |
| `perform_secondary_action` | 执行次要动作（Expand/Collapse/Toggle/Select...） | |

### 典型工作流

```
1. list_apps                      → 找到目标应用与 window_id
2. get_window_state(window_id)    → 拿到无障碍树（带元素索引）+ 截图
3. click(element_index=12)        → 按索引点击，不受布局微调影响
4. get_window_state(window_id)    → 重新抓取，确认操作生效
```

`get_window_state` 是昂贵的即时快照：**批量执行相关动作，再抓一次确认**。
元素索引只在产生它的那次快照内有效 —— 界面一变就要重新抓取。

## 工作原理

### 无障碍树与元素索引

`get_window_state` 用 UI Automation 的 **ControlView** 遍历窗口，跳过离屏元素，
为每个可见元素分配一个自增索引，并缓存索引 → `AutomationElement` 的映射。
后续 `click(element_index=N)` 直接取缓存元素、读它的屏幕矩形、点中心 —— 不需要坐标。

树里每行的格式：

```
<tab 缩进><索引> <控件类型> "<名称>" value="<当前值>" <状态标记> actions=[<可用动作>]
```

状态标记包含 `disabled` / `focusable` / `FOCUSED` / `required`；
`actions` 列出该元素支持的次要动作名，可直接传给 `perform_secondary_action`。

### 坐标与缩放

截图默认按最长边缩放到 1600px 以内，返回的 `scale` 表示"图像像素 / 物理像素"。
模型从图上读出的坐标是图像像素，helper 内部按 `scale` 换算回物理坐标，
再加上窗口原点得到屏幕坐标。传入 `screenshot_id` 时会校验它确实是该窗口最近一次截图。

### 输入安全

每次输入前都会：

1. 确保目标窗口在前台（`AttachThreadInput` + `SetForegroundWindow`，绕过前台锁定）；
2. 用 `WindowFromPoint` 校验落点确实属于目标窗口 —— 若被开始菜单、悬浮窗或模态框遮挡，
   返回 `point_obstructed` 错误而不是静默点错地方；
3. 校验坐标落在窗口范围内，否则返回 `point_outside_window`。

## 故障排查

| 现象 | 原因与处理 |
|---|---|
| `spawn EPERM` | 运行环境禁止管道 stdio（某些沙箱）。MCP server 需由客户端主进程直接启动，不要套在受限 shell 里 |
| `cache_dir_unwritable` | 截图目录不可写。设置 `CU_HELPER_CACHE_DIR` 到可写目录 |
| `activation_failed` | 目标窗口属于提权进程，或前台锁定阻止切换。手动把窗口切到前台再重试 |
| `point_obstructed` | 落点被别的窗口挡住。重新 `get_window_state` 并用新坐标 |
| `stale_element_index` | 元素索引已过期。重新 `get_window_state` |
| 树里控件很少 | 目标应用未启用无障碍支持（部分 Electron/Chromium 应用需要显式开启） |
| 截图全黑 | 该窗口不支持 `PrintWindow`；helper 会自动激活窗口后重新抓屏，若仍失败请手动切到前台 |

## 开发

```powershell
npm run build          # 编译 TypeScript
npm run build:native   # 编译原生 helper
npm run typecheck      # 类型检查

npm run test:mcp       # MCP 协议层测试（15 项，无需 GUI 会话）
npm run test:e2e       # 端到端 GUI 测试（30 项，会启动测试靶程序并真实点击）
```

`tests/e2e.ps1` 会编译并启动 `tests/fixture/TestTarget.cs`（一个控件结构固定的 WinForms 窗口），
然后真实执行点击、输入、滚动与错误路径断言。测试期间会短暂抢占前台焦点。

原生 helper 也可单独自检：

```powershell
native\windows\bin\cu-helper.exe --selftest
```

## 与 Codex computer use 的关系

本项目是**独立实现**，不包含也不分发 OpenAI 的任何代码、二进制或资源。逆向分析的目的是理解
公开可见的工具语义（工具名、参数含义、索引与截图的工作方式），以便让模型在相同的交互范式下工作。
原生层基于 Microsoft 公开的 UI Automation / SendInput / DWM API 编写。

实现细节与差异记录在 [`docs/`](docs/) 中。

## License

[MIT](LICENSE)
