/**
 * 真实链路测试：MCP 工具调用 -> HelperClient -> cu-helper.exe -> 真实 GUI 操作。
 *
 * 与 mcp.test.mjs 的区别：那个用 mock helper 验证协议层，这个连的是真正的原生 helper，
 * 并且会真的启动测试靶程序、真的点击、真的输入。
 *
 * 传输：helper 优先走 stdio 管道，被宿主拒绝时自动回退到回环 TCP —— 两种路径都跑同一套断言。
 *
 * 运行：node tests/mcp-live.mjs
 */
import { spawn } from "node:child_process";
import { existsSync } from "node:fs";
import path from "node:path";
import process from "node:process";
import { fileURLToPath } from "node:url";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { HelperClient } from "../dist/helper-client.js";
import { registerTools } from "../dist/tools.js";

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, "..");
const fixtureExe = path.join(here, "fixture", "bin", "test-target.exe");

let passed = 0;
let failed = 0;

function check(name, condition, detail = "") {
  if (condition) {
    passed++;
    console.log(`  PASS  ${name}`);
  } else {
    failed++;
    console.log(`  FAIL  ${name}${detail ? `\n        ${detail}` : ""}`);
  }
}

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

/** 从格式化树里找含指定子串的行，解析出它的元素索引。 */
function findIndex(tree, needle) {
  for (const line of tree.split("\n")) {
    if (line.includes(needle)) {
      const match = /^\s*(\d+)\s/.exec(line);
      if (match) return Number.parseInt(match[1], 10);
    }
  }
  return -1;
}

function lineFor(tree, index) {
  for (const line of tree.split("\n")) {
    const match = /^\s*(\d+)\s/.exec(line);
    if (match && Number.parseInt(match[1], 10) === index) return line.trim();
  }
  return "";
}

const firstText = (result) => result.content.find((c) => c.type === "text")?.text ?? "";

async function main() {
  if (!existsSync(fixtureExe)) {
    throw new Error(`测试靶程序不存在：${fixtureExe}\n先运行：pwsh -File tests/e2e.ps1（它会编译靶程序）`);
  }

  process.env.CU_HELPER_CACHE_DIR ??= path.join(repoRoot, ".tmp", "cu-cache");

  const helper = new HelperClient();
  const server = new McpServer({ name: "computer-use", version: "live-test" });
  registerTools(server, helper);

  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
  const client = new Client({ name: "live-test-client", version: "1.0.0" }, { capabilities: {} });
  await Promise.all([client.connect(clientTransport), server.connect(serverTransport)]);

  // stdio:ignore 是宿主允许的启动方式（GUI 程序本来也不需要我们的管道）
  const fixture = spawn(fixtureExe, [], { stdio: "ignore", detached: false });

  try {
    // ---------------- 等待靶窗口出现 ----------------
    let target = null;
    for (let i = 0; i < 40 && !target; i++) {
      await sleep(250);
      const result = await client.callTool({ name: "list_windows", arguments: {} });
      if (result.isError) continue;
      const text = firstText(result);
      const match = /window (\d+) — [^\n]*CU Helper Test Target/.exec(text);
      if (match) target = Number.parseInt(match[1], 10);
    }

    console.log("\n== 真实链路：MCP 工具 -> 原生 helper -> GUI ==");
    console.log(`传输方式: ${helper.activeTransport ?? "unknown"}`);
    check("list_windows 找到测试靶窗口", target !== null, "靶窗口未在 10 秒内出现");

    if (target === null) return;

    // ---------------- 抓取状态 ----------------
    const state = await client.callTool({
      name: "get_window_state",
      arguments: { window_id: target, include_screenshot: true, include_text: true },
    });
    check("get_window_state 成功", !state.isError, firstText(state));

    const tree = state.content.filter((c) => c.type === "text")[1]?.text ?? "";
    check("无障碍树包含 clickButton", tree.includes("clickButton"));
    check("无障碍树包含 inputBox", tree.includes("inputBox"));
    check("初始状态为 status:idle", tree.includes("status:idle"));

    const image = state.content.find((c) => c.type === "image");
    check("返回了截图图像块", Boolean(image) && image.data.length > 100, `type=${image?.type}`);
    check("图像为 JPEG", image?.mimeType === "image/jpeg");

    // ---------------- 按索引点击 ----------------
    const buttonIndex = findIndex(tree, "clickButton");
    const click = await client.callTool({
      name: "click",
      arguments: { window_id: target, element_index: buttonIndex },
    });
    check("click(element_index) 成功", !click.isError, firstText(click));

    await sleep(300);
    const afterClick = await client.callTool({
      name: "get_window_state",
      arguments: { window_id: target, include_screenshot: false, include_text: true },
    });
    const treeAfterClick = afterClick.content.filter((c) => c.type === "text")[1]?.text ?? "";
    check("点击改变了界面状态", treeAfterClick.includes("status:clicked:1"), treeAfterClick.slice(0, 200));

    // ---------------- 输入中文 ----------------
    const inputIndex = findIndex(treeAfterClick, "inputBox");
    const typed = await client.callTool({
      name: "type_text",
      arguments: { window_id: target, element_index: inputIndex, text: "中文输入 test 42" },
    });
    check("type_text 成功", !typed.isError, firstText(typed));

    await sleep(300);
    const afterType = await client.callTool({
      name: "get_window_state",
      arguments: { window_id: target, include_screenshot: false, include_text: true },
    });
    const treeAfterType = afterType.content.filter((c) => c.type === "text")[1]?.text ?? "";
    check("文本框写入了中文", lineFor(treeAfterType, inputIndex).includes("中文输入 test 42"), lineFor(treeAfterType, inputIndex));

    // ---------------- 次要动作 ----------------
    const toggleIndex = findIndex(treeAfterType, "toggleCheck");
    const toggled = await client.callTool({
      name: "perform_secondary_action",
      arguments: { window_id: target, element_index: toggleIndex, action: "Toggle" },
    });
    check("perform_secondary_action(Toggle) 成功", !toggled.isError, firstText(toggled));

    await sleep(300);
    const afterToggle = await client.callTool({
      name: "get_window_state",
      arguments: { window_id: target, include_screenshot: false, include_text: true },
    });
    const treeAfterToggle = afterToggle.content.filter((c) => c.type === "text")[1]?.text ?? "";
    check("复选框状态被切换", treeAfterToggle.includes("status:toggled:"), treeAfterToggle.slice(0, 200));

    // ---------------- 省略 window_id（会话当前窗口） ----------------
    const implicit = await client.callTool({ name: "press_key", arguments: { key: "Tab" } });
    check("省略 window_id 时使用会话当前窗口", !implicit.isError, firstText(implicit));

    // ---------------- 错误契约 ----------------
    const stale = await client.callTool({
      name: "click",
      arguments: { window_id: target, element_index: 99999 },
    });
    check("越界索引返回 stale_element_index", stale.isError && firstText(stale).includes("stale_element_index"), firstText(stale));
  } finally {
    await helper.close().catch(() => undefined);
    await server.close().catch(() => undefined);
    try {
      fixture.kill();
    } catch {
      /* 忽略 */
    }
  }
}

main()
  .then(() => {
    console.log("\n----------------------------------------");
    if (failed === 0) {
      console.log(`全部通过：${passed} 项`);
      process.exit(0);
    }
    console.log(`通过 ${passed} 项，失败 ${failed} 项`);
    process.exit(1);
  })
  .catch((error) => {
    console.error("测试异常终止：", error);
    process.exit(1);
  });
