/**
 * 与原生 helper 通信的 NDJSON 客户端。
 *
 * 协议：一行一个 JSON 请求 / 响应。
 *   -> {"id":1,"method":"get_window_state","params":{...}}
 *   <- {"id":1,"ok":true,"result":{...}}
 *   <- {"id":1,"ok":false,"error":{"code":"...","message":"..."}}
 *
 * 两种传输，自动选择：
 *   1. stdio 管道（默认，最轻量）；
 *   2. 回环 TCP（当宿主禁止子进程管道时自动回退 —— 部分沙箱会以 EPERM 拒绝 pipe stdio）。
 *   两种模式跑的是同一套协议，因此上层工具代码完全无感。
 *
 * 为什么用长驻子进程而不是每次调用起一个：
 *   element_index 的映射（UIA 元素引用）保存在 helper 进程内，
 *   每次调用重启进程会让"先看树、再点元素"这条最基本的工作流失效。
 */
import { spawn, type ChildProcess } from "node:child_process";
import { randomBytes } from "node:crypto";
import { existsSync } from "node:fs";
import { readFile, rm } from "node:fs/promises";
import net from "node:net";
import os from "node:os";
import path from "node:path";
import readline from "node:readline";
import { resolveCacheDir, resolveHelperPath } from "./helper-locator.js";

export interface HelperErrorShape {
  code: string;
  message: string;
}

/** helper 返回的结构化错误，保留 code 供上层区分处理。 */
export class HelperError extends Error {
  readonly code: string;

  constructor(code: string, message: string) {
    super(message);
    this.name = "HelperError";
    this.code = code;
  }
}

interface PendingCall {
  resolve: (value: unknown) => void;
  reject: (error: Error) => void;
  timer: NodeJS.Timeout;
  method: string;
}

interface HelperResponse {
  id: number;
  ok: boolean;
  result?: unknown;
  error?: HelperErrorShape;
}

type Transport = "pipe" | "tcp";

function isPermissionError(error: unknown): boolean {
  return (error as { code?: string } | null)?.code === "EPERM";
}

const sleep = (ms: number): Promise<void> => new Promise((resolve) => setTimeout(resolve, ms));

export class HelperClient {
  private child: ChildProcess | null = null;
  private socket: net.Socket | null = null;
  private reader: readline.Interface | null = null;
  private writer: NodeJS.WritableStream | null = null;
  private readonly pending = new Map<number, PendingCall>();
  private nextId = 1;
  private stderrTail: string[] = [];
  private closed = false;
  private starting: Promise<void> | null = null;
  private transport: Transport | null = null;
  private portFile: string | null = null;

  /** 单次调用的默认超时；截图 + UIA 遍历在超大窗口上可能到秒级。 */
  readonly defaultTimeoutMs: number;

  constructor(options: { timeoutMs?: number } = {}) {
    this.defaultTimeoutMs = options.timeoutMs ?? 30_000;
  }

  /** 当前使用的传输方式（未启动时为 null），用于诊断输出。 */
  get activeTransport(): Transport | null {
    return this.transport;
  }

  private async ensureStarted(): Promise<void> {
    if (this.child && this.child.exitCode === null && !this.closed) return;
    if (this.closed) throw new Error("Helper client has been closed.");
    if (!this.starting) {
      this.starting = this.start().finally(() => {
        this.starting = null;
      });
    }
    await this.starting;
  }

  private async start(): Promise<void> {
    const forced = process.env.CU_HELPER_TRANSPORT?.trim().toLowerCase();
    if (forced === "tcp") {
      await this.startTcp();
      return;
    }
    try {
      await this.startPipe();
      return;
    } catch (error) {
      if (forced === "pipe" || !isPermissionError(error)) throw error;
      // 宿主禁止管道：回退到回环 TCP。
      await this.startTcp();
    }
  }

  // ------------------------------------------------------------------ stdio 传输

