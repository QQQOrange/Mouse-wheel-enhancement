using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace MouseWheelEnhancement.Launcher
{
    /// <summary>
    /// 「缺少 .NET 10」提示窗口。
    ///
    /// # 为什么用手写布局而不是设计器
    ///
    /// 就三个按钮一段话，手写坐标比拖控件更好审 —— 这个窗口的存在感越弱越好，
    /// 它一辈子只在"用户还没装运行时"的那一次出现。
    ///
    /// # 为什么是浅色
    ///
    /// 主程序是深色主题，但启动器跑在 .NET Framework 上、没有主程序那套样式资源。
    /// 硬凑一个手绘深色在 WinForms 上容易出各种渲染怪相（高 DPI 下尤其），
    /// 而这是**唯一**一条通往主程序的路径，绝不能为了好看冒这个险。
    /// </summary>
    internal sealed class RuntimeDialog : Form
    {
        private readonly ProbeResult _probe;
        private readonly Label _status;
        private readonly Button _download;

        /// <summary>用户修好运行时并点了「重新检测」→ 主程序应当被拉起。</summary>
        public bool ShouldLaunch { get; private set; }

        public RuntimeDialog(ProbeResult probe)
        {
            _probe = probe;

            Text = Program.ProductName;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            ClientSize = new Size(520, 384);
            BackColor = Color.White;
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            try
            {
                var ico = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (ico != null) Icon = ico;
            }
            catch { /* 图标取不到不影响功能 */ }

            // ── 标题
            var icon = new PictureBox
            {
                Bounds = new Rectangle(16, 16, 36, 36),
                SizeMode = PictureBoxSizeMode.StretchImage,
                Image = ImageOf(),
            };
            Controls.Add(icon);

            Controls.Add(new Label
            {
                Text = "未检测到 .NET 环境",
                Bounds = new Rectangle(62, 18, 440, 28),
                Font = new Font(Font.FontFamily, 14F, FontStyle.Bold),
                ForeColor = Color.FromArgb(0x16, 0x1A, 0x22),
            });
            Controls.Add(new Label
            {
                Text = "这台电脑上还没有安装「鼠标滚轮增强」需要的 .NET 10 桌面运行时，所以现在无法启动。",
                Bounds = new Rectangle(62, 48, 440, 18),
                ForeColor = Color.FromArgb(0x5A, 0x62, 0x72),
            });

            // ── 正文
            Controls.Add(new Label
            {
                Bounds = new Rectangle(16, 70, 488, 90),
                ForeColor = Color.FromArgb(0x22, 0x27, 0x33),
                Text =
                    "点下面的「去下载 .NET 10」，会用默认浏览器打开微软官方的 .NET 10 下载页；" +
                    "在那页里选 Desktop Runtime（桌面运行时）→ x64 下载安装即可" +
                    "（不需要 SDK，也不需要装 Visual Studio）。\r\n\r\n" +
                    "装好之后回到这个窗口点「重新检测」，程序就会正常启动 —— 不用重新运行。",
            });

            // ── 检测详情
            Controls.Add(new TextBox
            {
                Bounds = new Rectangle(16, 166, 488, 56),
                Multiline = true,
                ReadOnly = true,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.FromArgb(0xF4, 0xF6, 0xF9),
                ForeColor = Color.FromArgb(0x3A, 0x42, 0x52),
                Text = probe.Detail.Replace("\n", "\r\n"),
                TabStop = false,
            });

            // ── 官方地址（浏览器被拦时还能手动复制）
            var link = new LinkLabel
            {
                Bounds = new Rectangle(16, 230, 488, 20),
                Text = Program.DownloadUrl,
                LinkColor = Color.FromArgb(0x2B, 0x5C, 0xC8),
                ActiveLinkColor = Color.FromArgb(0x2B, 0x5C, 0xC8),
            };
            link.LinkClicked += (s, e) => OpenDownloadPage();
            Controls.Add(link);

            // ── 状态行
            _status = new Label
            {
                Bounds = new Rectangle(16, 268, 488, 56),
                ForeColor = Color.FromArgb(0x5A, 0x62, 0x72),
                Text = "检测结果：" + probe.Summary,
            };
            Controls.Add(_status);

            // ── 按钮：下载在左（主操作），退在最右
            _download = MakeButton("去下载 .NET 10", 190, 130, primary: true);
            _download.Click += (s, e) =>
            {
                OpenDownloadPage();
                _status.Text = "已经在浏览器里打开了微软官方下载页。\r\n" +
                               "装好 Desktop Runtime x64 之后，回到这里点「重新检测」即可。";
            };
            Controls.Add(_download);

            var redetect = MakeButton("重新检测", 330, 100, primary: false);
            redetect.Click += (s, e) => Redetect();
            Controls.Add(redetect);

            var quit = MakeButton("退出", 440, 64, primary: false);
            quit.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(quit);

            CancelButton = quit;
            AcceptButton = _download;
        }

        private Button MakeButton(string text, int x, int width, bool primary)
        {
            var b = new Button
            {
                Text = text,
                Bounds = new Rectangle(x, 336, width, 32),
                FlatStyle = FlatStyle.Flat,
                UseVisualStyleBackColor = false,
                BackColor = primary ? Color.FromArgb(0x2B, 0x5C, 0xC8) : Color.FromArgb(0xF2, 0xF4, 0xF7),
                ForeColor = primary ? Color.White : Color.FromArgb(0x22, 0x27, 0x33),
                Font = new Font(Font.FontFamily, 9F, primary ? FontStyle.Bold : FontStyle.Regular),
            };
            b.FlatAppearance.BorderSize = primary ? 0 : 1;
            b.FlatAppearance.BorderColor = Color.FromArgb(0xD4, 0xD9, 0xE2);
            return b;
        }

        private Image ImageOf()
        {
            try
            {
                var ico = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (ico != null) return ico.ToBitmap();
            }
            catch { }
            return null;
        }

        /// <summary>用**系统默认浏览器**打开官方下载页。</summary>
        private void OpenDownloadPage()
        {
            try
            {
                Process.Start(new ProcessStartInfo(Program.DownloadUrl) { UseShellExecute = true });
                Log("已用默认浏览器打开下载页");
            }
            catch (Exception ex)
            {
                _status.Text = "打不开浏览器：" + ex.Message + "\r\n" +
                               "可以手动复制上面的地址到浏览器里打开。";
                Log("打开浏览器失败：" + ex.Message);
            }
        }

        /// <summary>重新检测：装好了就直接放行，没装好就更新提示。</summary>
        private void Redetect()
        {
            var again = RuntimeProbe.Probe();
            Log("重新检测 → " + again.State + " " + again.Summary);

            if (again.State == ProbeState.Ok)
            {
                ShouldLaunch = true;
                DialogResult = DialogResult.OK;
                Close();
                return;
            }

            _status.Text = again.State == ProbeState.Unknown
                // 这次探测失败不能把用户困住：给一条明确的继续路径
                ? "这次没能完成检测（" + again.Error + "）。\r\n" +
                  "如果你确实装好了，可以直接点下面的「仍然启动试试」。"
                : "还是没有检测到：" + again.Summary + "\r\n" +
                  "装完需要几秒生效；也可以点「仍然启动试试」。";

            ShowForceLaunch();
        }

        /// <summary>检测依然失败时，给用户一条"我自己承担"的出路（不误拦截）。</summary>
        private void ShowForceLaunch()
        {
            if (Controls.Contains(_force)) return;

            _force = MakeButton("仍然启动试试", 16, 120, primary: false);
            _force.Click += (s, e) => { ShouldLaunch = true; DialogResult = DialogResult.OK; Close(); };
            Controls.Add(_force);
        }

        private Button _force;

        private void Log(string msg) => Program.Log("dialog: " + msg);
    }
}
