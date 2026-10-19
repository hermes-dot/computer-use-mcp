/**
 * MCP 工具层：把 Codex computer-use 的 window2 API 语义映射成 MCP 工具。
 *
 * 命名与参数尽量保持与 Codex 一致（element_index / screenshot_id / scroll_x ...），
 * 因为这套语义已经被证明能让模型稳定地"看树 -> 动作 -> 再看树"。
 *
 * 与 Codex 的差异：
 *   - window_id 可以省略，此时使用会话中的"当前窗口"（减少模型漏传参数导致的失败）；
 *   - 每个动作类工具都会校验目标点确实落在目标窗口上，被遮挡时直接报错而不是点错地方。
 */
import { readFile } from "node:fs/promises";
import { z } from "zod";
import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import type { CallToolResult, ContentBlock } from "@modelcontextprotocol/sdk/types.js";
import { HelperClient, HelperError } from "./helper-client.js";

interface ShotInfo {
  id: string;
  path: string;
  width: number;
  height: number;
  sourceWidth: number;
  sourceHeight: number;
  scale: number;
  method: string;
}

interface WindowInfo {
  id: number;
  app: string;
  title: string;
  processName?: string;
  className?: string;
  focused?: boolean;
  minimized?: boolean;
  width?: number;
  height?: number;
  x?: number;
  y?: number;
}

/** 会话内的"当前窗口"，让连续动作不必每次都重复传 window_id。 */
class SessionState {
  currentWindowId: number | undefined;
  currentShotId: string | undefined;
}

const readOnly = { readOnlyHint: true, openWorldHint: true } as const;
const mutating = { readOnlyHint: false, destructiveHint: true, openWorldHint: true } as const;

