/**
 * MCP 协议层测试。
 *
 * 为什么这样测：原生 helper 的能力已经由 tests/e2e.ps1 用真实 GUI 断言覆盖；
 * 这里专注验证 MCP 这一层——握手、工具清单、参数映射（snake_case -> helper 的 camelCase）、
 * 内容块组装（文本 + 图片）以及错误契约。
 *
 * helper 用鸭子类型的 mock 替换：registerTools 只依赖它的 call(method, params)。
 * 这样测试既不需要 GUI 会话，也不依赖子进程管道（某些沙箱会禁止管道 stdio）。
 *
 * 运行：node --test tests/
 */
import { test, describe, before, after } from "node:test";
import assert from "node:assert/strict";
import { mkdtempSync, writeFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { registerTools } from "../dist/tools.js";

/** 记录调用参数并按需返回预设结果的假 helper。 */
class MockHelper {
  constructor(handlers) {
    this.handlers = handlers;
    this.calls = [];
  }

  async call(method, params = {}) {
    this.calls.push({ method, params });
    const handler = this.handlers[method];
    if (handler === undefined) throw new Error(`unexpected helper call: ${method}`);
    if (handler instanceof Error) throw handler;
    return typeof handler === "function" ? handler(params) : handler;
  }

  lastCall(method) {
    const matches = this.calls.filter((c) => c.method === method);
    return matches[matches.length - 1];
  }
}

/** 一个假的 HelperError（结构与 helper-client.ts 中的一致）。 */
class FakeHelperError extends Error {
  constructor(code, message) {
    super(message);
    this.name = "HelperError";
    this.code = code;
  }
}

let shotDir;
let shotPath;

before(() => {
  shotDir = mkdtempSync(path.join(tmpdir(), "cu-mcp-test-"));
  shotPath = path.join(shotDir, "shot-1.jpg");
  // 1x1 JPEG 足够验证 base64 组装路径
  writeFileSync(
    shotPath,
    Buffer.from(
      "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/wAALCAABAAEBAREA/8QAFAABAAAAAAAAAAAAAAAAAAAACf/EABQQAQAAAAAAAAAAAAAAAAAAAAD/2gAIAQEAAD8AKp//2Q==",
      "base64",
    ),
  );
});

after(() => {
  rmSync(shotDir, { recursive: true, force: true });
});

/** 建立一对内存互连的 MCP client/server。 */
async function connect(handlers) {
  const helper = new MockHelper(handlers);
  const server = new McpServer({ name: "computer-use", version: "test" });
  registerTools(server, helper);
  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
  const client = new Client({ name: "test-client", version: "1.0.0" }, { capabilities: {} });
  await Promise.all([client.connect(clientTransport), server.connect(serverTransport)]);
  return { client, helper, server };
}

const sampleWindow = {
  id: 4242,
  app: "C:\\Windows\\System32\\notepad.exe",
  title: "Untitled - Notepad",
  processName: "notepad",
  className: "Notepad",
  focused: true,
  minimized: false,
  width: 800,
  height: 600,
  x: 100,
  y: 100,
};

const sampleShot = {
  id: "shot-1",
  path: "", // 运行时填入
  width: 800,
  height: 600,
  sourceWidth: 1600,
  sourceHeight: 1200,
  scale: 0.5,
  method: "screen",
};

describe("MCP 握手与工具清单", () => {
  test("tools/list 暴露全部 computer-use 工具", async () => {
    const { client, server } = await connect({});
    const { tools } = await client.listTools();
    const names = tools.map((t) => t.name).sort();

    assert.deepEqual(names, [
      "activate_window",
      "click",
      "drag",
      "get_screenshot",
      "get_window",
      "get_window_state",
      "launch_app",
      "list_apps",
      "list_windows",
      "perform_secondary_action",
      "press_key",
      "scroll",
      "set_value",
      "type_text",
    ]);

    for (const tool of tools) {
      assert.ok(tool.description && tool.description.length > 20, `${tool.name} 缺少有意义的描述`);
    }

    const click = tools.find((t) => t.name === "click");
    assert.equal(click.inputSchema.properties.window_id.type, "integer");
    assert.equal(click.inputSchema.properties.element_index.type, "integer");

    await server.close();
  });

  test("只读工具带有 readOnlyHint 注解", async () => {
    const { client, server } = await connect({});
    const { tools } = await client.listTools();
    const readOnlyNames = ["list_apps", "list_windows", "get_window", "get_window_state", "get_screenshot"];
    for (const name of readOnlyNames) {
      const tool = tools.find((t) => t.name === name);
      assert.equal(tool.annotations?.readOnlyHint, true, `${name} 应标记为只读`);
    }
    const click = tools.find((t) => t.name === "click");
    assert.equal(click.annotations?.readOnlyHint, false);
    await server.close();
  });
});

describe("查询类工具", () => {
  test("list_apps 输出人类可读的 app/window 列表", async () => {
    const { client, server } = await connect({
      list_apps: {
        count: 1,
        apps: [{ id: sampleWindow.app, displayName: "notepad", windows: [sampleWindow] }],
      },
    });

    const result = await client.callTool({ name: "list_apps", arguments: {} });
    assert.equal(result.isError, undefined);
    const body = result.content[0].text;
    assert.match(body, /1 application\(s\)/);
    assert.match(body, /notepad/);
    assert.match(body, /window_id 4242/);
    assert.match(body, /Untitled - Notepad/);
    await server.close();
  });

  test("list_windows 无结果时给出明确说明而不是空字符串", async () => {
    const { client, server } = await connect({ list_windows: { count: 0, windows: [] } });
    const result = await client.callTool({ name: "list_windows", arguments: {} });
    assert.match(result.content[0].text, /No targetable windows/);
    await server.close();
  });
});

describe("get_window_state", () => {
  const handlers = () => ({
    get_window_state: {
      window: sampleWindow,
      accessibility: {
        tree: '0 Window "Untitled - Notepad"\n\t1 Edit "Text Editor" value=""\n\t2 Button "Save" actions=[Invoke]\n',
        nodeCount: 3,
        truncated: false,
        focusedElement: 'Edit "Text Editor"',
      },
      screenshots: [{ ...sampleShot, path: shotPath }],
    },
  });

  test("同时返回文本树与截图图像块", async () => {
    const { client, server } = await connect(handlers());
    const result = await client.callTool({ name: "get_window_state", arguments: { window_id: 4242 } });

    const kinds = result.content.map((c) => c.type);
    assert.deepEqual(kinds, ["text", "text", "image"]);

    assert.match(result.content[0].text, /window 4242/);
    assert.match(result.content[0].text, /screenshot shot-1: 800x600/);
    assert.match(result.content[0].text, /scaled 50%/);
    assert.match(result.content[1].text, /Button "Save"/);
    assert.equal(result.content[2].mimeType, "image/jpeg");
    assert.ok(result.content[2].data.length > 50, "图片应为 base64 数据");

    await server.close();
  });

  test("参数按 helper 的 camelCase 契约传递", async () => {
    const { client, helper, server } = await connect(handlers());
    await client.callTool({
      name: "get_window_state",
      arguments: { window_id: 4242, include_screenshot: false, include_text: true },
    });
    const call = helper.lastCall("get_window_state");
    assert.deepEqual(call.params, { hwnd: 4242, includeScreenshot: false, includeText: true });
    await server.close();
  });

  test("记住当前窗口，后续工具可省略 window_id", async () => {
    const { client, helper, server } = await connect({
      ...handlers(),
      press_key: { pressed: "Return" },
    });
    await client.callTool({ name: "get_window_state", arguments: { window_id: 4242 } });
    const result = await client.callTool({ name: "press_key", arguments: { key: "Return" } });
    assert.equal(result.isError, undefined);
    assert.equal(helper.lastCall("press_key").params.hwnd, 4242);
    await server.close();
  });
});

describe("输入类工具的参数映射", () => {
  test("click 支持元素索引路径", async () => {
    const { client, helper, server } = await connect({ click: { clicked: true, x: 400, y: 300 } });
    const result = await client.callTool({
      name: "click",
      arguments: { window_id: 7, element_index: 3, click_count: 2, mouse_button: "right" },
    });
    assert.equal(result.isError, undefined);
    assert.deepEqual(helper.lastCall("click").params, {
      hwnd: 7,
      elementIndex: 3,
      clickCount: 2,
      mouseButton: "right",
    });
    assert.match(result.content[0].text, /Clicked at screen \(400, 300\)/);
    await server.close();
  });

  test("click 坐标路径带上 screenshot_id", async () => {
    const { client, helper, server } = await connect({ click: { clicked: true, x: 1, y: 2 } });
    await client.callTool({
      name: "click",
      arguments: { window_id: 7, x: 10, y: 20, screenshot_id: "shot-9" },
    });
    assert.deepEqual(helper.lastCall("click").params, {
      hwnd: 7,
      x: 10,
      y: 20,
      screenshotId: "shot-9",
    });
    await server.close();
  });

  test("scroll 的 scroll_x/scroll_y 缺省为 0", async () => {
    const { client, helper, server } = await connect({ scroll: { scrolled: true } });
    await client.callTool({ name: "scroll", arguments: { window_id: 7, x: 5, y: 6, scroll_y: 300 } });
    assert.deepEqual(helper.lastCall("scroll").params, {
      hwnd: 7,
      x: 5,
      y: 6,
      scrollX: 0,
      scrollY: 300,
    });
    await server.close();
  });

  test("drag 的四个坐标都映射正确", async () => {
    const { client, helper, server } = await connect({ drag: { dragged: true } });
    await client.callTool({
      name: "drag",
      arguments: { window_id: 7, from_x: 1, from_y: 2, to_x: 3, to_y: 4 },
    });
    assert.deepEqual(helper.lastCall("drag").params, {
      hwnd: 7,
      fromX: 1,
      fromY: 2,
      toX: 3,
      toY: 4,
    });
    await server.close();
  });

  test("set_value 与 perform_secondary_action 映射正确", async () => {
    const { client, helper, server } = await connect({
      set_value: { set: true },
      perform_secondary_action: { performed: "Expand" },
    });
    await client.callTool({ name: "set_value", arguments: { window_id: 7, element_index: 2, value: "hi" } });
    assert.deepEqual(helper.lastCall("set_value").params, { hwnd: 7, elementIndex: 2, value: "hi" });

    await client.callTool({
      name: "perform_secondary_action",
      arguments: { window_id: 7, element_index: 2, action: "Expand" },
    });
    assert.deepEqual(helper.lastCall("perform_secondary_action").params, {
      hwnd: 7,
      elementIndex: 2,
      action: "Expand",
    });
    await server.close();
  });
});

describe("错误契约", () => {
  test("helper 结构化错误转成 isError 并保留 code", async () => {
    const { client, server } = await connect({
      click: new FakeHelperError("stale_element_index", "Element index 99 is not valid"),
    });
    const result = await client.callTool({ name: "click", arguments: { window_id: 1, element_index: 99 } });
    assert.equal(result.isError, true);
    assert.match(result.content[0].text, /\[stale_element_index\]/);
    assert.match(result.content[0].text, /not valid/);
    await server.close();
  });

  test("未选择窗口时给出可操作的指引", async () => {
    const { client, server } = await connect({ click: { clicked: true, x: 0, y: 0 } });
    const result = await client.callTool({ name: "click", arguments: { element_index: 0 } });
    assert.equal(result.isError, true);
    assert.match(result.content[0].text, /\[no_target_window\]/);
    assert.match(result.content[0].text, /list_apps/);
    await server.close();
  });

  test("缺少必填参数时由 schema 拒绝", async () => {
    const { client, server } = await connect({ press_key: { pressed: "Return" } });
    const result = await client.callTool({ name: "press_key", arguments: { window_id: 1 } });
    assert.equal(result.isError, true);
    await server.close();
  });
});
