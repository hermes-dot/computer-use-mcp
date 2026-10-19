/**
 * 原生 helper 进程的定位与启动。
 *
 * helper 是随包分发的 cu-helper.exe（Windows / .NET Framework，零第三方依赖）。
 * 查找顺序：环境变量 CU_HELPER_PATH -> 包内 native/windows/bin/ -> 常见构建产物路径。
 */
import { existsSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));

/** 包根目录（dist/ 的上一级）。 */
export const packageRoot = path.resolve(here, "..");

export function resolveHelperPath(): string {
  const override = process.env.CU_HELPER_PATH?.trim();
  if (override) {
    if (!existsSync(override)) {
      throw new Error(
        `CU_HELPER_PATH points to a missing file: ${override}`,
      );
    }
    return override;
  }

  const candidates = [
    path.join(packageRoot, "native", "windows", "bin", "cu-helper.exe"),
    // 允许从源码目录直接运行（未打包场景）
    path.join(packageRoot, "native", "windows", "bin", "cu-helper"),
  ];

  for (const candidate of candidates) {
    if (existsSync(candidate)) return candidate;
  }

  throw new Error(
    [
      "cu-helper executable was not found.",
      `Looked in: ${candidates.join(", ")}`,
      "Build it with:  npm run build:native   (requires the .NET SDK and .NET Framework 4.x)",
      "Or point CU_HELPER_PATH at an existing cu-helper.exe.",
    ].join("\n"),
  );
}

/** helper 落盘截图与缓存的目录；可用 CU_HELPER_CACHE_DIR 覆盖。 */
export function resolveCacheDir(): string | undefined {
  const override = process.env.CU_HELPER_CACHE_DIR?.trim();
  return override && override.length > 0 ? override : undefined;
}
