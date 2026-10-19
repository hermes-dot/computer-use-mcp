<div align="center">

# computer-use-mcp

**Let AI drive Windows desktop apps — UI Automation accessibility trees + SendInput + window capture**

An MCP server that lets a model *see structure* and *click precisely*.

English · [中文](README.zh-CN.md)

</div>

---

## What it is

What makes Codex's computer use far more reliable than "screenshot + guess coordinates" is that it
hands the model a **UI Automation accessibility tree**:

```
0 Window "Untitled - Notepad"
	1 MenuBar "Application"
		2 MenuItem "File" actions=[Expand]
		3 MenuItem "Edit" actions=[Expand]
	4 Edit "Text Editor" value="" focusable FOCUSED actions=[SetValue, SelectText]
	5 StatusBar "Ln 1, Col 1"
```

The model sees an **indexed list of controls**, not a picture it has to guess coordinates from.
It can say "click element 2" instead of "click (412, 37) and hope that's the File menu".

This project is a **clean-room reimplementation** of the Codex computer-use tool surface
(`@oai/sky` + the `codex-computer-use` native component). Only the publicly observable API
semantics were studied; all code here is independently written and contains no OpenAI source,
binary, or asset. The native layer is built on the .NET Framework UI Automation stack.

## Features

- **Accessibility-first**: every control gets a stable index, so actions survive layout tweaks
- **Captures occluded windows**: tries `PrintWindow` first, falls back to activating and grabbing the screen
- **Coordinate safety**: screenshots carry a `screenshot_id`; coordinates are rescaled and re-based automatically, and a click that would land on the *wrong window* fails loudly instead of silently
- **Unicode input**: `KEYEVENTF_UNICODE`, so CJK and emoji work regardless of keyboard layout
- **Zero third-party native dependencies**: only assemblies shipped with .NET Framework — no NuGet, no Rust/C++ toolchain
- **Testable**: ships a WinForms test target with a known control layout and 30 end-to-end assertions

## Architecture

```
┌─────────────────────────────┐
│  MCP client                  │   DeepSeek Harness / Claude Desktop / Cursor ...
└──────────────┬──────────────┘
               │ MCP over stdio (JSON-RPC)
┌──────────────▼──────────────┐
│  MCP server (Node/TypeScript)│   src/ — tool schemas, arg mapping, image encoding
└──────────────┬──────────────┘
               │ NDJSON over a child-process pipe
┌──────────────▼──────────────┐
│  Native helper (C#, .NET FW) │   native/windows/ — cu-helper.exe
│  ├ UI Automation → AX tree   │
│  ├ SendInput     → mouse/key │
│  ├ PrintWindow / → capture   │
│  │  BitBlt                   │
│  └ DWM           → geometry  │
└──────────────┬──────────────┘
               │ Win32
        ┌──────▼──────┐
        │ Target apps │
        └─────────────┘
```

Why two processes:

- UI Automation is a COM API and `SendInput` needs exact struct layouts — both are available
  dependency-free on .NET Framework, and awkward or impossible from Node.
- Node owns the MCP protocol and tool orchestration, where its ecosystem is strongest.
- The process boundary decouples the two: a helper crash yields a readable error and a restart
  instead of taking down the server.

## Install

### Requirements

| Component | Version | Notes |
|---|---|---|
| Windows | 10 1809+ / 11 | Needs UI Automation and Per-Monitor-V2 DPI |
| Node.js | 18+ | Runs the MCP server |
| .NET SDK | 8+ | Only to build the native helper (provides Roslyn) |
| .NET Framework | 4.7.2+ | Ships with Windows; provides `UIAutomationClient` |

### Build

```powershell
git clone <this-repo>
cd computer-use-mcp

npm install
npm run build          # TypeScript -> dist/
npm run build:native   # -> native/windows/bin/cu-helper.exe
```

Verify the native side:

```powershell
node dist/index.js --check
# helper: ...\native\windows\bin\cu-helper.exe
# ping: ok (dpi awareness = 2)
# targetable windows: 5
```

## DeepSeek Harness

DSH reads MCP servers from the cordis profile patch layer. Append this to
`%USERPROFILE%\.dsh\profiles\desktop\cordis.patch.yml`:

```yaml
- insert:
    - id: mcp-computer-use
      name: '@deepseek-ai/dsh-mcp-client'
      config:
        serverName: computer_use
        transport: stdio
        command: 'node'
        args:
          - 'C:\path\to\computer-use-mcp\dist\index.js'
        env:
          CU_HELPER_CACHE_DIR: 'C:\path\to\cache\computer-use'
        toolCallTimeoutMs: 60000
        failOnStartupError: false
```

Notes:

- **The `- insert:` wrapper is required.** A bare top-level `id` is treated as an override of an
  existing entry and silently ignored.
- `serverName` must match `[A-Za-z0-9_-]`; tools surface as `mcp__computer_use__<tool>`.
- No restart needed — the patch file is hot-reloaded.

