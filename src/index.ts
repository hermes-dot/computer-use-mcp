#!/usr/bin/env node
/**
 * codex-computer-use-mcp 入口。
 *
 * 架构：本进程是 MCP server（stdio），把工具调用转成 NDJSON 请求发给
 * 长驻的原生 helper（cu-helper.exe），由 helper 执行 UI Automation / SendInput / 截图。
 *
 * 注意：stdout 是 MCP 协议通道，任何调试输出都必须走 stderr。
 */
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { HelperClient } from "./helper-client.js";
import { resolveHelperPath } from "./helper-locator.js";
import { registerTools } from "./tools.js";

const VERSION = "0.1.0";

function log(message: string): void {
  process.stderr.write(`[computer-use-mcp] ${message}\n`);
}

async function main(): Promise<void> {
  const argv = process.argv.slice(2);

  if (argv.includes("--version") || argv.includes("-v")) {
    process.stdout.write(`${VERSION}\n`);
    return;
  }

  if (argv.includes("--check")) {
    // 自检入口：让用户在不启动 MCP 的情况下确认原生依赖是否就绪。
    try {
      const helperPath = resolveHelperPath();
      const client = new HelperClient();
      const pong = await client.call<{ pong: boolean; dpiAware: number }>("ping", {});
      const windows = await client.call<{ count: number }>("list_windows", { detail: false });
      await client.close();
      process.stdout.write(
        `helper: ${helperPath}\nping: ok (dpi awareness = ${pong.dpiAware})\ntargetable windows: ${windows.count}\n`,
      );
      return;
    } catch (error) {
      process.stderr.write(`check failed: ${error instanceof Error ? error.message : String(error)}\n`);
      process.exitCode = 1;
      return;
    }
  }

  const helper = new HelperClient();
  const server = new McpServer({ name: "computer-use", version: VERSION });

  registerTools(server, helper);

  let shuttingDown = false;
  const shutdown = async (reason: string): Promise<void> => {
    if (shuttingDown) return;
    shuttingDown = true;
    log(`shutting down (${reason})`);
    await helper.close().catch(() => undefined);
    await server.close().catch(() => undefined);
    process.exit(0);
  };

  process.on("SIGINT", () => void shutdown("SIGINT"));
  process.on("SIGTERM", () => void shutdown("SIGTERM"));
  process.stdin.on("close", () => void shutdown("stdin closed"));

  const transport = new StdioServerTransport();
  await server.connect(transport);

  // 启动阶段不预先拉起 helper：让 MCP 握手先完成，
  // 原生依赖缺失时表现为工具调用报错，而不是整个 server 起不来。
  try {
    log(`ready (helper: ${resolveHelperPath()})`);
  } catch (error) {
    log(`warning: ${error instanceof Error ? error.message : String(error)}`);
  }
}

main().catch((error) => {
  log(`fatal: ${error instanceof Error ? error.stack ?? error.message : String(error)}`);
  process.exit(1);
});