  private async startPipe(): Promise<void> {
    const helperPath = resolveHelperPath();
    const child = spawn(helperPath, [], {
      stdio: ["pipe", "pipe", "pipe"],
      windowsHide: true,
      env: this.childEnv(),
    });

    await new Promise<void>((resolve, reject) => {
      const onError = (error: Error): void => {
        child.removeListener("spawn", onSpawn);
        reject(error);
      };
      const onSpawn = (): void => {
        child.removeListener("error", onError);
        resolve();
      };
      child.once("error", onError);
      child.once("spawn", onSpawn);
    }).catch((error) => {
      try {
        child.kill();
      } catch {
        /* 忽略 */
      }
      throw error;
    });

    this.child = child;
    this.transport = "pipe";
    this.attachChild(child);
    this.writer = child.stdin!;
    this.reader = readline.createInterface({ input: child.stdout! });
    this.reader.on("line", (line) => this.handleLine(line));
  }

  // ------------------------------------------------------------------ TCP 传输

  private async startTcp(): Promise<void> {
    const helperPath = resolveHelperPath();
    const token = randomBytes(24).toString("hex");
    const portFile = path.join(os.tmpdir(), `cu-helper-${process.pid}-${Date.now()}.port`);
    await rm(portFile, { force: true }).catch(() => undefined);
    this.portFile = portFile;

    const child = spawn(
      helperPath,
      ["--tcp", "--port", "0", "--port-file", portFile, "--token", token],
      { stdio: "ignore", windowsHide: true, env: this.childEnv() },
    );
    this.child = child;
    this.transport = "tcp";
    this.attachChild(child);

    const port = await this.waitForPort(portFile, 15_000);
    const socket = await this.connectSocket(port);
    this.socket = socket;
    socket.setNoDelay(true);

    this.writer = socket;
    this.reader = readline.createInterface({ input: socket });
    this.reader.on("line", (line) => this.handleLine(line));

    // 握手：helper 校验 token 后才接受业务请求。
    this.writer.write(token + "\n");
    const hello = await new Promise<string>((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error("cu-helper TCP handshake timed out")), 5_000);
      this.reader!.once("line", (line) => {
        clearTimeout(timer);
        resolve(line);
      });
    });
    const parsed = JSON.parse(hello) as HelperResponse;
    if (!parsed.ok) {
      throw new HelperError(
        parsed.error?.code ?? "handshake_failed",
        `cu-helper rejected the TCP handshake: ${parsed.error?.message ?? "unknown reason"}`,
      );
    }
  }

  private async waitForPort(portFile: string, timeoutMs: number): Promise<number> {
    const deadline = Date.now() + timeoutMs;
    while (Date.now() < deadline) {
      if (this.child && this.child.exitCode !== null) {
        throw new Error(`cu-helper exited with code ${this.child.exitCode} before reporting its port.`);
      }
      if (existsSync(portFile)) {
        const raw = (await readFile(portFile, "utf8").catch(() => "")).trim();
        const port = Number.parseInt(raw, 10);
        if (Number.isFinite(port) && port > 0) return port;
      }
      await sleep(100);
    }
    throw new Error(`cu-helper did not report a TCP port within ${timeoutMs}ms.`);
  }

  private async connectSocket(port: number): Promise<net.Socket> {
    return await new Promise<net.Socket>((resolve, reject) => {
      const socket = net.connect({ host: "127.0.0.1", port });
      const onError = (error: Error): void => {
        socket.destroy();
        reject(error);
      };
      socket.once("error", onError);
      socket.once("connect", () => {
        socket.removeListener("error", onError);
        resolve(socket);
      });
    });
  }

  // ------------------------------------------------------------------ 共用

  private childEnv(): NodeJS.ProcessEnv {
    const env: NodeJS.ProcessEnv = { ...process.env };
    const cacheDir = resolveCacheDir();
    if (cacheDir) env.CU_HELPER_CACHE_DIR = cacheDir;
    return env;
  }

  private attachChild(child: ChildProcess): void {
    this.stderrTail = [];
    child.stderr?.setEncoding("utf8");
    child.stderr?.on("data", (chunk: string) => {
      // helper 正常工作时 stderr 应该是空的；保留尾部用于诊断崩溃原因。
      for (const line of chunk.split(/\r?\n/)) {
        if (line.trim().length === 0) continue;
        this.stderrTail.push(line);
        if (this.stderrTail.length > 40) this.stderrTail.shift();
      }
    });
    child.on("error", (error) => this.failAll(error));
    child.on("exit", (code, signal) => {
      if (this.closed) return;
      const reason = signal ? `signal ${signal}` : `exit code ${code}`;
      const detail = this.stderrTail.length > 0 ? `\nhelper stderr:\n${this.stderrTail.join("\n")}` : "";
      this.failAll(new Error(`cu-helper terminated unexpectedly (${reason}).${detail}`));
      this.teardownHandles();
    });
  }

  private teardownHandles(): void {
    this.child = null;
    this.socket = null;
    this.writer = null;
    this.reader?.close();
    this.reader = null;
  }

  private handleLine(line: string): void {
    const trimmed = line.trim();
    if (trimmed.length === 0) return;

    let parsed: HelperResponse;
    try {
      parsed = JSON.parse(trimmed) as HelperResponse;
    } catch {
      // 非协议输出（例如 .NET 的启动告警）不应打断请求队列。
      return;
    }

    const pending = this.pending.get(parsed.id);
    if (!pending) return;
    this.pending.delete(parsed.id);
    clearTimeout(pending.timer);

    if (parsed.ok) {
      pending.resolve(parsed.result);
    } else {
      const error = parsed.error ?? { code: "unknown_error", message: "helper returned an unspecified failure" };
      pending.reject(new HelperError(error.code, error.message));
    }
  }

  private failAll(error: Error): void {
    for (const [, pending] of this.pending) {
      clearTimeout(pending.timer);
      pending.reject(error);
    }
    this.pending.clear();
  }

  async call<T = unknown>(
    method: string,
    params: Record<string, unknown> = {},
    timeoutMs?: number,
  ): Promise<T> {
    await this.ensureStarted();
    const writer = this.writer;
    if (!writer) throw new Error("cu-helper transport is not available.");

    const id = this.nextId++;
    const payload = JSON.stringify({ id, method, params });

    return await new Promise<T>((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(
          new HelperError(
            "timeout",
            `cu-helper did not answer '${method}' within ${timeoutMs ?? this.defaultTimeoutMs}ms. ` +
              "The target window may be hung; try again or pick another window.",
          ),
        );
      }, timeoutMs ?? this.defaultTimeoutMs);

      this.pending.set(id, {
        resolve: resolve as (value: unknown) => void,
        reject,
        timer,
        method,
      });

      writer.write(payload + "\n", (error?: Error | null) => {
        if (!error) return;
        this.pending.delete(id);
        clearTimeout(timer);
        reject(error);
      });
    });
  }

  /** 优雅关闭：先请 helper 自行退出，再兜底强杀。 */
  async close(): Promise<void> {
    const child = this.child;
    if (!child) {
      this.closed = true;
      return;
    }
    try {
      await this.call("shutdown", {}, 2_000);
    } catch {
      // 忽略：下面统一兜底
    }
    this.closed = true;
    this.failAll(new Error("Helper client closed."));
    try {
      this.socket?.destroy();
    } catch {
      /* 忽略 */
    }
    if (child.exitCode === null) {
      try {
        child.kill();
      } catch {
        /* 忽略 */
      }
    }
    this.teardownHandles();
    if (this.portFile) {
      await rm(this.portFile, { force: true }).catch(() => undefined);
      this.portFile = null;
    }
  }
}