## Other MCP clients

<details>
<summary>Claude Desktop / Cursor / any stdio client</summary>

```json
{
  "mcpServers": {
    "computer-use": {
      "command": "node",
      "args": ["C:\\path\\to\\computer-use-mcp\\dist\\index.js"],
      "env": { "CU_HELPER_CACHE_DIR": "C:\\path\\to\\cache\\computer-use" }
    }
  }
}
```

</details>

## Tools

| Tool | Purpose | Read-only |
|---|---|---|
| `list_apps` | Apps that own visible windows, with their windows | ✅ |
| `list_windows` | Targetable top-level windows | ✅ |
| `get_window` | Re-read a window by id | ✅ |
| `get_window_state` | **Core**: accessibility tree + screenshot | ✅ |
| `get_screenshot` | Whole virtual desktop | ✅ |
| `launch_app` | Launch an application | |
| `activate_window` | Raise a window and give it focus | |
| `click` | Click by element index or screenshot coordinate | |
| `type_text` | Type text (Unicode, CJK-safe) | |
| `press_key` | Key or chord, e.g. `Control_L+a` | |
| `scroll` | Scroll at a point | |
| `set_value` | Replace an editable element's value | |
| `drag` | Drag between two points | |
| `perform_secondary_action` | Expand / Collapse / Toggle / Select / ... | |

### Typical loop

```
1. list_apps                    -> pick the app and window_id
2. get_window_state(window_id)  -> indexed tree + screenshot
3. click(element_index=12)      -> index-based, layout-proof
4. get_window_state(window_id)  -> verify the effect
```

`get_window_state` is an expensive point-in-time snapshot: **batch related actions, then capture
again**. Element indexes are valid only for the snapshot that produced them.

## How it works

### Tree and element indexes

`get_window_state` walks the window with UI Automation's **ControlView**, skips offscreen
elements, assigns each visible element an incrementing index, and caches index → `AutomationElement`.
A later `click(element_index=N)` resolves the cached element, reads its screen rectangle, and clicks
its centre — no coordinates involved.

Line format:

```
<tab indent><index> <ControlType> "<name>" value="<value>" <state flags> actions=[<actions>]
```

State flags: `disabled` / `focusable` / `FOCUSED` / `required`. The `actions` list is exactly what
`perform_secondary_action` accepts for that element.

### Coordinates and scaling

Screenshots are scaled so the longest edge is at most 1600px; the returned `scale` is
*image pixels / physical pixels*. Coordinates the model reads off the image are image pixels —
the helper divides by `scale` and adds the window origin. Passing `screenshot_id` verifies the
coordinates came from the current screenshot of that window.

### Input safety

Before every input the helper:

1. brings the target window to the foreground (`AttachThreadInput` + `SetForegroundWindow`, which
   defeats the foreground lock);
2. verifies with `WindowFromPoint` that the point really belongs to the target — if a Start menu,
   overlay, or modal covers it, it returns `point_obstructed` instead of clicking the wrong thing;
3. rejects coordinates outside the window with `point_outside_window`.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `spawn EPERM` | The host forbids pipe stdio (some sandboxes). The MCP server must be launched by the client's main process, not from inside a restricted shell |
| `cache_dir_unwritable` | Screenshot directory not writable — point `CU_HELPER_CACHE_DIR` at a writable path |
| `activation_failed` | Target belongs to an elevated process, or the foreground lock won. Bring it forward manually |
| `point_obstructed` | Something covers the point. Re-run `get_window_state` and use fresh coordinates |
| `stale_element_index` | Index expired. Re-run `get_window_state` |
| Sparse tree | The app doesn't expose accessibility (some Electron/Chromium apps need it enabled explicitly) |
| Black screenshot | `PrintWindow` unsupported; the helper auto-activates and retries. If it still fails, focus the window manually |

## Development

```powershell
npm run build          # TypeScript
npm run build:native   # native helper
npm run typecheck

npm run test:mcp       # MCP protocol layer (15 tests, no GUI needed)
npm run test:e2e       # end-to-end GUI (30 assertions; briefly steals focus)
```

`tests/e2e.ps1` compiles and launches `tests/fixture/TestTarget.cs` — a WinForms window with a
fixed control layout — then performs real clicks, typing, scrolling, and error-path assertions.

The helper also self-checks standalone:

```powershell
native\windows\bin\cu-helper.exe --selftest
```

## Relationship to Codex computer use

This is an independent implementation. It does not include or redistribute any OpenAI code,
binary, or asset. Reverse engineering was limited to understanding publicly observable tool
semantics (tool names, parameter meanings, how indexes and screenshots behave) so that models can
work in the same interaction pattern. The native layer is written against Microsoft's public
UI Automation / SendInput / DWM APIs.

Design notes and differences are documented in [`docs/`](docs/).

## License

[MIT](LICENSE)
