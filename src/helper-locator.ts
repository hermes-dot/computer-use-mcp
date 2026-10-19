/**
 * 原生 helper 进程的定位。
 *
 * 架构是跨平台的：MCP 层（本包）与平台无关，只依赖一个约定——
 * "在 native/<平台>/bin/ 下有一个可执行文件，能通过 stdio 或 TCP 讲 NDJSON"。
 * 每个平台各自提供该可执行文件即可接入，无需改动 MCP 层与工具定义。
 *
 * 当前状态：
 *   windows  已实现（C# / .NET Framework，见 native/windows）
 *   macos    未实现（可基于 AXUIElement + CGEvent + ScreenCaptureKit）
 *   linux    未实现（可基于 AT-SPI + XTest/uinput，Codex 在 Linux 上正是走 AT-SPI）
 */
import { existsSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));

/** 包根目录（dist/ 的上一级）。 */
export const packageRoot = path.resolve(here, "..");

export interface PlatformBackend {
  /** 目录名，位于 native/ 之下 */
  dir: string;
  /** 构建产物的文件名 */
  binary: string;
  /** 实现状态与建议的技术路线，用于错误提示与文档 */
  status: "implemented" | "planned";
  hint: string;
}

export const BACKENDS: Record<string, PlatformBackend> = {
  win32: {
    dir: "windows",
    binary: "cu-helper.exe",
    status: "implemented",
    hint: "Build with: npm run build:native (needs the .NET SDK and .NET Framework 4.x)",
  },
  darwin: {
    dir: "macos",
    binary: "cu-helper",
    status: "planned",
    hint:
      "No macOS backend yet. A backend needs: AXUIElement for the accessibility tree, " +
      "CGEvent for input, ScreenCaptureKit for window capture. See docs/architecture.md.",
  },
  linux: {
    dir: "linux",
    binary: "cu-helper",
    status: "planned",
    hint:
      "No Linux backend yet. A backend needs: AT-SPI for the accessibility tree, " +
      "XTest/uinput for input, and a compositor-aware capture path. See docs/architecture.md.",
  },
};

export function currentBackend(): PlatformBackend | undefined {
  return BACKENDS[process.platform];
}

/** 解析原生 helper 可执行文件路径。 */
export function resolveHelperPath(): string {
  const override = process.env.CU_HELPER_PATH?.trim();
  if (override) {
    if (!existsSync(override)) {
      throw new Error(`CU_HELPER_PATH points to a missing file: ${override}`);
    }
    return override;
  }

  const backend = currentBackend();
  if (!backend) {
    throw new Error(
      [
        `Unsupported platform: ${process.platform}.`,
        `Backends exist for: ${Object.keys(BACKENDS).join(", ")}.`,
        "Adding one only requires an executable under native/<platform>/bin/ that speaks the",
        "NDJSON protocol described in docs/architecture.md — the MCP layer stays unchanged.",
      ].join("\n"),
    );
  }

  // 同时接受带扩展名与不带扩展名的产物，方便从源码目录直接运行。
  const candidates = [
    path.join(packageRoot, "native", backend.dir, "bin", backend.binary),
    path.join(packageRoot, "native", backend.dir, "bin", "cu-helper"),
    path.join(packageRoot, "native", backend.dir, "bin", "cu-helper.exe"),
  ];

  for (const candidate of candidates) {
    if (existsSync(candidate)) return candidate;
  }

  throw new Error(
    [
      `The ${process.platform} helper was not found.`,
      `Looked in: ${[...new Set(candidates)].join(", ")}`,
      backend.hint,
      "Or point CU_HELPER_PATH at an existing helper executable.",
    ].join("\n"),
  );
}

/** helper 落盘截图与缓存的目录；可用 CU_HELPER_CACHE_DIR 覆盖。 */
export function resolveCacheDir(): string | undefined {
  const override = process.env.CU_HELPER_CACHE_DIR?.trim();
  return override && override.length > 0 ? override : undefined;
}
