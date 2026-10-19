# 逆向分析：Codex Computer Use

本文记录对 OpenAI Codex computer use 功能的逆向分析结果，以及本项目如何据此做**独立实现**。

分析对象来自本机已安装的 Codex 组件（`~/.codex`、Codex 桌面应用运行时），
方法为阅读其**公开可读**的 JavaScript API 层与配置、观察进程与管道行为。
未进行二进制反编译以窃取实现，也未复制任何源码。

## 1. 三层架构

```
┌──────────────────────────────────────────────┐
│ codex.exe（Rust 单文件，约 298 MB）           │
│  - 模型会话与工具调度                          │
│  - 通过 MCP 挂载 node_repl，把 JS 运行时给模型 │
└───────────────┬──────────────────────────────┘
                │ MCP (node_repl)
┌───────────────▼──────────────────────────────┐
│ @oai/sky  (dist/project/cua/sky_js)          │
│  - sky 对象：list_apps / get_window_state / … │
│  - 三个 target：full-desktop / window / window2│
│  - 把截图通过 nodeRepl.emitImage 注入上下文    │
└───────────────┬──────────────────────────────┘
                │ NDJSON over stdio pipe
┌───────────────▼──────────────────────────────┐
│ codex-computer-use.exe（约 1.5 MB）           │
│  - SendInput / UI Automation / WGC 截图       │
│  - 同目录另有 codex-computer-use-swift.exe    │
│    （约 56 MB，含 Swift 运行时的覆盖层 UI）    │
└──────────────────────────────────────────────┘
```

证据：

- `~/.codex/config.toml` 中 `notify` 指向 `...\cua_node\<hash>\bin\node_modules\@oai\sky\bin\windows\codex-computer-use.exe`
- 同一配置的 `[mcp_servers.node_repl.env]` 里有 `SKY_CUA_NATIVE_PIPE=1`、
  `SKY_CUA_NATIVE_PIPE_DIRECTORY=\\.\pipe\codex-computer-use-<uuid>`、
  `NODE_REPL_TRUSTED_SERVICES={"sky":"@oai/sky/service"}`
- `~/.codex/computer-use/config.json` 只有覆盖层文案：`"ChatGPT is using your computer"` / `"Esc to cancel"`

## 2. 工具 API 面

Windows 走 **window2** 目标，接口如下（摘自其随包文档 `docs/skills/oai_sky_lib/windows/SKILL.md`）：

| 方法 | 参数要点 |
|---|---|
| `list_apps()` | 返回 `{id, displayName?, lastUsedDate?, useCount?, isRunning?, windows[]}` |
| `list_windows()` | 返回可操作顶层窗口 |
| `get_window({id, app?})` | 按 id 重新取回窗口 |
| `launch_app({app})` | app id 或可执行文件 |
| `activate_window({window})` | 置前并交出焦点 |
| `get_window_state({window, include_screenshot?, include_text?})` | 核心：无障碍树 + 截图 |
| `click({window, element_index? \| x,y, screenshotId?, click_count?, mouse_button?})` | 元素索引或坐标 |
| `type_text({window, text})` | |
| `press_key({window, key})` | `+` 分隔的和弦，keysym 风格名 |
| `scroll({window, x, y, scrollX, scrollY, screenshotId?})` | |
| `set_value({window, element_index, value})` | |
| `drag({window, from_x, from_y, to_x, to_y, screenshotId?})` | |
| `perform_secondary_action({window, element_index, action})` | Expand / Collapse / Toggle / Select / Raise / Scroll Up… |

另外 full-desktop（Linux）目标还提供 `get_screenshot()`、`drag_handle()`、`perform_secondary_action` 的
`at_spi`/`x11` 双后端等。

### 值得注意的细节

- **`get_window_state` 的默认值**：`include_screenshot = true`、`include_text = false`，
  且**两者不能同时为 false**，否则抛 `TypeError("get_window_state must request include_text, include_screenshot, or both")`。
- **`click` 在原生层是两个方法**：给坐标时调 `click`，给元素索引时调 `click_element`。
- **`accessibility` 对象**：`tree`（必填字符串）、`focused_element`、`selected_text`、
  `selected_elements`（字符串数组）、`document_text`。
- **`screenshots` 是数组**，每项为 `{id, zIndex, url, originX?, originY?, width?, height?}`。
  即 Codex 会返回**多张截图**（例如主窗口 + 弹出的菜单/浮层），每张带自己的原点与层级，
  默认 id 为 `screenshot-0`。
- **音频**：仅当 `SKY_ENABLE_AUDIO=1` 时才挂载 `start_audio_recording` / `stop_audio_recording`。

## 3. 原生 helper 协议

- 传输：`spawn(helper, { stdio: ["pipe","pipe","pipe"], windowsHide: true })`，UTF-8 行分隔 JSON。
  子进程 `unref()`，父进程退出时不阻塞。
