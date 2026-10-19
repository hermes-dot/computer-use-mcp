// 输入层：基于 SendInput 的鼠标与键盘合成。
//
// 设计要点：
//   - 所有输入事件先累积到批次里，最后一次性 SendInput，保证顺序与原子性；
//   - 绝对坐标使用虚拟桌面归一化（0..65535），因此多显示器与负坐标显示器都能正确寻址；
//   - 文本输入走 KEYEVENTF_UNICODE，可直接发送中文等任意 Unicode 字符，不依赖键盘布局；
//   - 按键和弦（如 "Control_L+Shift_L+period"）先整体按下、再逆序抬起，避免修饰键粘连。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace CuHelper
{
    internal static class Input
    {
        // ---------------- 虚拟键码常量 ----------------
        private const ushort VK_BACK = 0x08, VK_TAB = 0x09, VK_RETURN = 0x0D, VK_SHIFT = 0x10, VK_CONTROL = 0x11;
        private const ushort VK_MENU = 0x12, VK_PAUSE = 0x13, VK_CAPITAL = 0x14, VK_ESCAPE = 0x1B, VK_SPACE = 0x20;
        private const ushort VK_PRIOR = 0x21, VK_NEXT = 0x22, VK_END = 0x23, VK_HOME = 0x24, VK_LEFT = 0x25;
        private const ushort VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28, VK_SNAPSHOT = 0x2C, VK_INSERT = 0x2D;
        private const ushort VK_DELETE = 0x2E, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_APPS = 0x5D;
        private const ushort VK_MULTIPLY = 0x6A, VK_ADD = 0x6B, VK_SEPARATOR = 0x6C, VK_SUBTRACT = 0x6D;
        private const ushort VK_DECIMAL = 0x6E, VK_DIVIDE = 0x6F, VK_NUMLOCK = 0x90, VK_SCROLL = 0x91;
        private const ushort VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3;
        private const ushort VK_LMENU = 0xA4, VK_RMENU = 0xA5;

        private static readonly Dictionary<string, ushort> NamedKeys = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
        {
            // 修饰键（左右通用别名）
            { "control", VK_CONTROL }, { "ctrl", VK_CONTROL }, { "control_l", VK_LCONTROL }, { "ctrl_l", VK_LCONTROL },
            { "control_r", VK_RCONTROL }, { "ctrl_r", VK_RCONTROL },
            { "shift", VK_SHIFT }, { "shift_l", VK_LSHIFT }, { "shift_r", VK_RSHIFT },
            { "alt", VK_MENU }, { "alt_l", VK_LMENU }, { "alt_r", VK_RMENU }, { "menu", VK_APPS },
            { "super", VK_LWIN }, { "super_l", VK_LWIN }, { "super_r", VK_RWIN },
            { "meta", VK_LWIN }, { "win", VK_LWIN }, { "windows", VK_LWIN }, { "command", VK_LWIN },
            // 编辑与导航
            { "return", VK_RETURN }, { "enter", VK_RETURN }, { "tab", VK_TAB }, { "escape", VK_ESCAPE },
            { "esc", VK_ESCAPE }, { "space", VK_SPACE }, { "spacebar", VK_SPACE },
            { "backspace", VK_BACK }, { "back_space", VK_BACK }, { "delete", VK_DELETE }, { "del", VK_DELETE },
            { "insert", VK_INSERT }, { "ins", VK_INSERT }, { "home", VK_HOME }, { "end", VK_END },
            { "page_up", VK_PRIOR }, { "pageup", VK_PRIOR }, { "prior", VK_PRIOR },
            { "page_down", VK_NEXT }, { "pagedown", VK_NEXT }, { "next", VK_NEXT },
            { "up", VK_UP }, { "down", VK_DOWN }, { "left", VK_LEFT }, { "right", VK_RIGHT },
            { "caps_lock", VK_CAPITAL }, { "capslock", VK_CAPITAL }, { "num_lock", VK_NUMLOCK },
            { "scroll_lock", VK_SCROLL }, { "print", VK_SNAPSHOT }, { "printscreen", VK_SNAPSHOT },
            { "pause", VK_PAUSE }, { "apps", VK_APPS },
            // 小键盘
            { "kp_0", 0x60 }, { "kp_1", 0x61 }, { "kp_2", 0x62 }, { "kp_3", 0x63 }, { "kp_4", 0x64 },
            { "kp_5", 0x65 }, { "kp_6", 0x66 }, { "kp_7", 0x67 }, { "kp_8", 0x68 }, { "kp_9", 0x69 },
            { "numpad_0", 0x60 }, { "numpad_1", 0x61 }, { "numpad_2", 0x62 }, { "numpad_3", 0x63 },
            { "numpad_4", 0x64 }, { "numpad_5", 0x65 }, { "numpad_6", 0x66 }, { "numpad_7", 0x67 },
            { "numpad_8", 0x68 }, { "numpad_9", 0x69 },
            { "kp_enter", VK_RETURN }, { "kp_add", VK_ADD }, { "kp_subtract", VK_SUBTRACT },
            { "kp_multiply", VK_MULTIPLY }, { "kp_divide", VK_DIVIDE }, { "kp_decimal", VK_DECIMAL },
            // X11 keysym 风格符号名
            { "period", 0xBE }, { "greater", 0xBE }, { "comma", 0xBC }, { "less", 0xBC },
            { "slash", 0xBF }, { "question", 0xBF }, { "semicolon", 0xBA }, { "colon", 0xBA },
            { "apostrophe", 0xDE }, { "quotedbl", 0xDE }, { "bracketleft", 0xDB }, { "braceleft", 0xDB },
            { "bracketright", 0xDD }, { "braceright", 0xDD }, { "backslash", 0xDC }, { "bar", 0xDC },
            { "minus", 0xBD }, { "underscore", 0xBD }, { "equal", 0xBB }, { "plus", 0xBB },
            { "grave", 0xC0 }, { "asciitilde", 0xC0 }, { "exclam", 0x31 }, { "at", 0x32 },
            { "numbersign", 0x33 }, { "dollar", 0x34 }, { "percent", 0x35 }, { "asciicircum", 0x36 },
            { "ampersand", 0x37 }, { "asterisk", 0x38 }, { "parenleft", 0x39 }, { "parenright", 0x30 },
        };

        /// <summary>按键和弦：`Control_L+Shift_L+period` 这类写法。</summary>
        public sealed class KeyChord
        {
            public readonly List<ushort> Keys = new List<ushort>();
            public readonly List<string> Names = new List<string>();

            public override string ToString() => string.Join("+", Names);
        }

        /// <summary>解析 `+` 分隔的按键和弦；空白被忽略，别名与 keysym 名都接受。</summary>
        public static KeyChord ParseChord(string chord)
        {
            if (string.IsNullOrWhiteSpace(chord))
                throw new HelperException("invalid_key", "key must not be empty.");

            var result = new KeyChord();
            foreach (string rawPart in chord.Split('+'))
            {
                string part = rawPart.Trim();
                if (part.Length == 0) continue;

                ushort vk;
                bool needsShift = false;
                if (!TryResolveKey(part, out vk, out needsShift))
                    throw new HelperException("invalid_key", $"Unrecognized key name: '{part}' in chord '{chord}'.");

                // 需要 Shift 的符号（如 "!"）自动补上修饰键，避免调用方还要自己拼 "Shift_L+1"。
                if (needsShift && !result.Keys.Contains(VK_SHIFT))
                {
                    result.Keys.Add(VK_SHIFT);
                    result.Names.Add("Shift_L");
                }
                result.Keys.Add(vk);
                result.Names.Add(part);
            }

            if (result.Keys.Count == 0)
                throw new HelperException("invalid_key", $"Chord '{chord}' contains no usable keys.");
            return result;
        }

        private static bool TryResolveKey(string part, out ushort vk, out bool needsShift)
        {
            vk = 0;
            needsShift = false;

            if (NamedKeys.TryGetValue(part, out vk)) return true;

            // F1..F24
            if (part.Length >= 2 && (part[0] == 'F' || part[0] == 'f'))
            {
                int number;
                if (int.TryParse(part.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) &&
                    number >= 1 && number <= 24)
                {
                    vk = (ushort)(0x70 + number - 1);
                    return true;
                }
            }

            // 单字符：交给当前键盘布局解析（自动判断是否需要 Shift / Ctrl / Alt）。
            if (part.Length == 1)
            {
                short scan = NativeMethods.VkKeyScanW(part[0]);
                if (scan != -1)
                {
                    vk = (ushort)(scan & 0xFF);
                    byte state = (byte)((scan >> 8) & 0xFF);
                    needsShift = (state & 1) != 0;
                    return true;
                }
                // 布局无法映射（例如中文标点）：退化为 VK 未知，交由 Unicode 路径处理。
                return false;
            }

            return false;
        }

        // ---------------- 事件批次 ----------------

        private sealed class Batch
        {
            private readonly List<INPUT> _events = new List<INPUT>();

            public void Mouse(uint flags, int dx = 0, int dy = 0, uint data = 0)
            {
                _events.Add(new INPUT
                {
                    type = NativeMethods.INPUT_MOUSE,
                    u = new INPUTUNION
                    {
                        mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags, time = 0, dwExtraInfo = IntPtr.Zero }
                    }
                });
            }

            public void Key(ushort vk, bool up, bool unicode = false, ushort scan = 0)
            {
                uint flags = up ? NativeMethods.KEYEVENTF_KEYUP : 0;
                if (unicode) flags |= NativeMethods.KEYEVENTF_UNICODE;
                _events.Add(new INPUT
                {
                    type = NativeMethods.INPUT_KEYBOARD,
                    u = new INPUTUNION
                    {
                        ki = new KEYBDINPUT { wVk = unicode ? (ushort)0 : vk, wScan = unicode ? scan : (ushort)0, dwFlags = flags, time = 0, dwExtraInfo = IntPtr.Zero }
                    }
                });
            }

            public void Flush()
            {
                if (_events.Count == 0) return;
                var array = _events.ToArray();
                uint sent = NativeMethods.SendInput((uint)array.Length, array, Marshal.SizeOf(typeof(INPUT)));
                _events.Clear();
                if (sent != array.Length)
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new HelperException("input_blocked",
                        $"SendInput delivered {sent}/{array.Length} events (Win32 error {error}). " +
                        "Another process may be blocking input (e.g. an elevated window).");
                }
            }
        }

        /// <summary>把屏幕物理坐标换算成 SendInput 需要的虚拟桌面绝对坐标（0..65535）。</summary>
        private static void Absolute(int x, int y, out int normalizedX, out int normalizedY)
        {
            int vx = NativeMethods.GetSystemMetrics(76), vy = NativeMethods.GetSystemMetrics(77);
            int vw = Math.Max(1, NativeMethods.GetSystemMetrics(78)), vh = Math.Max(1, NativeMethods.GetSystemMetrics(79));
            long nx = (long)(x - vx) * 65535 / Math.Max(1, vw - 1);
            long ny = (long)(y - vy) * 65535 / Math.Max(1, vh - 1);
            normalizedX = (int)Math.Max(0, Math.Min(65535, nx));
            normalizedY = (int)Math.Max(0, Math.Min(65535, ny));
        }

        private static uint ButtonFlags(string button, bool down)
        {
            switch ((button ?? "left").Trim().ToLowerInvariant())
            {
                case "right": case "r": return down ? NativeMethods.MOUSEEVENTF_RIGHTDOWN : NativeMethods.MOUSEEVENTF_RIGHTUP;
                case "middle": case "m": return down ? NativeMethods.MOUSEEVENTF_MIDDLEDOWN : NativeMethods.MOUSEEVENTF_MIDDLEUP;
                case "left": case "l": return down ? NativeMethods.MOUSEEVENTF_LEFTDOWN : NativeMethods.MOUSEEVENTF_LEFTUP;
                default:
                    throw new HelperException("invalid_button", $"Unknown mouse button '{button}' (expected left|right|middle).");
            }
        }

        /// <summary>移动指针到屏幕物理坐标。</summary>
        public static void MoveTo(int x, int y, KeyChord held = null)
        {
            var batch = new Batch();
            int nx, ny;
            Absolute(x, y, out nx, out ny);
            if (held != null) foreach (ushort vk in held.Keys) batch.Key(vk, false);
            batch.Mouse(NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE, nx, ny);
            if (held != null) for (int i = held.Keys.Count - 1; i >= 0; i--) batch.Key(held.Keys[i], true);
            batch.Flush();
        }

        /// <summary>在屏幕物理坐标处点击。</summary>
        public static void Click(int x, int y, string button, int clickCount, int durationMs)
        {
            var batch = new Batch();
            int nx, ny;
            Absolute(x, y, out nx, out ny);
            batch.Mouse(NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE, nx, ny);

            uint down = ButtonFlags(button, true), up = ButtonFlags(button, false);
            for (int i = 0; i < Math.Max(1, clickCount); i++)
            {
                batch.Mouse(down);
                if (durationMs > 0) { batch.Flush(); System.Threading.Thread.Sleep(durationMs); batch.Mouse(up); }
                else { batch.Mouse(up); }
            }
            batch.Flush();
        }

        /// <summary>拖拽：按下 -> 分步移动 -> 抬起。分步移动能让需要中间事件的控件（滑块、画布）正确响应。</summary>
        public static void Drag(int fromX, int fromY, int toX, int toY, string button, int steps = 12)
        {
            int nx, ny;
            var batch = new Batch();
            Absolute(fromX, fromY, out nx, out ny);
            batch.Mouse(NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE, nx, ny);
            batch.Mouse(ButtonFlags(button, true));
            batch.Flush();

            for (int i = 1; i <= steps; i++)
            {
                double t = (double)i / steps;
                int x = (int)Math.Round(fromX + (toX - fromX) * t);
                int y = (int)Math.Round(fromY + (toY - fromY) * t);
                MoveTo(x, y);
                System.Threading.Thread.Sleep(12);
            }

            var tail = new Batch();
            Absolute(toX, toY, out nx, out ny);
            tail.Mouse(NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE, nx, ny);
            tail.Mouse(ButtonFlags(button, false));
            tail.Flush();
        }

        /// <summary>用 Unicode 事件输入文本（支持任意字符，不依赖键盘布局）。</summary>
        public static void TypeText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var batch = new Batch();
            foreach (char ch in text)
            {
                if (ch == '\n') { batch.Key(VK_RETURN, false); batch.Key(VK_RETURN, true); continue; }
                if (ch == '\r') continue;
                if (ch == '\t') { batch.Key(VK_TAB, false); batch.Key(VK_TAB, true); continue; }
                // 代理对（emoji 等）需要按 UTF-16 码元逐个发送，SendInput 会自行重组。
                batch.Key(0, false, unicode: true, scan: ch);
                batch.Key(0, true, unicode: true, scan: ch);
            }
            batch.Flush();
        }

        /// <summary>按下并释放一个按键和弦。</summary>
        public static void PressChord(KeyChord chord, int durationMs = 0)
        {
            var batch = new Batch();
            foreach (ushort vk in chord.Keys) batch.Key(vk, false);
            if (durationMs > 0)
            {
                batch.Flush();
                System.Threading.Thread.Sleep(durationMs);
                var tail = new Batch();
                for (int i = chord.Keys.Count - 1; i >= 0; i--) tail.Key(chord.Keys[i], true);
                tail.Flush();
                return;
            }
            for (int i = chord.Keys.Count - 1; i >= 0; i--) batch.Key(chord.Keys[i], true);
            batch.Flush();
        }

        /// <summary>滚轮滚动。scrollX/scrollY 为像素意图，转换为滚轮刻度（一行约 100px）。</summary>
        public static void Scroll(int x, int y, double scrollX, double scrollY)
        {
            const double PixelsPerNotch = 100.0;
            var batch = new Batch();
            int nx, ny;
            Absolute(x, y, out nx, out ny);
            batch.Mouse(NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE, nx, ny);

            if (Math.Abs(scrollY) > 0.01)
            {
                int notches = (int)Math.Round(scrollY / PixelsPerNotch);
                if (notches == 0) notches = scrollY > 0 ? 1 : -1;
                // 屏幕坐标向下为正，而滚轮正 delta 表示向上滚动，故取负。
                uint data = unchecked((uint)(-notches * NativeMethods.WHEEL_DELTA));
                batch.Mouse(NativeMethods.MOUSEEVENTF_WHEEL, data: data);
            }
            if (Math.Abs(scrollX) > 0.01)
            {
                int notches = (int)Math.Round(scrollX / PixelsPerNotch);
                if (notches == 0) notches = scrollX > 0 ? 1 : -1;
                uint data = unchecked((uint)(notches * NativeMethods.WHEEL_DELTA));
                batch.Mouse(NativeMethods.MOUSEEVENTF_HWHEEL, data: data);
            }
            batch.Flush();
        }
    }
}
