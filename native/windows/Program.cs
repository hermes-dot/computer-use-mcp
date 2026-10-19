// cu-helper：Windows computer-use 原生后端。
//
// 协议：stdin/stdout 上的一行一个 JSON（NDJSON）。
//   请求  {"id":1,"method":"get_window_state","params":{...}}
//   成功  {"id":1,"ok":true,"result":{...}}
//   失败  {"id":1,"ok":false,"error":{"code":"...","message":"..."}}
//
// 为什么把原生能力做成独立进程而不是 Node 原生插件：
//   UI Automation 是 COM 接口、SendInput 需要精确的结构体布局，
//   这些在 .NET Framework 上零依赖可用；而 Node 侧负责 MCP 协议与工具编排，
//   两边通过进程边界解耦，任何一侧崩溃都不会拖垮另一侧。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace CuHelper
{
    internal static class Program
    {
        private static readonly Dictionary<long, AccessibilitySnapshot> Snapshots =
            new Dictionary<long, AccessibilitySnapshot>();

        private static StreamWriter _output;

        private static int Main(string[] args)
        {
            var utf8 = new UTF8Encoding(false);
            try
            {
                // 控制台被重定向时，.NET 默认使用系统 ANSI 代码页；必须显式切到 UTF-8，
                // 否则窗口标题里的中文会变成乱码。
                Console.InputEncoding = utf8;
                Console.OutputEncoding = utf8;
            }
            catch (IOException)
            {
                // stdio 被置为 NUL（TCP 模式）时无控制台可配置，忽略即可。
            }

            // DPI 感知必须在任何窗口/坐标 API 之前声明，且对所有运行模式都要生效：
            // 漏掉它会让进程被系统做 DPI 虚拟化，窗口矩形、UIA 元素坐标与截图像素三者全部错位。
            Win32.EnableDpiAwareness();

            if (args.Length > 0 && args[0] == "--selftest") return SelfTest();
            if (args.Length > 0 && args[0] == "--tcp") return TcpTransport.Run(args, utf8);

            var output = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = false };
            var input = new StreamReader(Console.OpenStandardInput(), utf8);
            return Serve(input, output);
        }

        /// <summary>请求处理主循环；stdio 与 TCP 两种传输共用同一套方法分发。</summary>
        internal static int Serve(StreamReader input, StreamWriter output)
        {
            _output = output;
            string line;
            while ((line = input.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.Length == 0) continue;

                long id = 0;
                try
                {
                    var request = Json.Parse(line);
                    id = Json.Long(request, "id") ?? 0;
                    string method = Json.Str(request, "method") ?? "";
                    var parameters = Json.Map(request, "params") ?? Json.Obj();

                    if (method == "shutdown")
                    {
                        Respond(id, true, Json.Obj());
                        break;
                    }

                    object result = Dispatch(method, parameters);
                    Respond(id, true, result);
                }
                catch (HelperException ex)
                {
                    RespondError(id, ex.Code, ex.Message);
                }
                catch (JsonException ex)
                {
                    RespondError(id, "bad_request", ex.Message);
                }
                catch (Exception ex)
                {
                    // 兜底：任何未预期异常都结构化返回，避免 helper 直接崩掉导致 MCP 连接悬空。
                    RespondError(id, "internal_error", ex.GetType().Name + ": " + ex.Message);
                }
            }
            _output.Flush();
            return 0;
        }

        private static object Dispatch(string method, IDictionary<string, object> p)
        {
            switch (method)
            {
                case "ping":
                    return Json.Obj("pong", true, "dpiAware", NativeMethods.GetAwarenessFromDpiAwarenessContext(NativeMethods.GetThreadDpiAwarenessContext()));

                case "list_windows":
                    return ListWindows(p);

                case "list_apps":
                    return ListApps();

                case "get_window":
                    return GetWindow(p);

                case "activate_window":
                    return ActivateWindow(p);

                case "launch_app":
                    return LaunchApp(p);

                case "get_window_state":
                    return GetWindowState(p);

                case "element_bounds":
                    return ElementBounds(p);

                case "get_screenshot":
                    return GetDesktopScreenshot();

                case "click":
                    return Click(p);

                case "move":
                    return Move(p);

                case "type_text":
                    return TypeText(p);

                case "press_key":
                    return PressKey(p);

                case "scroll":
                    return Scroll(p);

                case "set_value":
                    return SetValue(p);

                case "drag":
                    return Drag(p);

                case "perform_secondary_action":
                    return PerformSecondaryAction(p);

                default:
                    throw new HelperException("unknown_method", $"Unknown method '{method}'.");
            }
        }

        // ---------------- 查询类方法 ----------------

        private static object ListWindows(IDictionary<string, object> p)
        {
            bool detail = Json.Bool(p, "detail", true);
            var list = new List<object>();
            foreach (var window in Win32.ListWindows()) list.Add(window.ToJson(detail));
            return Json.Obj("windows", list, "count", list.Count);
        }

        /// <summary>按 app（进程路径）聚合窗口，语义对齐 Codex 的 list_apps。</summary>
        private static object ListApps()
        {
            var byApp = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();

            foreach (var window in Win32.ListWindows())
            {
                string appId = window.AppId;
                if (string.IsNullOrEmpty(appId)) continue;

                Dictionary<string, object> entry;
                if (!byApp.TryGetValue(appId, out entry))
                {
                    entry = Json.Obj();
                    entry["id"] = appId;
                    entry["displayName"] = window.AppName;
                    entry["isRunning"] = true;
                    entry["windows"] = new List<object>();
                    byApp[appId] = entry;
                    order.Add(appId);
                }
                ((List<object>)entry["windows"]).Add(window.ToJson(false));
            }

            var apps = new List<object>();
            foreach (string appId in order) apps.Add(byApp[appId]);
            return Json.Obj("apps", apps, "count", apps.Count);
        }

        private static object GetWindow(IDictionary<string, object> p)
        {
            var window = RequireWindow(p);
            return Json.Obj("window", window.ToJson(true));
        }

        private static object ActivateWindow(IDictionary<string, object> p)
        {
            var window = RequireWindow(p);
            bool ok = Win32.ActivateWindow(new IntPtr(window.Hwnd));
            if (!ok)
            {
                throw new HelperException("activation_failed",
                    $"Could not bring window {window.Hwnd} to the foreground. " +
                    "It may be owned by an elevated process, or Windows foreground lock is blocking the switch.");
            }
            return Json.Obj("activated", true, "window", Win32.Describe(new IntPtr(window.Hwnd)).ToJson(true));
        }

        private static object LaunchApp(IDictionary<string, object> p)
        {
            string app = Json.Str(p, "app");
            if (string.IsNullOrWhiteSpace(app))
                throw new HelperException("bad_request", "launch_app requires 'app' (an app id from list_apps, an executable path, or a shell name).");

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = app,
                    UseShellExecute = true,
                };
                string arguments = Json.Str(p, "arguments");
                if (!string.IsNullOrEmpty(arguments)) startInfo.Arguments = arguments;
                string workingDirectory = Json.Str(p, "cwd");
                if (!string.IsNullOrEmpty(workingDirectory)) startInfo.WorkingDirectory = workingDirectory;

                var process = Process.Start(startInfo);
                var result = Json.Obj("launched", true, "app", app);
                if (process != null)
                {
                    try { result["pid"] = process.Id; } catch { }
                }
                return result;
            }
            catch (Exception ex)
            {
                throw new HelperException("launch_failed", $"Could not launch '{app}': {ex.Message}");
            }
        }

        private static object GetWindowState(IDictionary<string, object> p)
        {
            var window = RequireWindow(p);
            bool includeScreenshot = Json.Bool(p, "includeScreenshot", true);
            bool includeText = Json.Bool(p, "includeText", true);

            var snapshot = Uia.Capture(new IntPtr(window.Hwnd), includeText);
            Snapshots[window.Hwnd] = snapshot;

            var accessibility = Json.Obj();
            accessibility["tree"] = snapshot.Tree;
            accessibility["nodeCount"] = snapshot.NodeCount;
            accessibility["truncated"] = snapshot.Truncated;
            if (snapshot.FocusedElement.Length > 0) accessibility["focusedElement"] = snapshot.FocusedElement;
            if (snapshot.DocumentText.Length > 0) accessibility["documentText"] = snapshot.DocumentText;
            if (snapshot.SelectedText.Length > 0) accessibility["selectedText"] = snapshot.SelectedText;

            var screenshots = new List<object>();
            if (includeScreenshot)
            {
                var shot = Capture.CaptureWindow(window, allowActivate: true);
                screenshots.Add(ShotJson(shot));
            }

            // 截图/激活可能改变窗口状态，回读一次最新几何信息给调用方。
            var fresh = Win32.Describe(new IntPtr(window.Hwnd));
            return Json.Obj(
                "window", fresh.ToJson(true),
                "accessibility", accessibility,
                "screenshots", screenshots);
        }

        private static object GetDesktopScreenshot()
        {
            var shot = Capture.CaptureDesktop();
            return Json.Obj("screenshots", new List<object> { ShotJson(shot) });
        }

        /// <summary>
        /// 查询某个元素索引对应的屏幕矩形。
        /// 用途：让调用方能在"元素索引"与"截图像素坐标"之间互相换算，
        /// 也便于诊断"树里有元素但点不中"的问题。
        /// </summary>
        private static object ElementBounds(IDictionary<string, object> p)
        {
            var window = RequireWindow(p);
            int? elementIndex = Json.Int(p, "elementIndex");
            if (!elementIndex.HasValue)
                throw new HelperException("bad_request", "element_bounds requires 'elementIndex'.");

            var element = SnapshotFor(window.Hwnd).ElementAt(elementIndex.Value);
            System.Windows.Rect rect = Uia.ElementBounds(element);
            RECT bounds = Win32.WindowBounds(new IntPtr(window.Hwnd));
            bool empty = rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0;

            return Json.Obj(
                "empty", empty,
                "screenX", empty ? 0 : (int)Math.Round(rect.Left),
                "screenY", empty ? 0 : (int)Math.Round(rect.Top),
                "width", empty ? 0 : (int)Math.Round(rect.Width),
                "height", empty ? 0 : (int)Math.Round(rect.Height),
                "windowLeft", bounds.Left,
                "windowTop", bounds.Top,
                "windowWidth", bounds.Width,
                "windowHeight", bounds.Height);
        }

        private static Dictionary<string, object> ShotJson(ShotRecord shot)
        {
            var o = Json.Obj();
            o["id"] = shot.Id;
            o["path"] = shot.Path;
            o["width"] = shot.ImageWidth;
            o["height"] = shot.ImageHeight;
            o["sourceWidth"] = shot.SourceWidth;
            o["sourceHeight"] = shot.SourceHeight;
            o["scale"] = shot.Scale;
            o["method"] = shot.Method;
            return o;
        }

        // ---------------- 动作类方法 ----------------

        private static object Click(IDictionary<string, object> p)
        {
            var window = RequireWindow(p);
            EnsureForeground(window);

            int clickCount = Json.Int(p, "clickCount") ?? 1;
            int duration = Json.Int(p, "durationMs") ?? 0;
            string button = Json.Str(p, "mouseButton", "left");
            int screenX, screenY;

            int? elementIndex = Json.Int(p, "elementIndex");
            if (elementIndex.HasValue)
            {
                var element = SnapshotFor(window.Hwnd).ElementAt(elementIndex.Value);
                var rect = Uia.ElementBounds(element);
                if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0)
                {
                    throw new HelperException("element_not_visible",
                        $"Element {elementIndex.Value} has no visible bounds; it may be scrolled out of view or collapsed.");
                }
                screenX = (int)Math.Round(rect.Left + rect.Width / 2);
                screenY = (int)Math.Round(rect.Top + rect.Height / 2);
            }
            else
            {
                int? x = Json.Int(p, "x"), y = Json.Int(p, "y");
                if (!x.HasValue || !y.HasValue)
                    throw new HelperException("bad_request", "click requires either 'elementIndex' or both 'x' and 'y'.");
                var shot = ResolveShot(window.Hwnd, Json.Str(p, "screenshotId"));
                ToScreen(window, shot, x.Value, y.Value, out screenX, out screenY);
            }

            VerifyPointBelongsTo(window, screenX, screenY);
            Input.Click(screenX, screenY, button, clickCount, duration);
            return Json.Obj("clicked", true, "x", screenX, "y", screenY);
        }

        private static object Move(IDictionary<string, object> p)
        {
            var window = RequireWindow(p);
            EnsureForeground(window);
            int? x = Json.Int(p, "x"), y = Json.Int(p, "y");
            if (!x.HasValue || !y.HasValue)
                throw new HelperException("bad_request", "move requires 'x' and 'y'.");
            var shot = ResolveShot(window.Hwnd, Json.Str(p, "screenshotId"));
            int screenX, screenY;
            ToScreen(window, shot, x.Value, y.Value, out screenX, out screenY);
            VerifyPointBelongsTo(window, screenX, screenY);
            Input.MoveTo(screenX, screenY);
            return Json.Obj("moved", true, "x", screenX, "y", screenY);
        }

        private static object TypeText(IDictionary<string, object> p)
        {
            var window = RequireWindow(p);
            EnsureForeground(window);

            int? elementIndex = Json.Int(p, "elementIndex");
            if (elementIndex.HasValue)
            {
                var element = SnapshotFor(window.Hwnd).ElementAt(elementIndex.Value);
                if (!Uia.TryFocus(element))
                    throw new HelperException("focus_failed", $"Could not focus element {elementIndex.Value} before typing.");
            }

            string text = Json.Str(p, "text", "");
            Input.TypeText(text);
            return Json.Obj("typed", true, "length", text.Length);
        }

        private static object PressKey(IDictionary<string, object> p)
        {
            var window = RequireWindow(p);
            EnsureForeground(window);
            string key = Json.Str(p, "key");
            var chord = Input.ParseChord(key);
            Input.PressChord(chord, Json.Int(p, "durationMs") ?? 0);
            return Json.Obj("pressed", chord.ToString());
        }

        private static object Scroll(IDictionary<string, object> p)
        {
            var window = RequireWindow(p);
            EnsureForeground(window);
            int? x = Json.Int(p, "x"), y = Json.Int(p, "y");
            if (!x.HasValue || !y.HasValue)
                throw new HelperException("bad_request", "scroll requires 'x' and 'y' (the point to scroll over).");

            double scrollX = Json.Double(p, "scrollX") ?? 0;
            double scrollY = Json.Double(p, "scrollY") ?? 0;

            var shot = ResolveShot(window.Hwnd, Json.Str(p, "screenshotId"));
            int screenX, screenY;
            ToScreen(window, shot, x.Value, y.Value, out screenX, out screenY);
            VerifyPointBelongsTo(window, screenX, screenY);
            Input.Scroll(screenX, screenY, scrollX, scrollY);
            return Json.Obj("scrolled", true, "x", screenX, "y", screenY, "scrollX", scrollX, "scrollY", scrollY);
        }

        private static object SetValue(IDictionary<string, object> p)
        {
            var window = RequireWindow(p);
            int? elementIndex = Json.Int(p, "elementIndex");
            if (!elementIndex.HasValue)
                throw new HelperException("bad_request", "set_value requires 'elementIndex'.");
            var element = SnapshotFor(window.Hwnd).ElementAt(elementIndex.Value);
            Uia.SetValue(element, Json.Str(p, "value", ""));
            return Json.Obj("set", true, "elementIndex", elementIndex.Value);
        }

        private static object Drag(IDictionary<string, object> p)
        {
            var window = RequireWindow(p);
            EnsureForeground(window);
            int? fromX = Json.Int(p, "fromX"), fromY = Json.Int(p, "fromY");
            int? toX = Json.Int(p, "toX"), toY = Json.Int(p, "toY");
            if (!fromX.HasValue || !fromY.HasValue || !toX.HasValue || !toY.HasValue)
                throw new HelperException("bad_request", "drag requires 'fromX', 'fromY', 'toX' and 'toY'.");

            var shot = ResolveShot(window.Hwnd, Json.Str(p, "screenshotId"));
            int sx, sy, ex, ey;
            ToScreen(window, shot, fromX.Value, fromY.Value, out sx, out sy);
            ToScreen(window, shot, toX.Value, toY.Value, out ex, out ey);
            VerifyPointBelongsTo(window, sx, sy);
            Input.Drag(sx, sy, ex, ey, Json.Str(p, "mouseButton", "left"));
            return Json.Obj("dragged", true, "fromX", sx, "fromY", sy, "toX", ex, "toY", ey);
        }

        private static object PerformSecondaryAction(IDictionary<string, object> p)
        {
            var window = RequireWindow(p);
            int? elementIndex = Json.Int(p, "elementIndex");
            if (!elementIndex.HasValue)
                throw new HelperException("bad_request", "perform_secondary_action requires 'elementIndex'.");
            string action = Json.Str(p, "action");
            if (string.IsNullOrWhiteSpace(action))
                throw new HelperException("bad_request", "perform_secondary_action requires 'action'.");

            var element = SnapshotFor(window.Hwnd).ElementAt(elementIndex.Value);
            Uia.PerformAction(element, action);
            return Json.Obj("performed", action, "elementIndex", elementIndex.Value);
        }

        // ---------------- 共享辅助 ----------------

        private static WindowInfo RequireWindow(IDictionary<string, object> p)
        {
            long? hwnd = Json.Long(p, "hwnd");
            if (!hwnd.HasValue || hwnd.Value == 0)
                throw new HelperException("bad_request", "This method requires 'hwnd' (from list_apps / list_windows).");
            var handle = new IntPtr(hwnd.Value);
            if (!NativeMethods.IsWindow(handle))
                throw new HelperException("window_gone",
                    $"Window {hwnd.Value} no longer exists. Call list_windows and pick a fresh window.");
            return Win32.Describe(handle);
        }

        private static AccessibilitySnapshot SnapshotFor(long hwnd)
        {
            AccessibilitySnapshot snapshot;
            if (!Snapshots.TryGetValue(hwnd, out snapshot) || snapshot.Elements.Count == 0)
            {
                throw new HelperException("stale_element_index",
                    "No accessibility snapshot for this window yet. Call get_window_state first, then use element indexes from its tree.");
            }
            return snapshot;
        }

        /// <summary>校验调用方传来的 screenshotId 是否就是该窗口最近一次截图。</summary>
        private static ShotRecord ResolveShot(long hwnd, string screenshotId)
        {
            var shot = Capture.LastShot(hwnd);
            if (!string.IsNullOrEmpty(screenshotId))
            {
                if (shot == null || shot.Id != screenshotId)
                {
                    throw new HelperException("stale_screenshot",
                        $"screenshotId '{screenshotId}' is not the latest screenshot for this window " +
                        $"(latest: {(shot == null ? "none" : shot.Id)}). " +
                        "Call get_window_state again and use coordinates from the returned screenshot.");
                }
            }
            return shot;
        }

        /// <summary>把截图图像坐标换算成屏幕物理坐标（考虑缩放比例与窗口当前位置）。</summary>
        private static void ToScreen(WindowInfo window, ShotRecord shot, int imageX, int imageY, out int screenX, out int screenY)
        {
            double scale = shot != null && shot.Scale > 0 ? shot.Scale : 1.0;
            RECT bounds = Win32.WindowBounds(new IntPtr(window.Hwnd));
            if (bounds.IsEmpty) bounds = window.Rect;
            screenX = bounds.Left + (int)Math.Round(imageX / scale);
            screenY = bounds.Top + (int)Math.Round(imageY / scale);
        }

        /// <summary>确保目标窗口在前台；UIA/SendInput 的坐标与焦点都依赖这一点。</summary>
        private static void EnsureForeground(WindowInfo window)
        {
            var handle = new IntPtr(window.Hwnd);
            if (NativeMethods.GetForegroundWindow() == handle) return;
            if (!Win32.ActivateWindow(handle))
            {
                throw new HelperException("activation_failed",
                    $"Window {window.Hwnd} could not be activated, so input would land on the wrong window. " +
                    "Close any modal dialog owned by it, or bring it to the foreground manually.");
            }
        }

        /// <summary>
        /// 检查屏幕点确实落在目标窗口上。
        /// 必要性：点击位置可能被别的窗口（如开始菜单、悬浮窗、模态框）遮挡，
        /// 此时点击会打到错误的窗口上，属于最危险的静默失败。
        /// </summary>
        private static void VerifyPointBelongsTo(WindowInfo window, int screenX, int screenY)
        {
            RECT bounds = Win32.WindowBounds(new IntPtr(window.Hwnd));
            if (bounds.IsEmpty) bounds = window.Rect;
            if (screenX < bounds.Left || screenX >= bounds.Right || screenY < bounds.Top || screenY >= bounds.Bottom)
            {
                throw new HelperException("point_outside_window",
                    $"The point ({screenX}, {screenY}) is outside the target window " +
                    $"(window occupies {bounds.Left},{bounds.Top} {bounds.Width}x{bounds.Height}). " +
                    "Use coordinates taken from the latest screenshot of this window.");
            }

            var point = new POINT { X = screenX, Y = screenY };
            IntPtr hit = NativeMethods.WindowFromPoint(point);
            if (hit == IntPtr.Zero)
            {
                throw new HelperException("point_obstructed",
                    $"No window is present at ({screenX}, {screenY}); the target may have moved or closed.");
            }

            IntPtr hitRoot = NativeMethods.GetAncestor(hit, NativeMethods.GA_ROOT);
            IntPtr targetRoot = NativeMethods.GetAncestor(new IntPtr(window.Hwnd), NativeMethods.GA_ROOT);
            if (hitRoot == targetRoot) return;

            uint pid;
            NativeMethods.GetWindowThreadProcessId(hitRoot, out pid);
            string owner = Win32.ProcessName(pid);
            string ownerTitle = Win32.TitleOf(hitRoot);
            throw new HelperException("point_obstructed",
                $"The point ({screenX}, {screenY}) is over '{owner}' (\"{ownerTitle}\"), not over the target window. " +
                "Activate the target window again and retry with coordinates from a fresh screenshot.");
        }

        // ---------------- 自检 ----------------

        /// <summary>不依赖桌面会话的最小自检，供 CI/打包后验证使用。</summary>
        private static int SelfTest()
        {
            int failures = 0;
            failures += Check("chord 解析 Control_L+a", () =>
            {
                // Control_L 解析为左 Ctrl 专用虚拟键（VK_LCONTROL = 0xA2），比通用 VK_CONTROL 更精确。
                var chord = Input.ParseChord("Control_L+a");
                return chord.Keys.Count == 2 && chord.Keys[0] == 0xA2 && chord.Keys[1] == 0x41;
            });
            failures += Check("chord 别名 ctrl+shift+period", () =>
            {
                var chord = Input.ParseChord("ctrl + shift + period");
                return chord.Keys.Count == 3 && chord.Keys[2] == 0xBE;
            });
            failures += Check("chord 自动补 Shift（!）", () =>
            {
                var chord = Input.ParseChord("!");
                return chord.Keys.Count == 2 && chord.Keys[0] == 0x10;
            });
            failures += Check("chord 非法键名报错", () =>
            {
                try { Input.ParseChord("NotAKey"); return false; }
                catch (HelperException) { return true; }
            });
            failures += Check("JSON 往返", () =>
            {
                var parsed = Json.Parse("{\"id\":7,\"method\":\"ping\",\"params\":{\"x\":3}}");
                return Json.Long(parsed, "id") == 7 && Json.Int(Json.Map(parsed, "params"), "x") == 3;
            });
            failures += Check("DPI 感知可用", () =>
            {
                Win32.EnableDpiAwareness();
                return true;
            });

            _output.Flush();
            Console.WriteLine(failures == 0 ? "selftest: OK" : $"selftest: {failures} FAILED");
            return failures == 0 ? 0 : 1;
        }

        private static int Check(string name, Func<bool> test)
        {
            try
            {
                bool ok = test();
                Console.WriteLine((ok ? "  ok   " : "  FAIL ") + name);
                return ok ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  FAIL " + name + " -> " + ex.GetType().Name + ": " + ex.Message);
                return 1;
            }
        }

        // ---------------- 输出 ----------------

        private static void Respond(long id, bool ok, object result)
        {
            var payload = Json.Obj();
            payload["id"] = id;
            payload["ok"] = ok;
            payload["result"] = result;
            WriteLine(Json.Write(payload));
        }

        private static void RespondError(long id, string code, string message)
        {
            var payload = Json.Obj();
            payload["id"] = id;
            payload["ok"] = false;
            payload["error"] = Json.Obj("code", code, "message", message);
            WriteLine(Json.Write(payload));
        }

        private static void WriteLine(string text)
        {
            _output.Write(text);
            _output.Write('\n');
            _output.Flush();
        }
    }
}