- stderr 被累积，仅用于组装失败消息：`computer-use helper exited with <reason>: <stderr>`。
- **退出码 130 = 用户按物理 Escape 中断**。此时抛出固定文案：
  > Computer Use was stopped by the user with the physical Escape key. Stop your work,
  > do not call further Computer Use tools in this turn, and send a final message noting
  > that the user stopped Computer Use.

  另有 `cache/computer-use/interrupts` 目录与 `x-oai-cua-request-budget-ms` 请求头，
  说明存在"单轮请求预算"与中断状态落盘机制。
- helper 命令与参数可注入（`helperCommand` / `helperArgs` / `helperEnv`），
  并有 `preserveHelperOnTimeout` 之类的超时策略开关。
- 截图以 data URL 形式回传，再由 `nodeRepl.emitImage(url)` 注入模型上下文
  （另有 `codex.emitImage({type:"input_image", image_url, detail:"original"})` 路径）。

## 4. 无障碍树与元素索引

Codex 把窗口的 UI Automation 树**格式化成文本**交给模型（`accessibility.tree`），
每行带一个自增索引，动作通过 `element_index` 寻址。其文档明确要求：

- 元素索引**只对产生它的那次快照有效**，任何可能改变界面的操作之后都要重新抓取；
- `get_window_state` 是"昂贵的即时快照"，应当批量执行动作再重新观察；
- 坐标只在"无障碍元素不可用"时使用，且必须带上对应的 `screenshotId`。

## 5. 本项目的实现与差异

| 方面 | Codex | 本项目 |
|---|---|---|
| 原生层 | 私有 `codex-computer-use.exe` | 自研 `cu-helper.exe`（.NET Framework，零第三方依赖） |
| 截图 | Windows.Graphics.Capture（遮挡窗口也能截） | `PrintWindow` 优先，失败则激活窗口后抓屏 |
| 多张截图 | 支持（主窗口 + 浮层，带 `zIndex`/`originX`） | 单张窗口截图 |
| 缩放 | 未观察到缩放字段 | 最长边缩放到 1600px，回传 `scale` 并自动换算坐标 |
| `include_text` 默认 | `false` | `true`（本项目的核心价值就是让模型看到结构） |
| `selected_elements` | 支持 | 暂未实现 |
| 音频 | `SKY_ENABLE_AUDIO=1` 时可录制 | 未实现 |
| 中断 | 物理 Escape → 退出码 130 + 固定文案 | 未实现（MCP 客户端可用自身的取消机制） |
| 传输 | 仅 stdio 管道 | stdio 管道优先，宿主禁止管道时回退回环 TCP |
| 调用方式 | 模型在 `node_repl` 里直接调 JS | 标准 MCP 工具，任意 MCP 客户端可用 |
| 窗口选择 | 每次显式传 `window` | `window_id` 可选，缺省用会话当前窗口 |
| 落点校验 | 文档提到会被非目标窗口遮挡的问题 | 输入前用 `WindowFromPoint` 校验，被遮挡即报错 |

### 保留的语义（因为已被证明有效）

- 工具名与参数名（`element_index` / `screenshotId` / `scrollX` / `click_count` …）
- "索引只对最近一次快照有效"的约束，以及越界时要求重新抓取
- `screenshotId` 与坐标绑定的校验
- "先看树、再动作、再看树"的工作流，以及把 `get_window_state` 标为昂贵操作

### 明确未采用的做法

- **不复制其原生二进制**：`codex-computer-use.exe` 属于 OpenAI，不可再分发，且复用它会与
  本项目"零专有依赖"的目标冲突。
- **不使用 Windows.Graphics.Capture**：它需要 Direct3D11 设备与 WinRT 投影，
  在"零 NuGet"约束下要手写大量 COM interop。当前方案覆盖了绝大多数场景，
  代价是非前台窗口有时需要先激活。

## 6. 分析方法与证据留存

| 证据 | 位置 |
|---|---|
| Codex 配置（helper 路径、管道名、node_repl env） | `~/.codex/config.toml` |
| 覆盖层文案 | `~/.codex/computer-use/config.json` |
| Windows 工具文档 | `@oai/sky/docs/skills/oai_sky_lib/windows/SKILL.md` |
| window2 客户端实现 | `@oai/sky/dist/project/cua/sky_js/src/targets/windows/internal/computer_use_client_base.js` |
| 传输层实现 | 同目录 `helper_transport.js` |
| 类型契约 | 同目录 `*.d.ts`（未压缩） |

分析产物（格式化后的 JS）与原始素材不随本仓库分发，因为它们包含 OpenAI 的代码。
本仓库只保留结论。

## 7. 合规声明

- 本项目不包含、不分发 OpenAI 的任何源码、二进制或资源；
- 原生层完全基于 Microsoft 公开文档的 UI Automation / SendInput / DWM API 编写；
- 保留的"工具语义"属于接口层面的功能设计，为实现互操作性所必需；
- 若权利人认为本文档中的描述超出合理引用范围，可联系移除。
