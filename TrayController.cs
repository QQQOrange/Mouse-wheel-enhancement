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

    /* 图标句柄自己留着，退出时销毁。
     * Icon.FromHandle 不接管 HICON，光 Icon.Dispose() 不会释放它 ——
     * 这是一个很典型的"每跑一次漏两个 GDI 句柄"，长时间挂着看不出来，
     * 但在任务管理器里 GDI 对象数只增不减。 */
    private readonly IntPtr _hiconOn;
    private readonly IntPtr _hiconOff;

    private readonly ContextMenuStrip _menu;

    private readonly ToolStripMenuItem _enableItem;
    private readonly ToolStripMenuItem _appItem;
    private readonly ToolStripMenuItem _updateItem;

    public TrayController()
    {
        (_iconOn, _hiconOn) = BuildIcon(true);
        (_iconOff, _hiconOff) = BuildIcon(false);

        var menu = new ContextMenuStrip { ShowImageMargin = false };
        _menu = menu;

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
        /* 打开设置：**单击与双击合并成一次触发**。
         *
         * 之前是 MouseClick 和 DoubleClick 各绑一个 ShowWindow()，
         * 结果双击托盘会连发 2~3 次 —— 每次都是一次完整的
         * "Show() + 置顶翻转 + 布局更新"，而这些全都发生在
         * NotifyIcon 的原生 WndProc 里（异常通道见 App.OnWinFormsThreadException
         * 的说明）。触发次数越多，撞上"窗口正在显示 / 弹层正在重定位"的时间窗越大。
         *
         * 现在改成：单击先**等 400ms**；如果这期间来了双击，就取消单击，
         * 只按双击处理一次。用户体感一样（点一下就开），但内部只走一遍。 */
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            ScheduleShow();
        };
        _icon.DoubleClick += (_, _) =>
        {
            _pendingShowCts?.Cancel();
            _pendingShowCts = null;
            Services.ShowWindow();
        };

        Services.StateChanged += RefreshVisual;
    }

    /// <summary>
    /// 单击托盘后延迟一点再真的打开窗口。
    ///
    /// <para>
    /// 这 400ms 是用来"等双击"的：双击的第二次按下会在这个窗口期内到达，
    /// 那时就把这次单击取消，只让双击处理一次。
    /// 于是无论用户点一下还是点两下，内部都**只走一遍**打开流程。
    /// </para>
    /// </summary>
    private void ScheduleShow()
    {
        _pendingShowCts?.Cancel();
        var cts = new CancellationTokenSource();
        _pendingShowCts = cts;
        _ = Task.Delay(400, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            try { Services.ShowWindow(); }
            catch (Exception ex) { Log.Warn("托盘打开窗口失败：" + ex.Message); }
        }, TaskScheduler.Default);
    }

    private CancellationTokenSource? _pendingShowCts;

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

    /// <summary>
    /// 运行时画托盘图标（32×32）：浅色鼠标轮廓 + 顶部按键缝 + 亮蓝滚轮，暂停时整体变灰。
    ///
    /// 形状数据和 <c>tools/make-icon.js</c>（app.ico）是同一份 —— 剖面控制点、
    /// 滚轮尺寸、按键缝长度都照抄。两处必须一致，否则任务栏图标和托盘图标
    /// 会是「长得像但不是同一个」的尴尬关系。
    /// </summary>
    private static (Icon Icon, IntPtr HIcon) BuildIcon(bool on)
    {
        var bmp = DrawIconBitmap(on);
        /* 直接用 HICON 包一个 Icon。
         *
         * 这里刻意**不做** "Clone 之后 DestroyIcon" 那一套：Icon.Clone() 在某些
         * 实现里与原对象共享 HICON，销毁之后托盘图标会变成一块空白 —— 而且只在
         * 真机上看得到，代码看起来完全正常。
         * Icon.FromHandle 不接管句柄，Dispose 也不会销毁它，所以句柄由调用方
         * 保存并在 Dispose 里 DestroyIcon —— 见 _hiconOn / _hiconOff。 */
        var hicon = bmp.GetHicon();
        return (Icon.FromHandle(hicon), hicon);
    }

    /// <summary>
    /// 把托盘图标渲染成字符画（只给 --smoke 用）。
    ///
    /// 托盘图标是唯一常驻可见的东西，它画坏了（空白、错位、多边形自交导致填充成怪形）
    /// 在日志里毫无痕迹 —— 只能靠人盯着托盘看。转成字符画就能在测试里直接看形状。
    /// </summary>
    internal static string AsciiDump(bool on)
    {
        using var bmp = DrawIconBitmap(on);
        return AsciiOf(bmp);
    }

    /// <summary>把任意位图转成字符画（托盘图标与 exe 内嵌图标的回归检查共用）。</summary>
    internal static string AsciiOf(Bitmap bmp)
    {
        var sb = new System.Text.StringBuilder();

        for (var y = 0; y < bmp.Height; y++)
        {
            for (var x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.A < 30) { sb.Append(' '); continue; }
                if (c.B - c.R > 55) { sb.Append('B'); continue; } // 蓝滚轮
                var lum = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
                var idx = Math.Clamp((int)Math.Round(lum * c.A / 255.0 * 9), 1, 9);
                sb.Append(" .:-=+*#%@"[idx]);
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static Bitmap DrawIconBitmap(bool on)
    {
        const float N = 32f;

        // 暂停态：不改变色相，只降明度 —— 一眼能看出「灰了」，而不是变成了别的图标
        var shell = on ? Color.FromArgb(0xE9, 0xEF, 0xFB) : Color.FromArgb(0xBF, 0xC6, 0xD3);
        var edge = on ? Color.FromArgb(0x3A, 0x4A, 0x66) : Color.FromArgb(0x62, 0x6B, 0x7A);
        var wheelTop = on ? Color.FromArgb(0x93, 0xBC, 0xFF) : Color.FromArgb(0x9A, 0xA3, 0xB4);
        var wheelBot = on ? Color.FromArgb(0x39, 0x6A, 0xE8) : Color.FromArgb(0x5E, 0x66, 0x78);
        var baseBg = Color.FromArgb(0x10, 0x15, 0x20);

        var bmp = new Bitmap((int)N, (int)N);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            // ① 圆角底板
            using (var bgPath = RoundedRect(0.4f, 0.4f, N - 0.8f, N - 0.8f, 7.4f))
            using (var bg = new SolidBrush(baseBg))
                g.FillPath(bg, bgPath);

            // ② 鼠标外壳（剖面采样成多边形）
            using (var body = MouseBodyPath())
            {
                using (var br = new SolidBrush(shell))
                    g.FillPath(br, body);

                // ③ 贴边的一道暗线：深底上白蛋形才不飘，也更像鼠标的分模线
                using (var pen = new Pen(edge, 0.6f))
                    g.DrawPath(pen, body);

                // ④ 顶部按键缝。用鼠标路径当裁剪区，缝就不会长出壳外
                var st = g.Save();
                g.SetClip(body, CombineMode.Replace);
                using (var pen = new Pen(baseBg, 1.35f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLine(pen, 16f, 4.6f, 16f, 10.4f);
                g.Restore(st);
            }

            // ⑤ 滚轮：竖向渐变胶囊（32px 太小，不放刻线 —— 会和抗锯齿糊成脏点）
            const float wx = 16f - 2.78f, wy = 9.4f, ww = 5.56f, wh = 8.6f;
            using (var wPath = RoundedRect(wx, wy, ww, wh, 2.78f))
            using (var wb = new LinearGradientBrush(new RectangleF(wx, wy, ww, wh), wheelTop, wheelBot, 90f))
                g.FillPath(wb, wPath);
        }

        return bmp;
    }

    public void Dispose()
    {
        Services.StateChanged -= RefreshVisual;

        try { _pendingShowCts?.Cancel(); } catch { }
        try { _pendingShowCts?.Dispose(); } catch { }
        _pendingShowCts = null;

        try { _icon.Visible = false; } catch { }
        try { _icon.ContextMenuStrip = null; } catch { }
        try { _icon.Dispose(); } catch { }
        try { _menu.Dispose(); } catch { }

        _iconOn.Dispose();
        _iconOff.Dispose();
        if (_hiconOn != IntPtr.Zero) Native.DestroyIcon(_hiconOn);
        if (_hiconOff != IntPtr.Zero) Native.DestroyIcon(_hiconOff);
    }

    /// <summary>圆角矩形路径。</summary>
    private static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath();
        var d = r * 2;
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>
    /// 鼠标轮廓：半宽剖面控制点。
    /// **必须和 tools/make-icon.js 里的 PROFILE 一致**（改一处就得改另一处）。
    /// </summary>
    private static readonly float[,] MouseProfile =
    {
        { 3.9f, 0.3f }, { 4.4f, 3.6f }, { 5.6f, 5.1f }, { 7.4f, 6.2f },
        { 10.0f, 7.0f }, { 14.0f, 7.6f }, { 18.0f, 7.8f }, { 22.0f, 7.4f },
        { 25.0f, 6.5f }, { 27.0f, 4.8f }, { 28.1f, 0.3f },
    };

    /// <summary>按剖面插值求某一高度处的半宽（与脚本里的 smoothstep 插值一致）。</summary>
    private static float MouseHalfW(float y)
    {
        var p = MouseProfile;
        var last = p.GetLength(0) - 1;
        if (y <= p[0, 0] || y >= p[last, 0]) return 0f;

        for (var i = 0; i < last; i++)
        {
            float y0 = p[i, 0], w0 = p[i, 1], y1 = p[i + 1, 0], w1 = p[i + 1, 1];
            if (y < y0 || y > y1) continue;
            var t = (y - y0) / (y1 - y0);
            return w0 + (w1 - w0) * (t * t * (3f - 2f * t));
        }
        return 0f;
    }

    /// <summary>把剖面采样成闭合多边形（右侧自上而下 + 左侧自下而上）。</summary>
    private static GraphicsPath MouseBodyPath()
    {
        const int Steps = 96;
        float yTop = MouseProfile[0, 0], yBot = MouseProfile[MouseProfile.GetLength(0) - 1, 0];

        var pts = new PointF[Steps * 2];
        for (var i = 0; i < Steps; i++)
        {
            var y = yTop + (yBot - yTop) * i / (Steps - 1f);
            var hw = MouseHalfW(y);
            pts[i] = new PointF(16f + hw, y);              // 右侧
            pts[Steps * 2 - 1 - i] = new PointF(16f - hw, y); // 左侧（反向补齐）
        }

        var path = new GraphicsPath();
        path.AddPolygon(pts);
        return path;
    }
}
