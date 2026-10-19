// 窗口层：DPI 初始化、顶层窗口枚举、进程身份、窗口激活。
//
// 设计要点（对齐 Codex window2 语义）：
//   - app 标识使用进程可执行文件完整路径（小写归一化），稳定且可跨会话复用；
//   - 窗口矩形一律使用 DWM 扩展边框（物理像素），与 UIA 的 BoundingRectangle 同一坐标系；
//   - 截图坐标 = 窗口矩形内的物理像素偏移，因此 helper 必须声明 Per-Monitor-V2 DPI 感知，
//     否则系统会对本进程做 DPI 虚拟化，坐标与截图尺寸全部错位。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace CuHelper
{
    internal sealed class WindowInfo
    {
        public long Hwnd;
        public string Title = "";
        public string ClassName = "";
        public uint Pid;
        public string AppPath = "";
        public string AppName = "";
        public RECT Rect;
        public bool IsForeground;
        public bool IsMinimized;
        public bool IsCloaked;
        public bool IsEnabled = true;
        public int Dpi = 96;

        /// <summary>app 标识：进程完整路径优先，取不到时退化为进程名。</summary>
        public string AppId => string.IsNullOrEmpty(AppPath) ? AppName : AppPath;

        public Dictionary<string, object> ToJson(bool includeDetail)
        {
            var o = Json.Obj();
            o["id"] = Hwnd;
            o["app"] = AppId;
            o["title"] = Title;
            if (includeDetail)
            {
                o["className"] = ClassName;
                o["processName"] = AppName;
                o["processPath"] = AppPath;
                o["pid"] = (long)Pid;
                o["focused"] = IsForeground;
                o["minimized"] = IsMinimized;
                o["enabled"] = IsEnabled;
                o["dpi"] = Dpi;
                o["x"] = Rect.Left;
                o["y"] = Rect.Top;
                o["width"] = Rect.Width;
                o["height"] = Rect.Height;
            }
            return o;
        }
    }

    internal static class Win32
    {
        // 系统外壳窗口：不是用户可操作的应用窗口，需要从列表中排除。
        private static readonly HashSet<string> ShellClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
            "Windows.UI.Core.CoreWindow", "ApplicationManager_DesktopShellWindow",
            "ForegroundStaging", "MultitaskingViewFrame", "XamlExplorerHostIslandWindow",
            "Windows.Internal.Shell.TabProxyWindow", "NarratorHelperWindow",
            "TaskListThumbnailWnd", "Shell_InputSwitchTopLevelWindow",
            "Windows.UI.Input.InputSite.WindowClass", "EdgeUiInputTopWndClass",
            "SysShadow", "Button", "tooltips_class32", "MSCTFIME UI", "IME",
        };

        // 桌面小挂件/无意义小窗口的尺寸阈值（像素）
        private const int MinWindowSize = 40;

        /// <summary>声明 Per-Monitor-V2 DPI 感知；必须在任何窗口/坐标 API 之前调用。</summary>
        public static void EnableDpiAwareness()
        {
            // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 == -4
            if (NativeMethods.SetProcessDpiAwarenessContext(new IntPtr(-4))) return;
            try
            {
                if (NativeMethods.SetProcessDpiAwareness(2) == 0) return; // PROCESS_PER_MONITOR_DPI_AWARE
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            NativeMethods.SetProcessDPIAware();
        }

        public static string ProcessName(uint pid)
        {
            try { return Process.GetProcessById((int)pid).ProcessName; }
            catch { return ""; }
        }

        /// <summary>取进程完整路径；对受保护进程可能失败，此时返回空串。</summary>
        public static string ProcessPath(uint pid)
        {
            IntPtr h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return "";
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                if (!NativeMethods.QueryFullProcessImageNameW(h, 0, sb, ref size)) return "";
                string path = sb.ToString();
                try { path = Path.GetFullPath(path); } catch { /* 保持原样 */ }
                return path;
            }
            finally { NativeMethods.CloseHandle(h); }
        }

        /// <summary>DWM 扩展边框优先：GetWindowRect 会把不可见的调整边框算进去，导致截图偏移。</summary>
        public static RECT WindowBounds(IntPtr hwnd)
        {
            RECT r;
            if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out r,
                    Marshal.SizeOf(typeof(RECT))) == 0 && !r.IsEmpty)
                return r;
            NativeMethods.GetWindowRect(hwnd, out r);
            return r;
        }

        public static bool IsCloaked(IntPtr hwnd)
        {
            int cloaked;
            if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out cloaked, sizeof(int)) != 0) return false;
            return cloaked != 0;
        }

        public static WindowInfo Describe(IntPtr hwnd)
        {
            uint pid;
            NativeMethods.GetWindowThreadProcessId(hwnd, out pid);
            var info = new WindowInfo
            {
                Hwnd = hwnd.ToInt64(),
                Pid = pid,
                ClassName = ClassNameOf(hwnd),
                Title = TitleOf(hwnd),
                AppName = ProcessName(pid),
                AppPath = ProcessPath(pid),
                Rect = WindowBounds(hwnd),
                IsForeground = NativeMethods.GetForegroundWindow() == hwnd,
                IsMinimized = NativeMethods.IsIconic(hwnd),
                IsCloaked = IsCloaked(hwnd),
                IsEnabled = NativeMethods.IsWindowEnabled(hwnd),
            };
            uint dpi = NativeMethods.GetDpiForWindow(hwnd);
            info.Dpi = dpi == 0 ? 96 : (int)dpi;
            return info;
        }

        public static string TitleOf(IntPtr hwnd)
        {
            int len = NativeMethods.GetWindowTextLengthW(hwnd);
            var sb = new StringBuilder(len + 2);
            NativeMethods.GetWindowTextW(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public static string ClassNameOf(IntPtr hwnd)
        {
            var sb = new StringBuilder(256);
            NativeMethods.GetClassNameW(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        /// <summary>枚举可操作的顶层应用窗口（已按外壳类、cloaked、尺寸过滤）。</summary>
        public static List<WindowInfo> ListWindows()
        {
            var result = new List<WindowInfo>();
            NativeMethods.EnumWindows((hwnd, _) =>
            {
                if (IsUserFacingWindow(hwnd)) result.Add(Describe(hwnd));
                return true;
            }, IntPtr.Zero);
            result.Sort((a, b) => string.Compare(a.AppName, b.AppName, StringComparison.OrdinalIgnoreCase));
            return result;
        }

        public static bool IsUserFacingWindow(IntPtr hwnd)
        {
            if (!NativeMethods.IsWindowVisible(hwnd)) return false;
            if (NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT) != hwnd) return false;
            if (IsCloaked(hwnd)) return false;

            string cls = ClassNameOf(hwnd);
            if (ShellClasses.Contains(cls)) return false;
            if (cls.StartsWith("Windows.UI.Core", StringComparison.OrdinalIgnoreCase)) return false;

            // 无标题窗口通常是不可见的宿主窗口，不作为操作目标。
            if (NativeMethods.GetWindowTextLengthW(hwnd) == 0) return false;

            // 有 owner 的通常是对话框/弹出层：保留（Codex 也把 dialog 视为可操作窗口），
            // 但要求它有实际尺寸。
            RECT r = WindowBounds(hwnd);
            if (r.IsEmpty) return false;
            if (r.Width < MinWindowSize || r.Height < MinWindowSize) return false;

            return true;
        }

        /// <summary>把窗口带到前台并交出键盘焦点。</summary>
        /// <remarks>
        /// SetForegroundWindow 受"前台锁定"限制：当前台窗口属于用户正在交互的进程时，
        /// 其他进程直接调用往往只闪动任务栏。这里按可靠性递增尝试三种手段：
        ///   1) AttachThreadInput 把本线程挂到前台线程与目标线程的输入队列上；
        ///   2) 先模拟一次 ALT 击键 —— 系统检测到用户输入后会解除前台锁定；
        ///   3) 临时置顶再取消置顶，强制窗口浮到最前。
        /// 每次尝试后都轮询确认，避免"看起来调用了但没生效"的静默失败。
        /// </remarks>
        public static bool ActivateWindow(IntPtr hwnd)
        {
            if (!NativeMethods.IsWindow(hwnd)) return false;
            if (NativeMethods.GetForegroundWindow() == hwnd) return true;

            if (NativeMethods.IsIconic(hwnd))
            {
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
                System.Threading.Thread.Sleep(60);
            }

            // 手段 1：AttachThreadInput
            AttachAndActivate(hwnd);
            if (WaitForForeground(hwnd, 8)) return true;

            // 手段 2：模拟 ALT 击键解除前台锁定后重试
            try
            {
                Input.TapAltKey();
            }
            catch (HelperException)
            {
                // 输入被阻塞（例如前台是提权窗口）时继续尝试下一种手段
            }
            System.Threading.Thread.Sleep(40);
            AttachAndActivate(hwnd);
            if (WaitForForeground(hwnd, 10)) return true;

            // 手段 3：临时置顶
            const int HWND_TOPMOST = -1, HWND_NOTOPMOST = -2;
            uint flags = NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_SHOWWINDOW;
            NativeMethods.SetWindowPos(hwnd, new IntPtr(HWND_TOPMOST), 0, 0, 0, 0, flags);
            NativeMethods.SetWindowPos(hwnd, new IntPtr(HWND_NOTOPMOST), 0, 0, 0, 0, flags);
            AttachAndActivate(hwnd);
            return WaitForForeground(hwnd, 10);
        }

        private static void AttachAndActivate(IntPtr hwnd)
        {
            IntPtr foreground = NativeMethods.GetForegroundWindow();
            uint targetThread = NativeMethods.GetWindowThreadProcessId(hwnd, out _);
            uint foregroundThread = foreground == IntPtr.Zero
                ? 0
                : NativeMethods.GetWindowThreadProcessId(foreground, out _);
            uint currentThread = NativeMethods.GetCurrentThreadId();

            bool attachedTarget = false, attachedForeground = false;
            try
            {
                if (targetThread != 0 && targetThread != currentThread)
                    attachedTarget = NativeMethods.AttachThreadInput(currentThread, targetThread, true);
                if (foregroundThread != 0 && foregroundThread != currentThread && foregroundThread != targetThread)
                    attachedForeground = NativeMethods.AttachThreadInput(currentThread, foregroundThread, true);

                NativeMethods.BringWindowToTop(hwnd);
                NativeMethods.SetForegroundWindow(hwnd);
                NativeMethods.SetActiveWindow(hwnd);
                NativeMethods.SetFocus(hwnd);
            }
            finally
            {
                // 必须成对 detach，否则会污染相关线程的输入队列。
                if (attachedForeground) NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
                if (attachedTarget) NativeMethods.AttachThreadInput(currentThread, targetThread, false);
            }
        }

        /// <summary>前台切换是异步的，轮询等待它落定。</summary>
        private static bool WaitForForeground(IntPtr hwnd, int attempts)
        {
            for (int i = 0; i < attempts; i++)
            {
                if (NativeMethods.GetForegroundWindow() == hwnd) return true;
                System.Threading.Thread.Sleep(30);
            }
            return NativeMethods.GetForegroundWindow() == hwnd;
        }
    }
}
