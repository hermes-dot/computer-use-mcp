# 架构与设计决策

本文记录实现中的关键取舍。目标读者是后续维护者，以及想理解"为什么这么写"的人。

## 1. 为什么拆成 Node + C# 两层

| 能力 | 只能在原生侧做 | 只能在 Node 侧做 |
|---|---|---|
| UI Automation（COM 接口） | ✅ | ✗ |
| `SendInput`（精确结构体布局） | ✅ | ✗ |
| DWM / GDI 截图 | ✅ | ✗ |
| MCP 协议与工具编排 | 可行但生态弱 | ✅ |

UI Automation 的托管封装（`System.Windows.Automation`）只随 .NET Framework 提供，
而 Node 侧没有成熟的零编译 UIA 绑定。反过来，MCP 的官方 SDK 在 Node 生态最完整。
所以按能力边界切分，通过进程边界解耦：

- helper 崩溃 → MCP server 报可读错误并可重启，不会带走整个会话；
- 元素索引缓存天然属于 helper 进程，生命周期清晰；
- 两侧可独立测试（helper 用 `--selftest` 与 PowerShell 直连，MCP 层用 mock）。

## 2. 为什么是 .NET Framework + Roslyn，而不是 .NET 8 项目

目标机器上 NuGet 不可达（TLS 层被拦），`dotnet restore` 直接失败。
而 `UIAutomationClient.dll` / `UIAutomationTypes.dll` / `System.Web.Extensions.dll`
都是 **.NET Framework 自带**的，零包依赖即可拿到 UIA 与 JSON 序列化。

代价是 Windows 自带的 `csc.exe` 停留在 C# 5（2012 年），写不了 `out var`、字符串插值等。
解决办法是用 **.NET SDK 内置的 Roslyn**（`sdk/*/Roslyn/bincore/csc.dll`）编译 .NET Framework 目标：

```
dotnet <sdk>/Roslyn/bincore/csc.dll /nostdlib+ /langversion:latest \
  /r:<Framework64>/mscorlib.dll /r:<Framework64>/WPF/UIAutomationClient.dll ...
```

于是既拿到最新 C# 语法，又保持零第三方依赖。`native/windows/build.ps1` 封装了这套参数。

产物只有 ~56 KB，运行时依赖 Windows 自带的 .NET Framework。

## 3. DPI 感知是正确性的前提，不是优化

如果 helper 不声明 DPI 感知，Windows 会对进程做 **DPI 虚拟化**：

- `GetSystemMetrics` 返回逻辑分辨率（实测 1560×1040），而窗口真实尺寸是 3120×2080；
- 于是"窗口矩形"、"UIA 元素坐标"、"截图像素"三者落在不同坐标系里，点击必然偏移。

因此 `EnableDpiAwareness()` 必须在**任何**窗口/坐标 API 之前调用，且对所有运行模式生效。
这个约束曾经被违反过一次（`--tcp` 分支提前 return），症状是 `ping` 返回 `dpiAware: 0`，
随即所有坐标失效 —— 现在它在 `Main` 的最前面，`--selftest` 也会校验。

## 4. 元素索引的生命周期

```
get_window_state ──> 遍历 ControlView ──> 分配索引 ──> 缓存 index -> AutomationElement
                                                          │
click(element_index=N) ───────────────────────────────────┘ 取矩形中心 -> SendInput
```

设计约束：

- **索引只在产生它的那次快照内有效**。界面一变（弹窗、列表刷新、页面跳转），旧索引就指向
  错误的元素。因此每次 `get_window_state` 都整体重建缓存，越界访问返回 `stale_element_index`
  并提示重新抓取。
- **跳过离屏元素**。UIA 的 ControlView 会包含 `IsOffscreen` 的元素（滚动出可视区、折叠面板），
  把它们放进树里既浪费上下文，也会让模型点到看不见的地方。
