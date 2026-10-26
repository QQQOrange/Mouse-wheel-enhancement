using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using MouseWheelEnhancement.Core;

namespace MouseWheelEnhancement;

/// <summary>
/// 托盘图标与右键菜单。
///
/// 用 WinForms 的 <see cref="NotifyIcon"/> 而不是第三方库：它在 Windows 上是最稳的，
/// 而 WPF 本身没有托盘能力。菜单也用它自带的 <see cref="ContextMenuStrip"/>，
/// 因为"右键托盘图标弹菜单"这件事对系统集成度要求很高，自己画容易在某些
/// DPI / 多屏 / 任务栏位置下错位。
/// </summary>
internal sealed class TrayController : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Icon _iconOn;
    private readonly Icon _iconOff;

    private readonly ToolStripMenuItem _enableItem;
    private readonly ToolStripMenuItem _appItem;
    private readonly ToolStripMenuItem _updateItem;

    public TrayController()
    {
        _iconOn = BuildIcon(true);
        _iconOff = BuildIcon(false);

        var menu = new ContextMenuStrip { ShowImageMargin = false };

        _enableItem = new ToolStripMenuItem("启用增强", null, (_, _) => Services.ToggleEnabled());
        _appItem = new ToolStripMenuItem("", null, (_, _) => ToggleCurrentApp());
        _updateItem = new ToolStripMenuItem("检查更新…", null, async (_, _) => await CheckUpdateAsync());
        var settings = new ToolStripMenuItem("打开设置…", null, (_, _) => Services.ShowWindow());
        var exit = new ToolStripMenuItem("退出", null, (_, _) => App.ExitApp());

        menu.Items.AddRange(new ToolStripItem[]
        {
            _enableItem,
            new ToolStripSeparator(),
            settings,
            _appItem,
            new ToolStripSeparator(),
            _updateItem,
            new ToolStripSeparator(),
            exit,
        });
        menu.Opening += (_, _) => Refresh(menu);

        _icon = new NotifyIcon
        {
            Icon = Services.Config.Enabled ? _iconOn : _iconOff,
            Visible = true,
            ContextMenuStrip = menu,
            Text = Tooltip(),
        };
        _icon.DoubleClick += (_, _) => Services.ShowWindow();

        Services.StateChanged += RefreshVisual;
    }

    private static string Tooltip()
    {
        var s = Services.Config.Enabled ? "已启用" : "已暂停";
        var running = Services.Engine.IsRunning ? "" : "｜钩子未运行";
        // NotifyIcon.Text 上限 63 个字符，超了会抛异常
        var t = $"{Services.AppTitle} · {s}{running}";
        return t.Length > 62 ? t[..62] : t;
    }

    private void RefreshVisual()
    {
        try
        {
            _icon.Icon = Services.Config.Enabled ? _iconOn : _iconOff;
            _icon.Text = Tooltip();
        }
        catch { /* 图标正在被系统绘制时偶发失败，忽略 */ }
    }

    private void Refresh(ContextMenuStrip menu)
    {
        _enableItem.Text = Services.Config.Enabled ? "暂停增强" : "启用增强";
        _enableItem.Checked = Services.Config.Enabled;

        var target = Services.Engine.Targets.CurrentForeground();
        if (target.IsKnown)
        {
            var listed = Services.IsAppListed(target.Exe);
            var verb = Services.Config.Filter.Mode == Core.FilterMode.Whitelist
                ? (listed ? "移出白名单" : "加入白名单")
                : (listed ? "移出黑名单" : "加入黑名单");
            _appItem.Text = $"{verb}：{target.Exe}";
            _appItem.Enabled = true;
        }
        else
        {
            _appItem.Text = "（没有识别出当前应用）";
            _appItem.Enabled = false;
        }

        _updateItem.Text = Services.PendingUpdate is { Newer: true } p
            ? $"有新版 v{p.Version} —— 去更新"
            : "检查更新…";

        _ = menu; // 预留：将来要按状态隐藏整段菜单
    }

    private void ToggleCurrentApp()
    {
        var target = Services.Engine.Targets.CurrentForeground();
        var msg = Services.ToggleAppInList(target.Exe);
        Log.Info("托盘：" + msg);
        _icon.ShowBalloonTip(1500, Services.AppTitle, msg, ToolTipIcon.None);
    }

    private async Task CheckUpdateAsync()
    {
        Services.ShowWindow(4); // 更新页
        try
        {
            var outcome = await Services.Updater.CheckAsync(Services.Version);
            if (!outcome.Ok)
            {
                _icon.ShowBalloonTip(3000, "检查更新失败", outcome.Error ?? "未知错误", ToolTipIcon.Warning);
                return;
            }
            var info = outcome.Info!;
            Services.PendingUpdate = info;
            if (info.Newer)
                _icon.ShowBalloonTip(3000, "发现新版本", $"v{info.Version} 可以更新", ToolTipIcon.Info);
            else
                _icon.ShowBalloonTip(2000, "已是最新版本", $"当前 v{Services.Version}", ToolTipIcon.None);
        }
        catch (Exception ex)
        {
            Log.Warn("托盘检查更新失败：" + ex.Message);
        }
    }

    /// <summary>后台自动检查发现新版时提示一句（静默模式下没新版不打扰）。</summary>
    public void NotifyUpdateAvailable(string version)
    {
        try
        {
            _icon.ShowBalloonTip(4000, Services.AppTitle,
                $"发现新版本 v{version}，点击托盘图标打开设置即可更新", ToolTipIcon.Info);
        }
        catch { }
    }

    private bool _hinted;

    /// <summary>
    /// 第一次点关闭时提示"我还在托盘里"。不提示的话用户会以为程序退了，
    /// 结果滚轮还在被接管 —— 那是最容易让人以为"中招了"的体验。
    /// </summary>
    public void ShowHintOnce()
    {
        if (_hinted) return;
        _hinted = true;
        try
        {
            _icon.ShowBalloonTip(2500, Services.AppTitle,
                "程序已最小化到托盘，滚轮增强仍在工作。右键托盘图标可以暂停或退出。",
                ToolTipIcon.Info);
        }
        catch { }
    }

    /* ══════════════ 图标绘制 ══════════════ */

    /// <summary>运行时画托盘图标（32×32）：一个滚轮 + 向上箭头，暂停时变灰。</summary>
    private static Icon BuildIcon(bool on)
    {
        const int n = 32;
        using var bmp = new Bitmap(n, n);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var accent = on ? Color.FromArgb(0x5B, 0x8C, 0xFF) : Color.FromArgb(0x6B, 0x74, 0x86);
            var accentSoft = on ? Color.FromArgb(0x8F, 0xB0, 0xFF) : Color.FromArgb(0x8A, 0x92, 0xA1);

            using var bg = new SolidBrush(Color.FromArgb(0x0F, 0x11, 0x17));
            g.FillEllipse(bg, 0, 0, n - 1, n - 1);

            // 滚轮胶囊
            using var wheel = new SolidBrush(accent);
            g.FillRectangle(wheel, 12, 5, 8, 22);
            g.FillEllipse(wheel, 12, 3, 8, 8);
            g.FillEllipse(wheel, 12, 21, 8, 8);

            // 两道刻线
            using var notch = new SolidBrush(Color.FromArgb(0x0F, 0x11, 0x17));
            g.FillRectangle(notch, 14, 11, 4, 1.6f);
            g.FillRectangle(notch, 14, 15, 4, 1.6f);

            // 右侧小箭头：提示"增强"
            using var pen = new Pen(accentSoft, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(pen, 22, 16, 26, 12);
            g.DrawLine(pen, 26, 12, 26, 17);
            g.DrawLine(pen, 26, 12, 21, 12);
        }

        var hicon = bmp.GetHicon();
        // 复制一份再销毁句柄，避免 Icon 持有已释放的 HICON
        using var tmp = Icon.FromHandle(hicon);
        var copy = (Icon)tmp.Clone();
        Native.DestroyIcon(hicon);
        return copy;
    }

    public void Dispose()
    {
        Services.StateChanged -= RefreshVisual;
        try { _icon.Visible = false; _icon.Dispose(); } catch { }
        _iconOn.Dispose();
        _iconOff.Dispose();
    }
}
