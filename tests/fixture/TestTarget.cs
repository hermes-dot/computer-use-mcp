// 端到端测试靶程序：一个已知结构的 WinForms 窗口。
//
// 为什么需要它：验证 computer use 必须有一个"内容可预期"的目标窗口。
// 用记事本/计算器这类系统应用做验证不可复现（版本差异、UWP 包装、启动失败），
// 而自建靶程序可以精确断言 UIA 树内容、点击效果与文本输入结果。
//
// 控件与 UIA 断言点：
//   statusLabel   —— 显示状态文本，点击按钮后变化，用于验证 click 生效
//   inputBox      —— 文本框，用于验证 type_text / set_value
//   clickButton   —— 点击后把 statusLabel 改成 "clicked:N"
//   toggleCheck   —— 复选框，用于验证 perform_secondary_action(Toggle)
//   choiceCombo   —— 下拉框，用于验证 Expand/Collapse 与选择
//   itemsList     —— 列表框，用于验证 Select
using System;
using System.Drawing;
using System.Windows.Forms;

namespace CuTestFixture
{
    internal sealed class TestTargetForm : Form
    {
        private readonly Label _status;
        private readonly TextBox _input;
        private readonly Button _click;
        private readonly CheckBox _toggle;
        private readonly ComboBox _combo;
        private readonly ListBox _list;
        private int _clickCount;

        public TestTargetForm()
        {
            Text = "CU Helper Test Target";
            ClientSize = new Size(520, 420);
            StartPosition = FormStartPosition.CenterScreen;
            Name = "TestTargetForm";

            _status = new Label
            {
                Name = "statusLabel",
                Text = "status:idle",
                AutoSize = false,
                Location = new Point(16, 16),
                Size = new Size(480, 24),
                // 刻意不设置 AccessibleName：这样 UIA 的 Name 就是可见文本本身，
                // 测试才能通过无障碍树观察到"点击真的改变了界面状态"。
            };

            _input = new TextBox
            {
                Name = "inputBox",
                Text = "",
                Location = new Point(16, 52),
                Width = 480,
                AccessibleName = "inputBox",
            };

            _click = new Button
            {
                Name = "clickButton",
                Text = "Click Me",
                Location = new Point(16, 88),
                Width = 140,
                AccessibleName = "clickButton",
            };
            _click.Click += (sender, args) =>
            {
                _clickCount++;
                _status.Text = "status:clicked:" + _clickCount;
            };

            _toggle = new CheckBox
            {
                Name = "toggleCheck",
                Text = "Enable feature",
                Location = new Point(16, 128),
                Width = 200,
                AccessibleName = "toggleCheck",
            };
            _toggle.CheckedChanged += (sender, args) =>
            {
                _status.Text = _toggle.Checked ? "status:toggled:on" : "status:toggled:off";
            };

            _combo = new ComboBox
            {
                Name = "choiceCombo",
                Location = new Point(16, 164),
                Width = 220,
                DropDownStyle = ComboBoxStyle.DropDownList,
                AccessibleName = "choiceCombo",
            };
            _combo.Items.AddRange(new object[] { "alpha", "beta", "gamma" });
            _combo.SelectedIndexChanged += (sender, args) =>
            {
                _status.Text = "status:combo:" + _combo.SelectedItem;
            };

            _list = new ListBox
            {
                Name = "itemsList",
                Location = new Point(16, 200),
                Size = new Size(220, 160),
                AccessibleName = "itemsList",
            };
            _list.Items.AddRange(new object[] { "one", "two", "three" });
            _list.SelectedIndexChanged += (sender, args) =>
            {
                _status.Text = "status:list:" + _list.SelectedItem;
            };

            var quit = new Button
            {
                Name = "quitButton",
                Text = "Quit",
                Location = new Point(260, 340),
                Width = 100,
                AccessibleName = "quitButton",
            };
            quit.Click += (sender, args) => Close();

            Controls.AddRange(new Control[] { _status, _input, _click, _toggle, _combo, _list, quit });
            AcceptButton = _click;
        }

        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TestTargetForm());
        }
    }
}