- **节点上限 1200**。浏览器和 IDE 的 UIA 树可达上万节点，必须截断并显式标记 `truncated`。

## 5. 截图策略：为什么要"先 PrintWindow 再抓屏"

| 方式 | 优点 | 缺点 |
|---|---|---|
| `PrintWindow(PW_RENDERFULLCONTENT)` | 不打扰用户，能截被遮挡窗口 | 对 Chromium/UWP 常返回空白 |
| 屏幕 `BitBlt` + 窗口矩形裁剪 | 内容一定是最新合成结果 | 要求窗口在前台，会抢焦点 |

实现按"是否前台"分流，并在 `PrintWindow` 结果上做**单色采样**判断是否空白：

```
前台          -> 直接抓屏裁剪
非前台        -> PrintWindow -> 非空白则采用
                空白/失败   -> 激活窗口 -> 抓屏裁剪
```

`LooksBlank()` 采样 12×12 网格，全同色即判为空白。这是启发式，但能挡住最常见的
"PrintWindow 成功返回但内容是黑的"这一类静默失败。

## 6. 坐标与缩放

截图按最长边缩放到 1600px 以内（模型看图不需要 4K，但 token 成本与 4K 成正比）。
返回的 `scale = 图像像素 / 物理像素`。

坐标换算发生在 helper 内部：

```
物理坐标 = 窗口原点 + round(图像坐标 / scale)
```

`scale` 由 helper 统一持有，避免上层各算各的算错。传入 `screenshot_id` 时校验它确实是
该窗口最近一次截图，否则返回 `stale_screenshot` —— 这能挡住"用旧图上的坐标点新界面"这类错误。

窗口移动后旧坐标仍然有效，因为坐标是**窗口相对**的；换算时读的是当前窗口矩形。

## 7. 输入安全：两道校验

`SendInput` 是不可撤销的：点错地方可能触发删除、发送、转账。所以每次输入前：

1. **前台校验**：目标窗口必须在前台。用 `AttachThreadInput` 把本线程挂到前台线程与目标线程的
   输入队列上再 `SetForegroundWindow`，绕过前台锁定；失败即报 `activation_failed`，
   绝不"尽力而为"地发送输入。
2. **落点校验**：`WindowFromPoint` 取该点实际所属窗口，与目标窗口的根祖先比对。
   不一致（开始菜单、悬浮窗、模态框遮挡）→ `point_obstructed`，并回报实际挡住它的进程与标题。
   坐标超出窗口范围 → `point_outside_window`。

宁可失败并让模型重新观察，也不制造"看起来成功、实际点错"的静默错误。

## 8. 传输层：pipe 优先，TCP 回退

正常情况用子进程 stdio 管道（最轻量）。但部分沙箱会以 `EPERM` 拒绝为子进程创建管道，
此时自动回退到回环 TCP：

```
spawn(helper, ["--tcp","--port","0","--port-file",<tmp>,"--token",<secret>], { stdio: "ignore" })
  -> helper 绑定 127.0.0.1:0，把实际端口写入 port-file
  -> 父进程轮询 port-file 拿到端口并连接
  -> 首行发送 token，helper 校验后才接受业务请求
```

两种传输跑的是**同一套 NDJSON 协议与同一个 `Serve()` 循环**，因此工具层完全无感。
安全上：只绑回环、必须带随机 token（这个接口能控制鼠标键盘，不能让同机任意进程调用）。

可用 `CU_HELPER_TRANSPORT=pipe|tcp` 强制指定，便于排查。

## 9. 与 Codex 实现的差异

| 方面 | Codex | 本项目 |
|---|---|---|
| 原生层 | 私有 `codex-computer-use.exe` | 自研 `cu-helper.exe`（.NET Framework） |
| 截图 | Windows.Graphics.Capture（可截遮挡窗口） | PrintWindow + 前台抓屏回退 |
| 调用方式 | 模型在 `node_repl` 里直接调 JS API | 标准 MCP 工具（任意客户端可用） |
| 元素寻址 | `element_index`（同一套语义） | 同 |
| 坐标校验 | `screenshotId`（同一套语义） | 同，另加落点遮挡校验 |
| 窗口选择 | 每次显式传 `window` | `window_id` 可选，缺省用会话当前窗口 |

