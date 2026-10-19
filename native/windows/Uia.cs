// UIA（UI Automation）无障碍树层。
//
// 职责：
//   1. 把窗口的 UIA 树遍历成"带索引的文本树"，供模型阅读（这是 computer use 能可靠点击的前提）；
//   2. 缓存 index -> AutomationElement 的映射，让后续 click/set_value 等动作可以按索引寻址；
//   3. 把元素矩形换算成窗口内坐标，供 SendInput 使用。
//
// 索引语义严格对齐 Codex：索引只在产生它的那次 get_window_state 之后有效，
// 任何一次新的 state 捕获都会重建缓存（因为 UI 结构可能已经变化）。
using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace CuHelper
{
    /// <summary>一次无障碍快照：格式化文本 + 索引到元素的映射。</summary>
    internal sealed class AccessibilitySnapshot
    {
        public long Hwnd;
        public string Tree = "";
        public string FocusedElement = "";
        public string DocumentText = "";
        public string SelectedText = "";
        public List<string> SelectedElements = new List<string>();
        public int NodeCount;
        public bool Truncated;
        public readonly List<AutomationElement> Elements = new List<AutomationElement>();

        public AutomationElement ElementAt(int index)
        {
            if (index < 0 || index >= Elements.Count)
            {
                throw new HelperException(
                    "stale_element_index",
                    $"Element index {index} is not valid for the latest window state " +
                    $"(valid range: 0..{Elements.Count - 1}). Call get_window_state again and use a fresh index.");
            }
            return Elements[index];
        }
    }

    internal static class Uia
    {
        // 节点上限：超大窗口（浏览器、IDE）的 UIA 树可能有上万个节点，
        // 既拖慢响应也会撑爆模型上下文，因此截断并显式标记。
        private const int MaxNodes = 1200;
        private const int MaxDepth = 60;
        private const int MaxNameLength = 160;

        /// <summary>遍历窗口的 UIA 树，生成文本并缓存元素索引。</summary>
        public static AccessibilitySnapshot Capture(IntPtr hwnd, bool includeText)
        {
            AutomationElement root;
            try
            {
                root = AutomationElement.FromHandle(hwnd);
            }
            catch (Exception ex)
            {
                throw new HelperException("uia_unavailable", "Cannot attach to window via UI Automation: " + ex.Message);
            }
            if (root == null)
                throw new HelperException("uia_unavailable", "Window has no UI Automation element (it may have closed).");

            var snapshot = new AccessibilitySnapshot { Hwnd = hwnd.ToInt64() };
            var sb = new StringBuilder(8192);
            var walker = TreeWalker.ControlViewWalker;
            Walk(root, walker, 0, sb, snapshot);

            snapshot.Tree = sb.ToString();
            if (snapshot.Truncated)
                snapshot.Tree += $"\n... (truncated at {MaxNodes} elements; use a narrower target window)\n";

            if (includeText) CollectText(root, snapshot);
            return snapshot;
        }

        private static void Walk(AutomationElement element, TreeWalker walker, int depth, StringBuilder sb,
            AccessibilitySnapshot snapshot)
        {
            if (snapshot.NodeCount >= MaxNodes)
            {
                snapshot.Truncated = true;
                return;
            }
            if (depth > MaxDepth) return;

            AutomationElement child;
            try
            {
                child = walker.GetFirstChild(element);
            }
            catch (ElementNotAvailableException) { return; }

            while (child != null)
            {
                if (snapshot.NodeCount >= MaxNodes) { snapshot.Truncated = true; return; }

                AutomationElement next;
                try
                {
                    AppendLine(child, depth, sb, snapshot);
                    next = walker.GetNextSibling(child);
                    Walk(child, walker, depth + 1, sb, snapshot);
                }
                catch (ElementNotAvailableException) { return; }

                child = next;
            }
        }

        /// <summary>把单个元素渲染成一行：`<tab 缩进><index> <role> "name" <value> <状态标记>`。</summary>
        private static void AppendLine(AutomationElement element, int depth, StringBuilder sb, AccessibilitySnapshot snapshot)
        {
            AutomationElement.AutomationElementInformation info;
            try
            {
                info = element.Current;
            }
            catch (ElementNotAvailableException) { return; }

            // 离屏元素对用户不可见，点击它们也没有意义，直接跳过以压缩树体积。
            if (info.IsOffscreen) return;

            int index = snapshot.Elements.Count;
            snapshot.Elements.Add(element);
            snapshot.NodeCount++;

            sb.Append('\t', depth);
            sb.Append(index).Append(' ');
            sb.Append(RoleName(info.ControlType));

            string name = Clean(info.Name);
            if (name.Length > 0) sb.Append(" \"").Append(name).Append('"');

            string value = ReadValue(element);
            if (value.Length > 0) sb.Append(" value=\"").Append(Clean(value)).Append('"');

            // 状态标记：只标注对决策有用的信息，避免噪音。
            if (!info.IsEnabled) sb.Append(" disabled");
            if (info.IsKeyboardFocusable) sb.Append(" focusable");
            if (info.HasKeyboardFocus) sb.Append(" FOCUSED");
            if (info.IsRequiredForForm) sb.Append(" required");

            var actions = SupportedActions(element);
            if (actions.Count > 0) sb.Append(" actions=[").Append(string.Join(", ", actions)).Append(']');

            sb.AppendLine();
        }

        private static string RoleName(ControlType type)
        {
            if (type == null) return "unknown";
            string name = type.ProgrammaticName;
            const string prefix = "ControlType.";
            return name.StartsWith(prefix, StringComparison.Ordinal) ? name.Substring(prefix.Length) : name;
        }

        private static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                if (c == '\r' || c == '\n' || c == '\t') sb.Append(' ');
                else if (char.IsControl(c)) continue;
                else sb.Append(c);
            }
            string result = sb.ToString().Trim();
            // 折叠连续空格，避免树行被长空白撑开
            while (result.Contains("  ")) result = result.Replace("  ", " ");
            return result.Length > MaxNameLength ? result.Substring(0, MaxNameLength) + "…" : result;
        }

        /// <summary>读取元素当前值（ValuePattern / TextPattern / RangeValuePattern 任一可用者）。</summary>
        public static string ReadValue(AutomationElement element)
        {
            object value;
            try
            {
                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out value))
                    return ((ValuePattern)value).Current.Value ?? "";
            }
            catch (ElementNotAvailableException) { return ""; }
            catch (InvalidOperationException) { }

            try
            {
                if (element.TryGetCurrentPattern(RangeValuePattern.Pattern, out value))
                    return ((RangeValuePattern)value).Current.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (ElementNotAvailableException) { return ""; }
            catch (InvalidOperationException) { }

            return "";
        }

        /// <summary>列出元素支持的次要动作名（供 perform_secondary_action 使用）。</summary>
        public static List<string> SupportedActions(AutomationElement element)
        {
            var actions = new List<string>();
            try
            {
                if (!element.Current.IsEnabled) return actions;

                object pattern;
                if (element.TryGetCurrentPattern(InvokePattern.Pattern, out pattern)) actions.Add("Invoke");
                if (element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out pattern))
                {
                    actions.Add("Expand");
                    actions.Add("Collapse");
                }
                if (element.TryGetCurrentPattern(ScrollPattern.Pattern, out pattern))
                {
                    actions.Add("Scroll Up");
                    actions.Add("Scroll Down");
                    actions.Add("Scroll Left");
                    actions.Add("Scroll Right");
                }
                if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out pattern)) actions.Add("Select");
                if (element.TryGetCurrentPattern(TogglePattern.Pattern, out pattern)) actions.Add("Toggle");
                if (element.TryGetCurrentPattern(WindowPattern.Pattern, out pattern)) actions.Add("Raise");
                if (element.TryGetCurrentPattern(TransformPattern.Pattern, out pattern)) actions.Add("Move");
                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out pattern)) actions.Add("SetValue");
                if (element.TryGetCurrentPattern(TextPattern.Pattern, out pattern)) actions.Add("SelectText");
            }
            catch (ElementNotAvailableException) { }
            catch (InvalidOperationException) { }
            return actions;
        }

        /// <summary>补充文本信息：焦点元素行、文档正文、选中文本。</summary>
        private static void CollectText(AutomationElement root, AccessibilitySnapshot snapshot)
        {
            try
            {
                AutomationElement focused = AutomationElement.FocusedElement;
                if (focused != null)
                {
                    string name = Clean(focused.Current.Name);
                    string value = ReadValue(focused);
                    string role = RoleName(focused.Current.ControlType);
                    snapshot.FocusedElement = value.Length > 0
                        ? $"{role} \"{name}\" value=\"{value}\""
                        : $"{role} \"{name}\"";
                }
            }
            catch (ElementNotAvailableException) { }

            try
            {
                object pattern;
                if (root.TryGetCurrentPattern(TextPattern.Pattern, out pattern))
                {
                    string text = ((TextPattern)pattern).DocumentRange.GetText(-1);
                    snapshot.DocumentText = Truncate(text, 20000);
                }
            }
            catch (ElementNotAvailableException) { }
            catch (InvalidOperationException) { }

            try
            {
                object pattern;
                if (root.TryGetCurrentPattern(TextPattern.Pattern, out pattern))
                {
                    var ranges = ((TextPattern)pattern).GetSelection();
                    var parts = new List<string>();
                    foreach (TextPatternRange range in ranges)
                    {
                        string t = range.GetText(-1);
                        if (!string.IsNullOrWhiteSpace(t)) parts.Add(t);
                    }
                    snapshot.SelectedText = Truncate(string.Join("\n", parts), 4000);
                }
            }
            catch (ElementNotAvailableException) { }
            catch (InvalidOperationException) { }
        }

        private static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Length <= max ? text : text.Substring(0, max) + "…";
        }

        /// <summary>元素在屏幕上的矩形（物理像素，与截图同一坐标系）。</summary>
        public static System.Windows.Rect ElementBounds(AutomationElement element)
        {
            try { return element.Current.BoundingRectangle; }
            catch (ElementNotAvailableException) { return System.Windows.Rect.Empty; }
        }

        /// <summary>执行次要动作：把动作名映射到对应的 UIA Pattern。</summary>
        public static void PerformAction(AutomationElement element, string action)
        {
            object pattern;
            string key = (action ?? "").Trim();
            string lower = key.ToLowerInvariant();

            try
            {
                switch (lower)
                {
                    case "invoke":
                    case "press":
                    case "click":
                        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out pattern))
                        {
                            ((InvokePattern)pattern).Invoke();
                            return;
                        }
                        break;

                    case "expand":
                        if (element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out pattern))
                        {
                            ((ExpandCollapsePattern)pattern).Expand();
                            return;
                        }
                        break;

                    case "collapse":
                        if (element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out pattern))
                        {
                            ((ExpandCollapsePattern)pattern).Collapse();
                            return;
                        }
                        break;

                    case "select":
                        if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out pattern))
                        {
                            ((SelectionItemPattern)pattern).Select();
                            return;
                        }
                        break;

                    case "toggle":
                        if (element.TryGetCurrentPattern(TogglePattern.Pattern, out pattern))
                        {
                            ((TogglePattern)pattern).Toggle();
                            return;
                        }
                        break;

                    case "raise":
                        if (element.TryGetCurrentPattern(WindowPattern.Pattern, out pattern))
                        {
                            ((WindowPattern)pattern).SetWindowVisualState(WindowVisualState.Normal);
                            return;
                        }
                        break;

                    case "scroll up":
                    case "scroll down":
                    case "scroll left":
                    case "scroll right":
                        if (element.TryGetCurrentPattern(ScrollPattern.Pattern, out pattern))
                        {
                            var scroll = (ScrollPattern)pattern;
                            var direction = lower.Substring("scroll ".Length);
                            switch (direction)
                            {
                                case "up": scroll.Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeDecrement); return;
                                case "down": scroll.Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeIncrement); return;
                                case "left": scroll.Scroll(ScrollAmount.LargeDecrement, ScrollAmount.NoAmount); return;
                                case "right": scroll.Scroll(ScrollAmount.LargeIncrement, ScrollAmount.NoAmount); return;
                            }
                        }
                        break;
                }
            }
            catch (ElementNotAvailableException)
            {
                throw new HelperException("stale_element", "The element disappeared before the action could run; refresh window state.");
            }
            catch (InvalidOperationException ex)
            {
                throw new HelperException("action_unavailable", $"Action '{action}' could not be performed: {ex.Message}");
            }

            throw new HelperException(
                "action_unavailable",
                $"Element does not support action '{action}'. Supported actions: {string.Join(", ", SupportedActions(element))}");
        }

        /// <summary>用 ValuePattern 直接改写元素值（比逐字符输入更快更稳）。</summary>
        public static void SetValue(AutomationElement element, string value)
        {
            object pattern;
            try
            {
                if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out pattern))
                    throw new HelperException("action_unavailable",
                        "Element is not editable via ValuePattern; use type_text after focusing it instead.");
                var valuePattern = (ValuePattern)pattern;
                if (valuePattern.Current.IsReadOnly)
                    throw new HelperException("action_unavailable", "Element is read-only.");
                valuePattern.SetValue(value ?? "");
            }
            catch (ElementNotAvailableException)
            {
                throw new HelperException("stale_element", "The element disappeared before set_value could run; refresh window state.");
            }
        }

        /// <summary>聚焦元素（点击前的准备，或 set_value 前的定位）。</summary>
        public static bool TryFocus(AutomationElement element)
        {
            try
            {
                element.SetFocus();
                return true;
            }
            catch (ElementNotAvailableException) { return false; }
            catch (InvalidOperationException) { return false; }
        }
    }
}