export function registerTools(server: McpServer, helper: HelperClient): void {
  const session = new SessionState();

  // ------------------------------------------------------------------ 辅助

  const text = (value: string): ContentBlock => ({ type: "text", text: value });

  const ok = (blocks: ContentBlock[]): CallToolResult => ({ content: blocks });

  const fail = (error: unknown): CallToolResult => {
    // 用鸭子类型而不是 instanceof：错误可能来自不同模块实例或被包装过，
    // 只要带 code 就把它暴露给模型（helper 的 stale_element_index、Node 的 EPERM 等都有诊断价值）。
    const code = (error as { code?: unknown } | null)?.code;
    if (error instanceof Error && typeof code === "string" && code.length > 0) {
      return { isError: true, content: [text(`[${code}] ${error.message}`)] };
    }
    const message = error instanceof Error ? error.message : String(error);
    return { isError: true, content: [text(message)] };
  };

  const imageFromShot = async (shot: ShotInfo): Promise<ContentBlock> => {
    const buffer = await readFile(shot.path);
    const mimeType = shot.path.toLowerCase().endsWith(".png") ? "image/png" : "image/jpeg";
    return { type: "image", data: buffer.toString("base64"), mimeType };
  };

  /** 解析目标窗口：显式传入优先，否则回退到会话当前窗口。 */
  const resolveWindowId = (explicit?: number): number => {
    const id = explicit ?? session.currentWindowId;
    if (id === undefined) {
      throw new HelperError(
        "no_target_window",
        "No target window selected. Call list_apps (or list_windows) and pass window_id, " +
          "or call get_window_state first to establish the current window.",
      );
    }
    return id;
  };

  const rememberWindow = (windowId: number, shot?: ShotInfo): void => {
    session.currentWindowId = windowId;
    if (shot) session.currentShotId = shot.id;
  };

  const describeWindow = (window: WindowInfo): string =>
    `window ${window.id} — ${window.processName ?? "?"} — "${window.title}"` +
    (window.width && window.height ? ` (${window.width}x${window.height})` : "") +
    (window.minimized ? " [minimized]" : "") +
    (window.focused ? " [foreground]" : "");

  const describeShot = (shot: ShotInfo): string =>
    `screenshot ${shot.id}: ${shot.width}x${shot.height} px` +
    (shot.scale !== 1 ? ` (scaled ${(shot.scale * 100).toFixed(0)}% from ${shot.sourceWidth}x${shot.sourceHeight}; coordinates you read off the image are in image pixels)` : "") +
    ` [${shot.method}]`;

  // ------------------------------------------------------------------ 发现

  server.registerTool(
    "list_apps",
    {
      title: "List applications",
      description:
        "List running applications that own visible windows, together with their windows. " +
        "Start here: pick the app, then pick one of its windows to act on.",
      inputSchema: {},
      annotations: readOnly,
    },
    async () => {
      try {
        const result = await helper.call<{ apps: Array<{ id: string; displayName: string; windows: WindowInfo[] }>; count: number }>(
          "list_apps",
        );
        if (result.count === 0) return ok([text("No applications with visible windows were found.")]);
        const lines: string[] = [`${result.count} application(s) with visible windows:`, ""];
        for (const app of result.apps) {
          lines.push(`${app.displayName ?? app.id}`);
          lines.push(`  app: ${app.id}`);
          for (const window of app.windows) {
            lines.push(`  - window_id ${window.id}  "${window.title}"${window.minimized ? " [minimized]" : ""}`);
          }
        }
        return ok([text(lines.join("\n"))]);
      } catch (error) {
        return fail(error);
      }
    },
  );

  server.registerTool(
    "list_windows",
    {
      title: "List windows",
      description:
        "List open top-level windows that can be targeted. Use this to recover a window whose id stopped working, " +
        "or to find modal dialogs and secondary windows.",
      inputSchema: {},
      annotations: readOnly,
    },
    async () => {
      try {
        const result = await helper.call<{ windows: WindowInfo[]; count: number }>("list_windows", { detail: true });
        if (result.count === 0) return ok([text("No targetable windows were found.")]);
        const lines = [`${result.count} targetable window(s):`, ""];
        for (const window of result.windows) lines.push(describeWindow(window));
        return ok([text(lines.join("\n"))]);
      } catch (error) {
        return fail(error);
      }
    },
  );

  server.registerTool(
    "get_window",
    {
      title: "Rehydrate a window",
      description:
        "Re-read a window by id after losing its binding (for example after the app recreated the window). " +
        "Returns the current title and geometry.",
      inputSchema: { window_id: z.number().int().describe("Window id from list_apps / list_windows") },
      annotations: readOnly,
    },
    async ({ window_id }) => {
      try {
        const result = await helper.call<{ window: WindowInfo }>("get_window", { hwnd: window_id });
        rememberWindow(result.window.id);
        return ok([text(describeWindow(result.window))]);
      } catch (error) {
        return fail(error);
      }
    },
  );

  // ------------------------------------------------------------------ 生命周期

  server.registerTool(
    "launch_app",
    {
      title: "Launch an application",
      description:
        "Launch a desktop application by its app id (from list_apps), an executable path, or a shell name such as 'notepad'. " +
        "After launching, call list_apps again and pick the new window.",
      inputSchema: {
        app: z.string().describe("App id from list_apps, an executable path, or a shell name"),
        arguments: z.string().optional().describe("Optional command line arguments"),
      },
      annotations: { readOnlyHint: false, destructiveHint: false, openWorldHint: true },
    },
    async ({ app, arguments: args }) => {
      try {
        const result = await helper.call<{ launched: boolean; pid?: number }>("launch_app", {
          app,
          ...(args ? { arguments: args } : {}),
        });
        return ok([text(`Launched ${app}${result.pid ? ` (pid ${result.pid})` : ""}. Call list_apps to find its window.`)]);
      } catch (error) {
        return fail(error);
      }
    },
  );

  server.registerTool(
    "activate_window",
    {
      title: "Bring a window to the foreground",
      description:
        "Raise a window and give it keyboard focus. Input tools activate their target automatically, " +
        "so this is mainly an escape hatch when a window is behind others or minimized.",
      inputSchema: { window_id: z.number().int().optional().describe("Defaults to the current window") },
      annotations: mutating,
    },
    async ({ window_id }) => {
      try {
        const target = resolveWindowId(window_id);
        const result = await helper.call<{ window: WindowInfo }>("activate_window", { hwnd: target });
        rememberWindow(result.window.id);
        return ok([text(`Activated ${describeWindow(result.window)}`)]);
      } catch (error) {
        return fail(error);
      }
    },
  );

  // ------------------------------------------------------------------ 观察

  server.registerTool(
    "get_window_state",
    {
      title: "Capture window state (accessibility tree + screenshot)",
      description:
        "Capture a point-in-time snapshot of a window: a formatted accessibility tree whose element indexes address " +
        "individual controls, plus a screenshot of the window. Treat this as expensive — perform a batch of related " +
        "actions, then capture again to verify. Element indexes are only valid for the snapshot that produced them, " +
        "so refresh after anything that may change the UI.",
      inputSchema: {
        window_id: z.number().int().optional().describe("Defaults to the current window"),
        include_screenshot: z.boolean().optional().describe("Capture a screenshot (default true)"),
        include_text: z.boolean().optional().describe("Capture accessibility tree and text (default true)"),
      },
      annotations: readOnly,
    },
    async ({ window_id, include_screenshot, include_text }) => {
      try {
        const target = resolveWindowId(window_id);
        const result = await helper.call<{
          window: WindowInfo;
          accessibility: {
            tree: string;
            nodeCount: number;
            truncated: boolean;
            focusedElement?: string;
            documentText?: string;
            selectedText?: string;
          };
          screenshots: ShotInfo[];
        }>("get_window_state", {
          hwnd: target,
          includeScreenshot: include_screenshot ?? true,
          includeText: include_text ?? true,
        });

        const shot = result.screenshots[0];
        rememberWindow(result.window.id, shot);

        const blocks: ContentBlock[] = [];
        const header: string[] = [describeWindow(result.window)];
        if (shot) header.push(describeShot(shot));
        header.push(`accessibility: ${result.accessibility.nodeCount} element(s)${result.accessibility.truncated ? " (truncated)" : ""}`);
        if (result.accessibility.focusedElement) header.push(`focused: ${result.accessibility.focusedElement}`);
        if (result.accessibility.selectedText) header.push(`selected text: ${result.accessibility.selectedText}`);
        blocks.push(text(header.join("\n")));

        if (include_text !== false && result.accessibility.tree.length > 0) {
          blocks.push(text(result.accessibility.tree));
        }
        if (result.accessibility.documentText) {
          blocks.push(text(`--- document text ---\n${result.accessibility.documentText}`));
        }
        if (shot) blocks.push(await imageFromShot(shot));

        return ok(blocks);
      } catch (error) {
        return fail(error);
      }
    },
  );

  server.registerTool(
    "get_screenshot",
    {
      title: "Capture the whole desktop",
      description:
        "Capture the entire virtual desktop. Use it to see what is on screen when you do not yet know which window to target. " +
        "Coordinates from this image are desktop coordinates and cannot be passed to the window-scoped input tools.",
      inputSchema: {},
      annotations: readOnly,
    },
    async () => {
      try {
        const result = await helper.call<{ screenshots: ShotInfo[] }>("get_screenshot", {});
        const shot = result.screenshots[0];
        if (!shot) return fail(new Error("Desktop capture returned no image."));
        return ok([text(describeShot(shot)), await imageFromShot(shot)]);
      } catch (error) {
        return fail(error);
      }
    },
  );

  // ------------------------------------------------------------------ 输入

  server.registerTool(
    "click",
    {
      title: "Click",
      description:
        "Click either an indexed element from the latest window state, or a coordinate read off the latest screenshot. " +
        "Prefer element indexes: they survive layout changes and are unambiguous. " +
        "Coordinate clicks must pass the screenshot_id they came from, and coordinates are image pixels " +
        "(the screenshot may be scaled down from the physical window size).",
      inputSchema: {
        window_id: z.number().int().optional().describe("Defaults to the current window"),
        element_index: z.number().int().optional().describe("Element index from the latest get_window_state tree"),
        x: z.number().int().optional().describe("X in screenshot image pixels"),
        y: z.number().int().optional().describe("Y in screenshot image pixels"),
        screenshot_id: z.string().optional().describe("Id of the screenshot the coordinates came from"),
        click_count: z.number().int().min(1).max(3).optional().describe("Number of clicks (default 1)"),
        mouse_button: z.enum(["left", "right", "middle"]).optional().describe("Mouse button (default left)"),
      },
      annotations: mutating,
    },
    async ({ window_id, element_index, x, y, screenshot_id, click_count, mouse_button }) => {
      try {
        const target = resolveWindowId(window_id);
        const result = await helper.call<{ clicked: boolean; x: number; y: number }>("click", {
          hwnd: target,
          ...(element_index !== undefined ? { elementIndex: element_index } : {}),
          ...(x !== undefined ? { x } : {}),
          ...(y !== undefined ? { y } : {}),
          ...(screenshot_id !== undefined ? { screenshotId: screenshot_id } : {}),
          ...(click_count !== undefined ? { clickCount: click_count } : {}),
          ...(mouse_button !== undefined ? { mouseButton: mouse_button } : {}),
        });
        return ok([text(`Clicked at screen (${result.x}, ${result.y}). Capture a fresh window state to verify the effect.`)]);
      } catch (error) {
        return fail(error);
      }
    },
  );

  server.registerTool(
    "type_text",
    {
      title: "Type text",
      description:
        "Type text into the focused element of a window (optionally focusing an indexed element first). " +
        "Uses Unicode input, so any character works regardless of keyboard layout. " +
        "For plain text fields, set_value is faster and more reliable.",
      inputSchema: {
        window_id: z.number().int().optional().describe("Defaults to the current window"),
        text: z.string().describe("Text to type; \\n sends Enter"),
        element_index: z.number().int().optional().describe("Focus this element before typing"),
      },
      annotations: mutating,
    },
    async ({ window_id, text: value, element_index }) => {
      try {
        const target = resolveWindowId(window_id);
        const result = await helper.call<{ typed: boolean; length: number }>("type_text", {
          hwnd: target,
          text: value,
          ...(element_index !== undefined ? { elementIndex: element_index } : {}),
        });
        return ok([text(`Typed ${result.length} character(s).`)]);
      } catch (error) {
        return fail(error);
      }
    },
  );

  server.registerTool(
    "press_key",
    {
      title: "Press a key or chord",
      description:
        "Press a key or a '+' separated chord in a window, e.g. 'Return', 'Tab', 'Control_L+a', 'Control_L+Shift_L+period'. " +
        "Common aliases (Ctrl, Alt, Shift, Esc, Enter) are accepted.",
      inputSchema: {
        window_id: z.number().int().optional().describe("Defaults to the current window"),
        key: z.string().describe("Key or chord, e.g. 'Control_L+a'"),
      },
      annotations: mutating,
    },
    async ({ window_id, key }) => {
      try {
        const target = resolveWindowId(window_id);
        const result = await helper.call<{ pressed: string }>("press_key", { hwnd: target, key });
        return ok([text(`Pressed ${result.pressed}.`)]);
      } catch (error) {
        return fail(error);
      }
    },
  );

  server.registerTool(
    "scroll",
    {
      title: "Scroll",
      description:
        "Scroll at a point in a window. Positive scroll_y scrolls down, positive scroll_x scrolls right. " +
        "Coordinates are screenshot image pixels and must come with the matching screenshot_id.",
      inputSchema: {
        window_id: z.number().int().optional().describe("Defaults to the current window"),
        x: z.number().int().describe("X in screenshot image pixels"),
        y: z.number().int().describe("Y in screenshot image pixels"),
        scroll_x: z.number().optional().describe("Horizontal delta in pixels (positive = right)"),
        scroll_y: z.number().optional().describe("Vertical delta in pixels (positive = down)"),
        screenshot_id: z.string().optional().describe("Id of the screenshot the coordinates came from"),
      },
      annotations: mutating,
    },
    async ({ window_id, x, y, scroll_x, scroll_y, screenshot_id }) => {
      try {
        const target = resolveWindowId(window_id);
        const result = await helper.call<{ scrolled: boolean }>("scroll", {
          hwnd: target,
          x,
          y,
          scrollX: scroll_x ?? 0,
          scrollY: scroll_y ?? 0,
          ...(screenshot_id !== undefined ? { screenshotId: screenshot_id } : {}),
        });
        return ok([text(`Scrolled (${scroll_x ?? 0}, ${scroll_y ?? 0}).`)]);
      } catch (error) {
        return fail(error);
      }
    },
  );

  server.registerTool(
    "set_value",
    {
      title: "Set an element's value",
      description:
        "Replace the value of an editable indexed element directly through UI Automation. " +
        "Faster and more reliable than focusing and typing, but only works on elements that expose a value pattern.",
      inputSchema: {
        window_id: z.number().int().optional().describe("Defaults to the current window"),
        element_index: z.number().int().describe("Element index from the latest get_window_state tree"),
        value: z.string().describe("Replacement value"),
      },
      annotations: mutating,
    },
    async ({ window_id, element_index, value }) => {
      try {
        const target = resolveWindowId(window_id);
        await helper.call("set_value", { hwnd: target, elementIndex: element_index, value });
        return ok([text(`Set element ${element_index} to "${value}".`)]);
      } catch (error) {
        return fail(error);
      }
    },
  );

  server.registerTool(
    "drag",
    {
      title: "Drag",
      description:
        "Drag the mouse between two points in a window (for sliders, selections, drag-and-drop). " +
        "Coordinates are screenshot image pixels and must come with the matching screenshot_id.",
      inputSchema: {
        window_id: z.number().int().optional().describe("Defaults to the current window"),
        from_x: z.number().int().describe("Start X in screenshot image pixels"),
        from_y: z.number().int().describe("Start Y in screenshot image pixels"),
        to_x: z.number().int().describe("End X in screenshot image pixels"),
        to_y: z.number().int().describe("End Y in screenshot image pixels"),
        screenshot_id: z.string().optional().describe("Id of the screenshot the coordinates came from"),
      },
      annotations: mutating,
    },
    async ({ window_id, from_x, from_y, to_x, to_y, screenshot_id }) => {
      try {
        const target = resolveWindowId(window_id);
        const result = await helper.call<{ dragged: boolean }>("drag", {
          hwnd: target,
          fromX: from_x,
          fromY: from_y,
          toX: to_x,
          toY: to_y,
          ...(screenshot_id !== undefined ? { screenshotId: screenshot_id } : {}),
        });
        return ok([text(`Dragged (${from_x}, ${from_y}) -> (${to_x}, ${to_y}).`)]);
      } catch (error) {
        return fail(error);
      }
    },
  );

  server.registerTool(
    "perform_secondary_action",
    {
      title: "Invoke a secondary accessibility action",
      description:
        "Run a named accessibility action on an indexed element, such as 'Invoke', 'Expand', 'Collapse', 'Select', " +
        "'Toggle', 'Raise', or 'Scroll Up' / 'Scroll Down'. The actions an element supports are listed in its tree line.",
      inputSchema: {
        window_id: z.number().int().optional().describe("Defaults to the current window"),
        element_index: z.number().int().describe("Element index from the latest get_window_state tree"),
        action: z.string().describe("Action name, e.g. 'Expand'"),
      },
      annotations: mutating,
    },
    async ({ window_id, element_index, action }) => {
      try {
        const target = resolveWindowId(window_id);
        await helper.call("perform_secondary_action", { hwnd: target, elementIndex: element_index, action });
        return ok([text(`Performed "${action}" on element ${element_index}.`)]);
      } catch (error) {
        return fail(error);
      }
    },
  );
}