WGC（Windows.Graphics.Capture）能在窗口被遮挡时仍然截到内容，本项目未采用：
它需要 Direct3D11 设备与 WinRT 投影，在"零 NuGet"约束下要手写大量 COM interop。
当前方案覆盖了绝大多数场景（前台抓屏 + PrintWindow 回退），代价是非前台窗口有时需要先激活。

## 10. 测试分层

| 层 | 文件 | 覆盖 | 是否需要 GUI |
|---|---|---|---|
| 原生 helper | `tests/e2e.ps1` | 30 项：窗口发现、树、点击、输入、坐标换算、错误契约 | 是（自建靶程序） |
| MCP 协议 | `tests/mcp.test.mjs` | 15 项：握手、工具清单、参数映射、错误契约 | 否（mock helper） |
| 真实链路 | `tests/mcp-live.mjs` | 15 项：MCP 工具 -> 真 helper -> 真 GUI | 是 |

靶程序 `tests/fixture/TestTarget.cs` 刻意使用固定控件布局，并让 `statusLabel` **不设置**
`AccessibleName` —— 这样 UIA 的 `Name` 就是可见文本本身，测试才能通过无障碍树观察到
"点击真的改变了界面状态"，而不是只验证"调用返回了成功"。

## 11. 跨平台扩展

MCP 层与平台无关，它只依赖一条约定：**`native/<平台>/bin/` 下有一个可执行文件，
能用 NDJSON 讲同一套方法**（见第 8 节的协议）。因此新增平台**不需要改动 `src/` 下
任何工具定义或参数映射**。

```
native/
├── windows/   已实现   C# / .NET Framework 4.8（UI Automation + SendInput + PrintWindow/GDI）
├── macos/     待实现   AXUIElement + CGEvent + ScreenCaptureKit
└── linux/     待实现   AT-SPI + XTest/uinput + 合成器相关抓屏
```

后端需要实现的方法集（与 `native/windows/Program.cs` 的分发表一致）：

| 类别 | 方法 |
|---|---|
| 查询 | `ping` `list_windows` `list_apps` `get_window` `get_window_state` `element_bounds` `get_screenshot` |
| 生命周期 | `activate_window` `launch_app` |
| 输入 | `click` `move` `type_text` `press_key` `scroll` `set_value` `drag` `perform_secondary_action` |

对平台后端的要求（这些是正确性的前提，不是优化项）：

1. **坐标系必须与无障碍 API 一致**：Windows 上体现为 DPI 感知；macOS 上是 backing scale
   factor；Linux 上是 X11 逻辑坐标与合成器缩放的关系。三者不一致会让"元素矩形"与
   "截图像素"错位，而这类错误不会报错，只会点偏。
2. **元素索引只在最近一次 `get_window_state` 内有效**，越界必须返回 `stale_element_index`。
3. **输入前校验前台归属与落点遮挡**，宁可失败也不要静默点错窗口。
4. **截图必须回传 `scale`**，让上层把模型读到的图像坐标换算回物理坐标。

`helper-locator.ts` 里的 `BACKENDS` 表声明了各平台的目标路径与实现状态；
平台未支持或产物缺失时，`--check` 与工具调用都会给出可操作的错误信息。

顺带一提：Codex 在 Linux 上走的是 AT-SPI 的 **full-desktop** 目标（桌面级坐标），
在 macOS/Windows 上走 **window2**（窗口级坐标 + 元素索引）。若要严格对齐，
Linux 后端直接沿用本项目已有的窗口级语义即可，不必引入第二套坐标系。
