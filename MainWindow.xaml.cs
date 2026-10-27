using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MouseWheelEnhancement.Core;
using MouseWheelEnhancement.Update;

namespace MouseWheelEnhancement;

public partial class MainWindow : Window
{
    /// <summary>
    /// 程序化设置控件时置位，防止 Checked/ValueChanged 反向写回配置。
    /// 初值必须是 true：XAML 解析时给 Slider 设 Minimum/Maximum 就会触发
    /// ValueChanged，而那时后半个页面的控件还没建出来。
    /// </summary>
    private bool _loading = true;

    /// <summary>这次变更来自界面自己 —— 用来决定要不要整页回填（避免拖动滑块时被回填打断）。</summary>
    private bool _updatingFromUi;

    private readonly DispatcherTimer _tick;
    private readonly ObservableCollection<AppRuleRowData> _rows = new();

    /// <summary>画刷缓存：每 400 ms 的刷新里不必反复走资源字典查找。</summary>
    private readonly Dictionary<string, Brush> _brushes = new();

    /// <summary>帮助页是懒渲染的：二十多个参数块几百个控件，不影响启动才合理。</summary>
    private bool _helpRendered;

    /// <summary>曲线重绘合并标记：拖动滑块时一次布局只重画一次。</summary>
    private bool _curvePending;

    /// <summary>名单内容的指纹，用来避免"拖一下滑块就把列表重建一遍"。</summary>
    private string _appsSignature = "\u0000";

    /// <summary>置位后关闭窗口就是真的销毁（释放资源），而不是退回托盘。</summary>
    private bool _allowClose;
    private UpdateInfo? _info;
    private CancellationTokenSource? _downloadCts;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();

        AppsList.ItemsSource = _rows;

        SubtitleText.Text = $"{Services.AppTitleEn} · v{Services.Version}";
        PageBadge.Text = $"v{Services.Version}";
        CurrentVersionText.Text = $"v{Services.Version}";
        RepoText.Text = Services.Updater.Repo;
        AboutVersion.Text = $"v{Services.Version}（build {BuildStamp()}）";
        AboutAuthor.Text = "QQQOrange";
        AboutDataDir.Text = AppPaths.DataDir;
        AboutLogPath.Text = Log.FilePath;
        FooterDataDir.Text = AppPaths.DataDir;
        HomeText.Text = $"https://github.com/{Services.Updater.Repo}";
        GitHubText.Text = $"https://github.com/{Services.Updater.Repo}";
        ReleasesText.Text = Services.Updater.ReleasesUrl;
        RefreshElevationCard();

        LoadFromConfig();
        RefreshStats();
        RefreshApps();
        RefreshTokenUi();
        RedrawCurve();

        if (Services.Store.LoadWarning != null) UpdateStatus.Text = Services.Store.LoadWarning;
        if (Services.PendingUpdate is { Newer: true } pending) ShowUpdateInfo(pending, fromAutoCheck: true);

        Services.StateChanged += OnServicesStateChanged;

        _tick = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
        _tick.Tick += (_, _) => RefreshStats();
        _tick.Start();

        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { _tick.Start(); RefreshStats(); }
            else _tick.Stop();
        };

        Closing += OnClosing;
        Theme.Changed += OnThemeChanged;
        Closed += (_, _) =>
        {
            Services.StateChanged -= OnServicesStateChanged;
            Theme.Changed -= OnThemeChanged;
            _tick.Stop();

            /* 窗口（连同视觉树、D3D 表面、字形缓存）刚被销毁，此刻是把工作集
             * 还给系统的唯一合适时机。放在窗口自己身上而不是创建方，是为了
             * "谁打开的就谁负责还" —— 任何路径销毁窗口都会走到这里。 */
            MemoryTrim.TrimIfLarge();
        };
        Loaded += (_, _) =>
        {
            ApplyTitleBar(Theme.IsLightNow);
            ApplyWindowCorner();
        };
    }

    /// <summary>取画刷（带缓存）。</summary>
    private Brush B(string key)
    {
        if (_brushes.TryGetValue(key, out var b)) return b;
        b = (Brush)FindResource(key);
        _brushes[key] = b;
        return b;
    }

    /// <summary>
    /// 构建日期。用 exe 文件自身的修改时间，而不是 Assembly.Location ——
    /// 单文件发布时后者恒为空串（IL3000）。
    /// </summary>
    private static string BuildStamp()
    {
        try
        {
            var exe = UpdateService.ExePath;
            return File.Exists(exe) ? File.GetLastWriteTime(exe).ToString("yyyy-MM-dd") : "local";
        }
        catch { return "local"; }
    }

    /// <summary>
    /// 让**标题栏**跟着界面主题走（Win10 1809+ 支持，取不到就算了）。
    ///
    /// <para>
    /// 浅色时必须显式写 0，不能"什么都不做"：这个属性是**按窗口**保存的，
    /// 从深色切回浅色时如果只是不再设置，标题栏会停在深色，
    /// 于是窗口主体是白的、标题栏是黑的，很难看。
    /// </para>
    /// </summary>
    private void ApplyTitleBar(bool light)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            var dark = light ? 0 : 1;
            // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE；老 build 上是 19
            if (Native.DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)) != 0)
                Native.DwmSetWindowAttribute(hwnd, 19, ref dark, sizeof(int));
        }
        catch { /* 非关键美化 */ }
    }

    /// <summary>
    /// 窗口外框四角变圆角（Win11 22000+；取不到就保持直角，不报错）。
    ///
    /// <para>
    /// 自绘标题栏之后窗口不再有标准边框，Win11 的默认圆角就不生效了，
    /// 得显式把 DWMWA_WINDOW_CORNER_PREFERENCE 设成 DWMWCP_ROUND。
    /// </para>
    /// <para>
    /// 圆角只影响**窗口外形**：左侧栏是直角方块，不跟着收角的话，
    /// 左上/左下会露出两块缺口（后面是桌面）。所以这里顺手把侧栏的
    /// 左外角也打成同一个半径 —— 只有真的圆上时才打，Win10 上仍是直角，
    /// 不会出现"窗口方、侧栏圆"的割裂感。
    /// </para>
    /// </summary>
    private void ApplyWindowCorner()
    {
        try
        {
            if (Environment.OSVersion.Version.Build < 22000) return;   // 只有 Win11 有得设

            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            var pref = Native.DWMWCP_ROUND;
            if (Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE,
                    ref pref, sizeof(int)) != 0)
                return;   // 设不上（旧版 DWM、远程桌面之类）就老老实实直角

            // 侧栏左外角跟窗口圆角对齐，半径取窗口圆角的 8px 同款
            if (Sidebar != null) Sidebar.CornerRadius = new CornerRadius(8, 0, 0, 8);
        }
        catch { /* 纯美化，任何失败都不该影响启动 */ }
    }

    /// <summary>
    /// 主题变了：把"现画的东西"重画一遍。
    ///
    /// <para>
    /// 走 DynamicResource 的控件会自己更新，但有三处不会：
    /// </para>
    /// <list type="number">
    ///   <item><see cref="B"/> 里的画刷缓存 —— 存的是旧主题的 Brush 对象；</item>
    ///   <item>倍率曲线 —— Canvas 里的 Line/Polygon/TextBlock 是代码建的，颜色写死在对象上；</item>
    ///   <item>帮助页 —— 文案里的前景色是渲染时查出来的，不重建就一直是旧色。</item>
    /// </list>
    /// </summary>
    private void OnThemeChanged()
    {
        _brushes.Clear();
        ApplyTitleBar(Theme.IsLightNow);
        RefreshStats();   // 状态点/徽章的颜色是 FindResource 取完写死在控件上的

        if (_helpRendered && HelpPanel != null)
        {
            HelpPanel.Children.Clear();
            _helpRendered = false;
            EnsureHelpRendered();
        }

        UpdateThemeHints();
        InvalidateCurve();
    }

    /// <summary>冒烟测试用：把关键尺寸打出来，确认页面真的被布局了（不是全 0）。</summary>
    internal string LayoutReport()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"win={ActualWidth:0}x{ActualHeight:0}");
        sb.Append($" nav={Nav.ActualWidth:0}x{Nav.ActualHeight:0}");
        sb.Append($" curve={CurveCanvas.ActualWidth:0}x{CurveCanvas.ActualHeight:0}");
        sb.Append($" line={CurveCanvas.Children.Count}");
        sb.Append($" help={HelpPanel.Children.Count}");
        sb.Append($" tab={Nav.SelectedIndex}");
        sb.Append($" 自启={(AutoStartSwitch.IsChecked == true ? "开" : "关")}");
        sb.Append($" 管理员启动={(AdminStartSwitch.IsChecked == true ? "开" : "关")}");
        sb.Append($" 管理员行可用={(AdminStartRow.IsEnabled ? "是" : "否")}");
        return sb.ToString();
    }

    public void SelectTab(int index)
    {
        if (index >= 0 && index < Nav.Items.Count) Nav.SelectedIndex = index;
        if (index == HelpTabIndex) EnsureHelpRendered();
    }

    /// <summary>测量/测试用：立刻真正销毁窗口（绕过"退到托盘"）。</summary>
    internal void CloseAndRelease()
    {
        _allowClose = true;
        Close();
    }

    /* ══════════════ 配置 → 界面 ══════════════ */

    private void LoadFromConfig()
    {
        _loading = true;
        try
        {
            var c = Services.Config;

            MasterSwitch.IsChecked = c.Enabled;
            RefreshStartupSwitches();
            CloseToTraySwitch.IsChecked = c.Behavior.CloseToTray;
            PauseSelfSwitch.IsChecked = c.Behavior.PauseWhileSettingsOpen;

            BoostSwitch.IsChecked = c.Boost.Enabled;
            ThresholdSlider.Value = c.Boost.FastThresholdPerSec;
            SatSlider.Value = c.Boost.SaturationPerSec;
            MaxMultSlider.Value = c.Boost.MaxMultiplier;
            FlushReverseSwitch.IsChecked = c.Boost.FlushOnReverse;
            InvertSwitch.IsChecked = c.Input.InvertDirection;
            PrecisionSwitch.IsChecked = c.Input.EnhancePrecisionInput;

            SmoothSwitch.IsChecked = c.Smooth.Enabled;
            SyncWheelLinesFromSystem();
            ScopeBoosted.IsChecked = c.Smooth.Scope == SmoothScope.BoostedOnly;
            ScopeAlways.IsChecked = c.Smooth.Scope == SmoothScope.Always;
            Fps60.IsChecked = c.Smooth.TargetFps == 60;
            Fps90.IsChecked = c.Smooth.TargetFps == 90;
            Fps120.IsChecked = c.Smooth.TargetFps == 120;
            Fps144.IsChecked = c.Smooth.TargetFps == 144;
            Fpn2.IsChecked = c.Smooth.FramesPerNotch == 2;
            Fpn3.IsChecked = c.Smooth.FramesPerNotch == 3;
            Fpn4.IsChecked = c.Smooth.FramesPerNotch == 4;
            Fpn6.IsChecked = c.Smooth.FramesPerNotch == 6;
            Fpn8.IsChecked = c.Smooth.FramesPerNotch == 8;
            TauSlider.Value = c.Smooth.TailTauMs;
            GlideSlider.Value = c.Smooth.GlideMs;
            BacklogSlider.Value = c.Smooth.MaxBacklogNotches;

            ModeBlack.IsChecked = c.Filter.Mode == FilterMode.Blacklist;
            ModeWhite.IsChecked = c.Filter.Mode == FilterMode.Whitelist;
            TitleRegexBox.Text = c.Filter.TitleRegex;

            var th = Theme.Normalize(c.Appearance.Theme);
            ThemeSystem.IsChecked = th == Theme.System;
            ThemeLight.IsChecked = th == Theme.Light;
            ThemeDark.IsChecked = th == Theme.Dark;

            AutoFpsSwitch.IsChecked = c.Smooth.AutoFps;
            AutoCheckSwitch.IsChecked = c.Update.AutoCheck;
        }
        finally { _loading = false; }

        UpdateDerivedLabels();
    }

    /// <summary>只更新"显示出来的数字"，不碰控件值（拖动时逐帧调它）。</summary>
    private void UpdateDerivedLabels()
    {
        /* 解析期间可能被 Slider 的 Minimum/Maximum 触发，此时后面的控件还没建出来。
         * 这里把**本方法用到的每一个控件**都列进来了 —— 少列一个，就会在
         * "用户刚好在这一刻拖了一下滑块"时炸 NullReference。 */
        if (ThresholdValue == null || SatValue == null || MaxMultValue == null ||
            CurveHint == null || TauValue == null ||
            BacklogValue == null || FpsValue == null || FpsManualGroup == null ||
            GlideValue == null || GlideSlider == null ||
            ThemeNow == null || ThemeSystem == null ||
            RegexHint == null)
            return;

        var c = Services.Config;
        // 一律按整格显示：格数带小数点既不好读也不好调
        ThresholdValue.Text = $"{c.Boost.FastThresholdPerSec:0} 格/秒";
        SatValue.Text = $"{c.Boost.SaturationPerSec:0} 格/秒";
        MaxMultValue.Text = $"{c.Boost.MaxMultiplier:0}×";
        CurveHint.Text = $"拨 1 格 → 滚 {c.Boost.MaxMultiplier:0} 格";
        TauValue.Text = $"{c.Smooth.TailTauMs:0} ms";
        GlideValue.Text = c.Smooth.GlideMs <= 0 ? "关闭" : $"{c.Smooth.GlideMs:0} ms";

        /* 这两项是有依赖的：滑行只能把"松手时还没发完的量"慢慢放出来。
         * 惯性长度太小 → 一次滚动两三帧就发完了 → 松手时手里是空的 → 滑行形同虚设。
         * 用户不会自己想到这一层，直接在界面上指出来。 */
        if (GlideHint != null)
        {
            var clash = c.Smooth.GlideMs > 0 && c.Smooth.TailTauMs < 20;
            GlideHint.Visibility = clash ? Visibility.Visible : Visibility.Collapsed;
            GlideHint.Text = clash
                ? $"「惯性长度」只有 {c.Smooth.TailTauMs:0} ms，一次滚动在几帧内就发完了，" +
                  "松手时手里没有剩余量，这一项滑不起来 —— 把它调到 30 以上才会有尾巴。"
                : "";
        }
        BacklogValue.Text = $"{c.Smooth.MaxBacklogNotches:0} 格";

        // 自动时显示显示器实际刷新率（刷新率一变这里会跟着变），手动时显示选定值
        FpsValue.Text = c.Smooth.AutoFps
            ? RefreshRateMonitor.Describe()
            : $"手动 · {c.Smooth.TargetFps} Hz（{1000.0 / c.Smooth.TargetFps:0.00} ms/帧）";

        // 说明文字也要跟着变，否则关掉自动之后那句"自动跟着显示器刷新率走"还挂在那儿，
        // 用户看不出当前到底按哪个值在跑。
        FpsHint.Text = c.Smooth.AutoFps
            ? "自动跟着显示器刷新率走：屏幕多少 Hz，就每多少毫秒推一小步。"
            : "固定用你选的帧率注入。显示器更快时，手动设低了会白丢平滑度；设高了只是多算几帧，不会更顺。";

        FpsManualGroup.IsEnabled = !c.Smooth.AutoFps;
        FpsManualGroup.Opacity = c.Smooth.AutoFps ? 0.45 : 1.0;

        UpdateThemeHints();

        var pattern = c.Filter.TitleRegex;
        if (string.IsNullOrWhiteSpace(pattern))
        {
            RegexHint.Visibility = Visibility.Collapsed;
        }
        else
        {
            try
            {
                _ = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                RegexHint.Visibility = Visibility.Collapsed;
            }
            catch (ArgumentException ex)
            {
                RegexHint.Text = "这个正则写错了，暂时不生效：" + ex.Message;
                RegexHint.Visibility = Visibility.Visible;
            }
        }
    }

    private void RefreshStats()
    {
        var e = Services.Engine;
        var running = e.IsRunning;

        StatHook.Text = running ? "运行中" : "未运行";
        StatHook.Foreground = running
            ? B("SuccessBrush")
            : B("DangerBrush");

        var t = e.LastTarget;
        StatApp.Text = t.IsKnown ? t.Exe : "—";

        StatCount.Text = $"{e.BoostedCount} / {e.PassedCount}";

        var rate = e.LastRatePerSec;
        StatRate.Text = rate > 0.05 ? $"{rate:0.0} 格/秒" : "—";

        var on = Services.Config.Enabled;
        _loading = true;
        MasterSwitch.IsChecked = on;
        _loading = false;

        StatusText.Text = !on ? "已暂停" : running ? "已启用" : "钩子异常";
        StatusDot.Fill = (Brush)FindResource(!on ? "TextMuted" : running ? "SuccessBrush" : "DangerBrush");
        HookHint.Text = !on
            ? "总开关关闭 · 完全走系统默认"
            : running
                ? $"钩子正常 · 已接管 {e.BoostedCount} 次"
                : "钩子未启动，滚动不受影响";

        FooterRight.Text = Services.IsElevated
            ? $"v{Services.Version} · 管理员"
            : $"v{Services.Version} · 普通权限";
    }

    private void OnServicesStateChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(OnServicesStateChanged));
            return;
        }

        if (!_updatingFromUi) LoadFromConfig();

        UpdateDerivedLabels();
        RefreshApps();
        RefreshStats();
        InvalidateCurve();
    }

    /// <summary>界面改动 → 写配置 → 通知引擎 → 合并写盘。</summary>
    private void Commit(bool refreshDerived = true)
    {
        if (Services.Store == null || ThresholdValue == null) return;

        _updatingFromUi = true;
        try { Services.Store.Touch(); }
        finally { _updatingFromUi = false; }

        if (refreshDerived) UpdateDerivedLabels();
        InvalidateCurve();
    }

    /* ══════════════ 倍率曲线预览 ══════════════ */

    private void CurveCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => InvalidateCurve();

    /// <summary>
    /// 请求重绘曲线：同一轮消息里多次请求只真正画一次。
    /// 拖滑块时 ValueChanged 一秒能来几十次，每次都重建 121 个点的 Polyline
    /// 会把 UI 线程占满（拖动明显发涩）。
    /// </summary>
    private void InvalidateCurve()
    {
        if (_curvePending || CurveCanvas == null) return;
        _curvePending = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _curvePending = false;
            RedrawCurve();
        }), DispatcherPriority.Background);
    }

    private void RedrawCurve()
    {
        if (CurveCanvas == null) return;
        var w = CurveCanvas.ActualWidth;
        var h = CurveCanvas.ActualHeight;
        if (w < 60 || h < 40) return;

        CurveCanvas.Children.Clear();
        var cfg = Services.Config.Boost;

        const double left = 36, right = 10, top = 8, bottom = 18;
        var pw = Math.Max(10, w - left - right);
        var ph = Math.Max(10, h - top - bottom);

        var maxRate = Math.Max(cfg.SaturationPerSec * 1.45, 24);
        var maxMult = cfg.MaxMultiplier <= 1.02 ? 1.6 : cfg.MaxMultiplier;

        double X(double r) => left + pw * Math.Clamp(r / maxRate, 0, 1);
        double Y(double m) => top + ph * (1 - Math.Clamp((m - 1) / (maxMult - 1), 0, 1));

        // 网格（横：1×、中间、上限）
        var gridBrush = B("CurveGrid");
        foreach (var m in new[] { 1.0, (1 + maxMult) / 2, maxMult })
        {
            CurveCanvas.Children.Add(new Line
            {
                X1 = left, X2 = left + pw,
                Y1 = Y(m), Y2 = Y(m),
                Stroke = gridBrush,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 2, 3 },
            });
            var lbl = new TextBlock
            {
                Text = $"{m:0.#}×",
                FontSize = 9.5,
                Foreground = B("TextMuted"),
            };
            Canvas.SetLeft(lbl, 2);
            Canvas.SetTop(lbl, Y(m) - 9);
            CurveCanvas.Children.Add(lbl);
        }

        // 阈值竖线
        if (cfg.FastThresholdPerSec <= maxRate)
        {
            var x = X(cfg.FastThresholdPerSec);
            CurveCanvas.Children.Add(new Line
            {
                X1 = x, X2 = x, Y1 = top, Y2 = top + ph,
                Stroke = B("CurveGuide"),
                StrokeThickness = 1,
            });
        }

        // 曲线（采样 draw）
        var pts = new PointCollection();
        const int steps = 120;
        for (var i = 0; i <= steps; i++)
        {
            var r = maxRate * i / steps;
            pts.Add(new Point(X(r), Y(WheelVelocityTracker.MultiplierFor(r, cfg))));
        }

        // 面积填充
        var area = new PointCollection(pts) { new Point(X(maxRate), top + ph), new Point(X(0), top + ph) };
        CurveCanvas.Children.Add(new Polygon
        {
            Points = area,
            Fill = B("CurveArea"),
        });

        CurveCanvas.Children.Add(new Polyline
        {
            Points = pts,
            Stroke = B("AccentBrush"),
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
        });

        // 底部刻度：阈值 / 满速点
        foreach (var (rate, text) in new[]
                 {
                     (cfg.FastThresholdPerSec, $"{cfg.FastThresholdPerSec:0}"),
                     (cfg.SaturationPerSec, $"{cfg.SaturationPerSec:0}"),
                 })
        {
            if (rate > maxRate) continue;
            var lbl = new TextBlock
            {
                Text = text,
                FontSize = 9.5,
                Foreground = B("TextMuted"),
            };
            Canvas.SetLeft(lbl, Math.Min(X(rate) - 6, left + pw - 12));
            Canvas.SetTop(lbl, top + ph + 3);
            CurveCanvas.Children.Add(lbl);
        }

        var axis = new TextBlock
        {
            Text = "格/秒",
            FontSize = 9.5,
            Foreground = B("TextMuted"),
        };
        Canvas.SetLeft(axis, left + pw - 26);
        Canvas.SetTop(axis, top + ph + 3);
        CurveCanvas.Children.Add(axis);
    }

    /* ══════════════ 事件：总开关与行为 ══════════════ */

    private bool _navSyncing;

    /// <summary>
    /// 侧栏导航 → 页面。
    ///
    /// <para>
    /// 左边是 ListBox、右边是（没有标签条的）TabControl，两者要双向同步：
    /// 用户点导航时推 TabControl，代码调用 <see cref="SelectTab"/> 时推回导航。
    /// 用 <c>_navSyncing</c> 挡住回环 —— 否则 A 改 B、B 又改 A，选中项会抖。
    /// </para>
    /// </summary>
    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_navSyncing || NavList.SelectedIndex < 0) return;

        _navSyncing = true;
        try { Nav.SelectedIndex = NavList.SelectedIndex; }
        finally { _navSyncing = false; }
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 标题栏显示当前页名（TabItem.Header 仍是纯文本，正好拿来当标题）
        if (Nav.SelectedItem is TabItem ti && ti.Header != null)
            PageTitle.Text = ti.Header.ToString();

        if (!_navSyncing && Nav.SelectedIndex >= 0)
        {
            _navSyncing = true;
            try { NavList.SelectedIndex = Nav.SelectedIndex; }
            finally { _navSyncing = false; }
        }

        if (Nav.SelectedIndex == HelpTabIndex) EnsureHelpRendered();
        if (Nav.SelectedIndex == 1) InvalidateCurve();
    }

    /// <summary>
    /// 标题栏的深浅色按钮：一次点击直接切到"另一种"，并把它记成显式选择。
    ///
    /// <para>
    /// 只做浅↔深两态切换，不把"跟随系统"塞进这个循环 ——
    /// 三态循环点起来没人能预期下一次是什么。"跟随系统"仍然在「概览 → 外观」里选。
    /// </para>
    /// </summary>
    private void ThemeBtn_Click(object sender, RoutedEventArgs e)
    {
        var wantLight = !Theme.IsLightNow;
        var mode = wantLight ? Theme.Light : Theme.Dark;

        Services.Config.Appearance.Theme = mode;
        Theme.SetMode(mode);
        UpdateThemeHints();
        Commit(refreshDerived: false);

        // 换掉 ToolTip 之前先把这个按钮的提示气泡收掉。
        // 用户就是悬停在按钮上点它的，此时气泡正开着；
        // 直接换字符串会触发 WPF 去重挂正在显示的 Popup，
        // 那是"根 Visual 不能具有父级"最容易冒头的时机。
        PopupCleanup.CloseCurrentToolTip();
        ThemeBtn.ToolTip = $"当前{(wantLight ? "浅色" : "深色")} · 点击切换";
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    /// <summary>关闭按钮：走正常的 Closing 流程（默认收进托盘，可在设置里改成真退出）。</summary>
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>帮助页的页签序号（0 概览 / 1 加速 / 2 平滑 / 3 应用 / 4 更新 / 5 帮助 / 6 关于）。</summary>
    private const int HelpTabIndex = 5;

    /// <summary>
    /// 第一次真正打开「帮助」页时再生成内容。
    /// 那些文案是几百个控件，一次性在构造里建好会让每次开设置都白付这笔开销 ——
    /// 而大多数时候用户根本不会翻到这一页。
    /// </summary>
    private void EnsureHelpRendered()
    {
        if (_helpRendered || HelpPanel == null) return;
        _helpRendered = true;
        HelpContent.Render(HelpPanel);
    }

    private void Master_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Services.SetEnabled(MasterSwitch.IsChecked == true);
    }

    private void AutoStart_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var on = AutoStartSwitch.IsChecked == true;
        Services.Config.Behavior.RunAtLogin = on;

        /* 注意：**不要**顺手把「以管理员身份运行」也关掉。
         *
         * 旧版本里它俩是从属关系，父开关一关就连带拉平。现在管理员开关管的是
         * 每一次启动（手动双击也提权），跟"开不开机自启"是两件事 ——
         * 用户完全可能想要"平时以管理员跑，但别开机自己启动"。
         * 真在这里清掉的话，用户关一次开机自启就悄悄丢了管理员模式，
         * 下次打开又看到"当前不是管理员身份"。 */

        UpdateAdminStartHint();
        Commit();
        ApplyStartup(allowElevate: true);
    }

    private void AdminStart_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        Services.Config.Behavior.RunAsAdmin = AdminStartSwitch.IsChecked == true;
        Commit();
        ApplyStartup(allowElevate: true);
    }

    /// <summary>
    /// 「关于」页的权限说明 + 概览页的权限提示卡片。
    ///
    /// <para>
    /// 关键在于把两种情况分开说：<b>没开这个功能</b>（正常，普通权限够用）和
    /// <b>开了却没提权成功</b>（异常，要说清楚为什么、怎么重试）。
    /// 以前两种情况共用一句"当前不是管理员身份"，用户明明开了开关，
    /// 看到这句只会觉得"程序没生效"。
    /// </para>
    /// </summary>
    private void RefreshElevationCard()
    {
        if (ElevationCard == null || AboutElevation == null) return;

        var elevated = Services.IsElevated;
        var wantsAdmin = Services.Config.Behavior.RunAsAdmin;

        AboutElevation.Text = elevated
            ? "管理员（可以接管以管理员身份运行的窗口）"
            : wantsAdmin
                ? "普通用户 —— 设置里已开启「以管理员身份运行」，但这次启动没拿到权限"
                : "普通用户（管理员窗口内的滚动不会被增强）";

        /* 页脚那行由 RefreshStats 统一写（它要带版本号），这里不要抢着改 ——
         * 两处格式不一样，先写的会被后写的覆盖，改的人还以为没生效。 */
        ElevationCard.Visibility = elevated ? Visibility.Collapsed : Visibility.Visible;

        ElevationHint.Text = wantsAdmin
            ? "设置里已经开启了「以管理员身份运行」，但这次启动没能拿到管理员权限 —— " +
              "可能是在系统弹出的确认里点了「取消」，或者被安全策略拦下了。" +
              "点下面的按钮重试；如果每次都这样，检查一下系统的 UAC 设置。"
            : "当前不是管理员身份。Windows 不允许普通程序拦截更高权限窗口的输入，" +
              "所以「以管理员身份运行的窗口」里的滚轮不会被增强（任务管理器、部分游戏启动器、安装程序等）。" +
              "下面的按钮会打开「以管理员身份运行」并重启，之后每次启动都保持。";
    }

    /// <summary>
    /// 让两个开关反映**真实状态**。
    ///
    /// <para>
    /// 「开机自启」看系统：注册表项或计划任务任一存在就算开着 ——
    /// 计划任务可能被用户在「任务计划程序」里手动删掉，以系统为准更诚实。
    /// </para>
    /// <para>
    /// 「以管理员身份运行」看**配置意图**，不看计划任务。因为它现在管的是每一次启动，
    /// 手动启动那条路根本不经过计划任务 —— 拿任务存在性来显示，会出现
    /// "明明开着却显示关"（用户报的就是这个）。
    /// 意图与实际不符时，由下面的提示条负责说清楚。
    /// </para>
    /// </summary>
    private void RefreshStartupSwitches()
    {
        var adminTask = AdminStart.IsEnabled();
        var runKey = AutoStart.IsRunKeyEnabled();

        var prev = _loading;
        _loading = true;
        try
        {
            AutoStartSwitch.IsChecked = adminTask || runKey;
            AdminStartSwitch.IsChecked = Services.Config.Behavior.RunAsAdmin;
        }
        finally { _loading = prev; }

        UpdateAdminStartHint();
        UpdateAdminStartBrokenBar(adminTask);
        RefreshElevationCard();
    }

    /// <summary>
    /// 「以管理员身份运行」下面那行说明。两种组合说实话：
    /// 开了开机自启，开机那次是不弹确认的（计划任务）；没开，就只有手动启动才提权。
    /// </summary>
    private void UpdateAdminStartHint()
    {
        if (AdminStartHint == null) return;

        var runAtLogin = AutoStartSwitch.IsChecked == true;
        AdminStartHint.Text = runAtLogin
            ? "每次启动都以管理员身份运行。开机那次由计划任务直接给权限、不弹确认；手动打开时系统会问一次。"
            : "每次手动打开都会请求管理员权限。打开上面的「开机自启」后，开机那次也会是管理员 —— 而且不弹确认。";
    }

    /// <summary>
    /// 设置意图与系统实际不一致时的提示条。三种情况：
    /// <list type="number">
    ///   <item><b>该有任务却没有</b>（开关开着、任务没建成功）→ 提示补建；</item>
    ///   <item><b>任务失效</b>（指向的文件没了 / 指向版本目录）→ 提示重建；</item>
    ///   <item><b>不该有任务却还在</b>（用户关掉了）→ 提示清理，否则下次登录仍会被无声拉起。</item>
    /// </list>
    /// 三者都"看上去一切正常"，所以必须显式说出来。
    /// </summary>
    private void UpdateAdminStartBrokenBar(bool adminTask)
    {
        if (AdminStartBrokenBar == null) return;

        var b = Services.Config.Behavior;
        var wantTask = b.RunAtLogin && b.RunAsAdmin;

        string? message = null;
        var stale = false;

        if (adminTask && !wantTask)
        {
            message = "计划任务还在，但设置里已经关掉了「开机自启 + 以管理员身份运行」—— " +
                      "它会让你下次登录时仍然被拉起来（而且是管理员）。点「立即修复」按当前设置清理。";
        }
        else if (adminTask)
        {
            try { stale = AdminStart.IsStale(); }
            catch { /* COM 查询失败就当没坏，别给用户假警报 */ }

            if (stale)
            {
                var target = AdminStart.TaskTargetPath();
                var missing = string.IsNullOrWhiteSpace(target) || !File.Exists(target);

                /* 两种失效要说清楚是哪一种 —— 用户看到"文件不存在"和
                 * "指向的不是启动器"，能做的事其实不一样。 */
                message = missing
                    ? "开机自启任务已失效：它指向的程序文件已经不存在（" +
                      (string.IsNullOrWhiteSpace(target) ? "任务里没有可执行动作" : target) +
                      "），多半是升级后旧版本目录被清理了。下次开机不会自动启动 —— 点「立即修复」重建它。"
                    : "开机自启任务还在，但指向的不是启动器（" + target +
                      "）—— 这个位置在下次升级时会被清理掉，到时候开机就起不来了。点「立即修复」重建它。";
            }
        }
        else if (wantTask)
        {
            message = "设置里开着「开机自启 + 以管理员身份运行」，但计划任务不存在 —— " +
                      "开机那次不会自动启动（多半是上次建任务时没拿到管理员权限）。点「立即修复」补上。";
        }

        AdminStartBrokenBar.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible;
        if (message != null) AdminStartBrokenText.Text = message;
    }

    /// <summary>
    /// 「立即修复」：按当前设置把系统状态同步回来（建该建的、删不该留的）。
    ///
    /// <para>
    /// 不是简单地"重建任务"——提示条也管"任务多余"那种情况，那种要的是删。
    /// 统一走 <see cref="ApplyStartup"/>，由 <see cref="AutoStart.Apply"/> 去调和两种自启方式。
    /// 写计划任务必须管理员权限：已经管理员就直接做，否则走提权重启流程。
    /// </para>
    /// </summary>
    private void AdminStartFix_Click(object sender, RoutedEventArgs e)
    {
        if (Services.IsElevated)
        {
            ApplyStartup(allowElevate: false);
            return;
        }

        ElevateToApplyStartup();
    }

    /// <summary>
    /// 把两种开机自启方式按配置同步到系统。
    ///
    /// <para>
    /// 涉及计划任务的分支（要建、或者要删已有的）都必须有管理员权限 ——
    /// 注册表 Run 项能写，计划任务不能。没有权限就提权重启自己，
    /// 由新进程带 <c>--apply-startup</c> 回来把这一步做完。
    /// </para>
    /// </summary>
    private void ApplyStartup(bool allowElevate)
    {
        var b = Services.Config.Behavior;

        /* 需要管理员权限的两种情形：
         *   ① 要建任务（开机自启 + 以管理员运行都开着）；
         *   ② 任务已经存在，无论如何都要删一次。
         * 只开「以管理员身份运行」、不开机自启时**不需要**动系统 ——
         * 那时候提权纯属白弹一次确认框（提权是在下次启动时自己做的）。 */
        var needAdmin = AdminStart.IsEnabled() || (b.RunAtLogin && b.RunAsAdmin);

        if (needAdmin && !Services.IsElevated)
        {
            if (allowElevate) ElevateToApplyStartup();
            else RefreshStartupSwitches();
            return;
        }

        if (!AutoStart.Apply(b.RunAtLogin, b.RunAsAdmin, out var error))
        {
            Log.Warn("应用开机自启失败：" + error);
            MessageBox.Show(this, error ?? "设置开机自启失败。", Services.AppTitle,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        RefreshStartupSwitches();
    }

    /// <summary>连续点开关时只弹一次提权确认，避免 UAC 连闪。</summary>
    private bool _elevatePrompted;

    private void ElevateToApplyStartup()
    {
        if (_elevatePrompted) { RefreshStartupSwitches(); return; }
        _elevatePrompted = true;

        var r = MessageBox.Show(this,
            "这一项要写 Windows 计划任务（开机那次由它直接给管理员权限），设置它需要管理员权限。\n\n" +
            "点「确定」后系统会弹出一次确认，请选「是」。程序会自动重启并把设置应用好。",
            Services.AppTitle, MessageBoxButton.OKCancel, MessageBoxImage.Information);

        if (r != MessageBoxResult.OK)
        {
            _elevatePrompted = false;
            RefreshStartupSwitches();
            return;
        }

        /* 带 --apply-startup 但不带 --minimized：重启后把设置窗口打开，
         * 让用户看见开关真的变成"开"，而不是"点完之后一片安静"。
         *
         * 拉起目标、参数转义、防循环标记都交给 App.RelaunchElevated ——
         * 它用的是**稳定入口**（启动器），而不是版本目录里的主程序本体
         * （后者换个版本就丢了"启动器在哪"，自启任务又会指回版本目录）。 */
        if (App.RelaunchElevated("--apply-startup"))
        {
            App.ExitApp();
            return;
        }

        _elevatePrompted = false;
        MessageBox.Show(this, "提权被取消或失败，设置没有生效。", Services.AppTitle,
            MessageBoxButton.OK, MessageBoxImage.Warning);
        RefreshStartupSwitches();
    }

    private void CloseToTray_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Services.Config.Behavior.CloseToTray = CloseToTraySwitch.IsChecked == true;
        Commit();
    }

    private void PauseSelf_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Services.Config.Behavior.PauseWhileSettingsOpen = PauseSelfSwitch.IsChecked == true;
        Commit();
    }

    /* ══════════════ 事件：加速 ══════════════ */

    private void Boost_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Services.Config.Boost.Enabled = BoostSwitch.IsChecked == true;
        Commit();
    }

    private void Threshold_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        Services.Config.Boost.FastThresholdPerSec = Math.Round(e.NewValue);
        // 满速点必须和阈值拉开至少 5 格，否则曲线被压成阶跃（一过阈值就顶格）
        if (SatSlider != null && SatSlider.Value < e.NewValue + 5)
            SatSlider.Value = Math.Min(SatSlider.Maximum, Math.Round(e.NewValue) + 5);
        Commit();
    }

    private void Sat_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        Services.Config.Boost.SaturationPerSec = Math.Round(e.NewValue);
        Commit();
    }

    private void MaxMult_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        Services.Config.Boost.MaxMultiplier = Math.Round(e.NewValue);
        Commit();
    }

    private void FlushReverse_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Services.Config.Boost.FlushOnReverse = FlushReverseSwitch.IsChecked == true;
        Commit();
    }

    private void Invert_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Services.Config.Input.InvertDirection = InvertSwitch.IsChecked == true;
        Commit();
    }

    private void Precision_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Services.Config.Input.EnhancePrecisionInput = PrecisionSwitch.IsChecked == true;
        Commit();
    }

    /* ══════════════ 事件：平滑 ══════════════ */

    private void Smooth_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Services.Config.Smooth.Enabled = SmoothSwitch.IsChecked == true;
        Commit();
    }

    /// <summary>
    /// 「每格滚动格数」：滑块一动就**立刻写进系统设置**，不等保存。
    ///
    /// <para>
    /// 这个值和系统面板里的「每次要滚动的行数」是同一个东西，所以不能只改配置 ——
    /// 用户在系统里看到没变，就会以为开关坏了。软件**不另存一份**：
    /// 配置里已经没有 <c>wheelLines</c> 这个字段，系统就是唯一真相。
    /// </para>
    /// </summary>
    private void WheelLines_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;

        var lines = (int)Math.Round(e.NewValue);

        if (!Core.SystemWheelLines.Set(lines))
        {
            /* 写不进去（组策略拦、或者系统已被设成"一次滚一页"）→ 界面拨回系统真实值，
             * 而不是把滑块留在一个"看起来生效了、其实没生效"的位置。 */
            SyncWheelLinesFromSystem();
            return;
        }

        /* 不调 Commit()：这一项**不在配置里**（系统就是真相），
         * 没有需要保存的东西，也不该因为拖滑块去重烘一遍运行时快照 ——
         * 引擎的任何一个参数都不看这个值。 */
        RefreshWheelLinesLabel();
    }

    /// <summary>
    /// 把「每格滚动格数」从系统读回来 —— 读取、回填滑块、更新标签、决定能不能调，
    /// **只有这一个入口**。
    ///
    /// <para>
    /// 载入界面和"从系统设置面板回来"（<see cref="OnActivated"/>）都走它。
    /// 分开写的话，两边只要有一处漏判（比如"一次一页"只在其中一处处理），
    /// 界面就会出现"滑块停在 1、标签却写着一次一页"这种自相矛盾的状态 ——
    /// 这种不一致比数字错了更让人不敢相信软件。
    /// </para>
    /// </summary>
    private void SyncWheelLinesFromSystem()
    {
        var lines = Core.SystemWheelLines.Current();
        var usable = Core.SystemWheelLines.IsUsable(lines);

        /* 保存并恢复外层状态：LoadFromConfig 里本来就把 _loading 置成 true，
         * 这里若无条件在 finally 里写 false，会把外层的保护提前拆掉 ——
         * 后面几十行赋值就会真的触发 Commit（把半载入状态写进配置）。 */
        var prev = _loading;
        _loading = true;
        try
        {
            if (WheelLinesSlider != null)
            {
                // "一次一页"和"读不到"都不该让滑块假装能调：禁用 + 说明原因
                WheelLinesSlider.IsEnabled = usable;
                WheelLinesSlider.Opacity = usable ? 1.0 : 0.45;
                if (usable) WheelLinesSlider.Value = lines;
            }
        }
        finally { _loading = prev; }

        RefreshWheelLinesLabel();
    }

    /// <summary>标签 + 说明文字。值一律现读系统，不读任何缓存。</summary>
    private void RefreshWheelLinesLabel()
    {
        var lines = Core.SystemWheelLines.Current();
        var usable = Core.SystemWheelLines.IsUsable(lines);

        if (WheelLinesValue != null)
        {
            WheelLinesValue.Text =
                lines == Core.SystemWheelLines.PageMode ? "一次一页"
                : usable ? lines + " 行"
                : "读取不到";
        }

        if (WheelLinesHint == null) return;

        WheelLinesHint.Text =
            lines == Core.SystemWheelLines.PageMode
                ? "系统当前设成「一次滚一页」，没有「行」可调。先在系统鼠标设置里选回「每次滚动以下行数」，回来就能调这里。"
                : usable ? ""
                : "读不到系统当前的滚动行数（可能被组策略锁定），这一项暂时不可用。";
        WheelLinesHint.Visibility = usable ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// 从系统面板回来时把滑块同步过来。
    ///
    /// <para>
    /// 用户在系统设置里改了行数，本程序并不知道 —— 只能在自己重新获得焦点时读一次。
    /// 这就是"双向同步"的另一半：软件 → 系统靠写，系统 → 软件靠这里。
    /// </para>
    /// </summary>
    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        // 窗口还没建好时（构造期间）不要碰控件
        if (WheelLinesSlider != null) SyncWheelLinesFromSystem();
    }

    /// <summary>跳到系统「鼠标」设置页。Win10/11 通用的 ms-settings 协议。</summary>
    private void OpenMouseSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ms-settings:mousetouchpad",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show("打不开系统设置：" + ex.Message + "\n\n可以手动打开：设置 → 蓝牙和其他设备 → 鼠标",
                Services.AppTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void Scope_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Services.Config.Smooth.Scope = ScopeAlways.IsChecked == true
            ? SmoothScope.Always
            : SmoothScope.BoostedOnly;
        Commit();
    }

    private void Theme_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var mode = ThemeLight.IsChecked == true ? Theme.Light
                 : ThemeDark.IsChecked == true ? Theme.Dark
                 : Theme.System;

        Services.Config.Appearance.Theme = mode;

        // 立刻生效，不等配置保存那条链路 —— 切主题如果是"点了过半秒才变"，很像卡了
        Theme.SetMode(mode);
        UpdateThemeHints();

        // 主题跟引擎无关，不必重算派生标签
        Commit(refreshDerived: false);
    }

    /// <summary>
    /// 刷新「外观」那一行右侧的状态文字，并把单选框对齐到配置。
    ///
    /// <para>
    /// 之所以要单独写这么一段：选"跟随系统"时，用户看不到系统当前到底是浅还是深，
    /// 界面上就会变成"我选了跟随系统，但不知道它跟成了什么"。
    /// </para>
    /// </summary>
    private void UpdateThemeHints()
    {
        if (ThemeNow == null || ThemeSystem == null || ThemeLight == null || ThemeDark == null) return;

        var mode = Theme.Normalize(Services.Config.Appearance.Theme);

        var prev = _loading;
        _loading = true;
        try
        {
            ThemeSystem.IsChecked = mode == Theme.System;
            ThemeLight.IsChecked = mode == Theme.Light;
            ThemeDark.IsChecked = mode == Theme.Dark;
        }
        finally { _loading = prev; }

        ThemeNow.Text = mode == Theme.System
            ? $"跟随系统 · 现在是{(Theme.SystemIsLight() ? "浅色" : "深色")}"
            : $"固定{(Theme.IsLightNow ? "浅色" : "深色")}，系统换了也不跟";
    }

    private void AutoFps_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Services.Config.Smooth.AutoFps = AutoFpsSwitch.IsChecked == true;
        Commit();
    }

    private void Fps_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        int fps = 120;
        if (Fps60.IsChecked == true) fps = 60;
        else if (Fps90.IsChecked == true) fps = 90;
        else if (Fps144.IsChecked == true) fps = 144;
        Services.Config.Smooth.TargetFps = fps;
        Commit();
    }

    private void Fpn_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        int fpn = 4;
        if (Fpn2.IsChecked == true) fpn = 2;
        else if (Fpn3.IsChecked == true) fpn = 3;
        else if (Fpn6.IsChecked == true) fpn = 6;
        else if (Fpn8.IsChecked == true) fpn = 8;
        Services.Config.Smooth.FramesPerNotch = fpn;
        Commit();
    }

    private void Glide_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        Services.Config.Smooth.GlideMs = (int)Math.Round(e.NewValue);
        Commit();
    }

    private void Tau_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        Services.Config.Smooth.TailTauMs = Math.Round(e.NewValue);
        Commit();
    }

    private void Backlog_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        Services.Config.Smooth.MaxBacklogNotches = Math.Round(e.NewValue);
        Commit();
    }

    /* ══════════════ 事件：应用名单 ══════════════ */

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Services.Config.Filter.Mode = ModeWhite.IsChecked == true
            ? FilterMode.Whitelist
            : FilterMode.Blacklist;
        Commit();
    }

    private void RefreshApps()
    {
        var apps = Services.Config.Filter.Apps;

        /* 指纹没变就直接返回。
         * 原来这段在**每次**配置变更时都跑（拖滑块一秒几十次），
         * 结果是列表被清空重建几十次：选中项丢失、滚动位置跳回顶部、
         * 还平白产生一堆 UI 对象。 */
        var signature = string.Join('\u0001', apps.Select(a => a.Exe + '\u0002' + a.Note));
        if (signature == _appsSignature) return;
        _appsSignature = signature;

        _rows.Clear();
        foreach (var a in apps.OrderBy(a => a.Exe, StringComparer.OrdinalIgnoreCase))
            _rows.Add(new AppRuleRowData { Exe = a.Exe, Note = string.IsNullOrWhiteSpace(a.Note) ? "（无备注）" : a.Note });

        AppsCount.Text = $"{apps.Count} 项";
        AppsEmpty.Visibility = apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddApp_Click(object sender, RoutedEventArgs e)
    {
        var raw = AddAppBox.Text;
        var name = AppRuleName.Normalize(raw);
        if (name.Length == 0)
        {
            UpdateStatus.Text = "先填一个程序名，例如 chrome.exe";
            return;
        }

        var apps = Services.Config.Filter.Apps;
        if (apps.Any(a => string.Equals(a.Exe, name, StringComparison.OrdinalIgnoreCase)))
        {
            UpdateStatus.Text = $"{name} 已经在名单里了";
            return;
        }

        apps.Add(new AppRule { Exe = name, Note = "手动添加" });
        AddAppBox.Text = "";
        Commit();
        UpdateStatus.Text = $"已加入名单：{name}";
    }

    private void RemoveApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string exe) return;
        var apps = Services.Config.Filter.Apps;
        var hit = apps.FirstOrDefault(a => string.Equals(a.Exe, exe, StringComparison.OrdinalIgnoreCase));
        if (hit == null) return;
        apps.Remove(hit);
        Commit();
        UpdateStatus.Text = $"已从名单移除：{exe}";
    }

    private void TitleRegex_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        Services.Config.Filter.TitleRegex = TitleRegexBox.Text.Trim();
        Commit();
    }

    /// <summary>"从运行中的应用选择"：列当前有可见窗口的进程。</summary>
    private void PickRunning_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = PickRunningBtn };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        IEnumerable<Process> procs;
        try { procs = Process.GetProcesses(); }
        catch { procs = Array.Empty<Process>(); }

        var items = new List<(string Exe, string Title)>();
        foreach (var p in procs)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(p.MainWindowTitle)) continue;
                var exe = AppRuleName.Normalize(p.MainModule?.FileName ?? p.ProcessName);
                if (exe.Length == 0 || !seen.Add(exe)) continue;
                items.Add((exe, p.MainWindowTitle));
            }
            catch { /* 受保护进程读不到 MainModule，跳过 */ }
            finally { p.Dispose(); }
        }

        if (items.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "（没有找到带窗口的程序）", IsEnabled = false });
        }
        else
        {
            foreach (var (exe, title) in items.OrderBy(i => i.Exe).Take(50))
            {
                var mi = new MenuItem { Header = $"{exe}    —    {Trim(title, 46)}" };
                mi.Click += (_, _) =>
                {
                    AddAppBox.Text = exe;
                    AddAppBox.Focus();
                    AddAppBox.CaretIndex = AddAppBox.Text.Length;
                };
                menu.Items.Add(mi);
            }
        }

        menu.IsOpen = true;
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /* ══════════════ 事件：更新 ══════════════ */

    private void AutoCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Services.Config.Update.AutoCheck = AutoCheckSwitch.IsChecked == true;
        Commit();
    }

    private void OpenRepo_Click(object sender, RoutedEventArgs e) => App.OpenExternal(Services.Updater.RepoUrl);
    private void OpenHome_Click(object sender, RoutedEventArgs e) => App.OpenExternal(Services.Updater.RepoUrl);
    private void OpenGitHub_Click(object sender, RoutedEventArgs e)
        => App.OpenExternal($"https://github.com/{Services.Updater.Repo}");
    private void OpenReleases_Click(object sender, RoutedEventArgs e) => App.OpenExternal(Services.Updater.ReleasesUrl);

    /// <summary>把一次检查结果铺到界面上（自动检查与手动检查共用）。</summary>
    public void ShowUpdateInfo(UpdateInfo info, bool fromAutoCheck)
    {
        _info = info;
        CurrentVersionText.Text = $"v{Services.Version}";

        if (info.Newer)
        {
            UpdateStatus.Foreground = B("WarnBrush");
            UpdateStatus.Text = $"发现新版本 v{info.Version}" +
                                (info.Size > 0 ? $"（{Text.HumanSize(info.Size)}）" : "") +
                                (fromAutoCheck ? " —— 点「下载并安装」即可在程序内更新。" : "");
            DownloadBtn.Visibility = Visibility.Visible;
            DownloadBtn.IsEnabled = true;

            if (!string.IsNullOrWhiteSpace(info.Notes))
            {
                NotesTitle.Text = $"v{info.Version} 更新说明";
                NotesBox.Text = info.Notes;
                NotesCard.Visibility = Visibility.Visible;
            }
        }
        else
        {
            UpdateStatus.Foreground = B("TextSecondary");
            UpdateStatus.Text = $"已是最新版本（v{Services.Version}）";
            DownloadBtn.Visibility = Visibility.Collapsed;
            NotesCard.Visibility = Visibility.Collapsed;
        }

        if (info.AuthWarning != null)
        {
            UpdateStatus.Foreground = B("WarnBrush");
            UpdateStatus.Text += "\n" + info.AuthWarning;
        }
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        CheckBtn.IsEnabled = false;
        UpdateProgress.Visibility = Visibility.Collapsed;
        UpdateStatus.Foreground = B("TextSecondary");
        UpdateStatus.Text = "正在检查…";

        try
        {
            var outcome = await Services.Updater.CheckAsync(Services.Version);
            if (!outcome.Ok)
            {
                UpdateStatus.Foreground = B("DangerBrush");
                UpdateStatus.Text = "检查失败：" + outcome.Error;
                return;
            }

            var info = outcome.Info!;
            Services.Config.Update.LastCheckUtc = DateTime.UtcNow.ToString("o");
            Services.Store.Touch(notify: false);
            Services.PendingUpdate = info;
            ShowUpdateInfo(info, fromAutoCheck: false);
        }
        catch (Exception ex)
        {
            UpdateStatus.Foreground = B("DangerBrush");
            UpdateStatus.Text = "检查失败：" + ex.Message;
            Log.Warn("检查更新失败：" + ex.Message);
        }
        finally
        {
            _busy = false;
            CheckBtn.IsEnabled = true;
        }
    }

    private async void DownloadUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _info == null) return;

        if (!Services.Updater.CanSelfUpdate(out var reason))
        {
            UpdateStatus.Foreground = B("DangerBrush");
            UpdateStatus.Text = reason;
            return;
        }

        _busy = true;
        _downloadCts = new CancellationTokenSource();
        DownloadBtn.IsEnabled = false;
        CancelBtn.Visibility = Visibility.Visible;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateProgress.Value = 0;
        RestartBtn.Visibility = Visibility.Collapsed;

        var progress = new Progress<DownloadProgress>(p =>
        {
            if (p.Total > 0) UpdateProgress.Value = p.Percent;
            UpdateStatus.Foreground = B("TextSecondary");
            UpdateStatus.Text = p.Message;
        });

        try
        {
            await Services.Updater.DownloadAsync(_info, progress, _downloadCts.Token);

            UpdateStatus.Text = "校验通过，正在替换程序…";
            Services.Updater.ApplyUpdate();

            UpdateProgress.Value = 100;
            UpdateStatus.Foreground = B("SuccessBrush");
            UpdateStatus.Text = "更新已就位 —— 重启程序即可用上新版本。";
            RestartBtn.Visibility = Visibility.Visible;
            DownloadBtn.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException)
        {
            UpdateStatus.Foreground = B("TextSecondary");
            UpdateStatus.Text = "已取消下载（已下载的部分会保留，下次可以接着下）。";
        }
        catch (Exception ex)
        {
            UpdateStatus.Foreground = B("DangerBrush");
            UpdateStatus.Text = "更新失败：" + ex.Message;
            Log.Warn("更新失败：" + ex.Message);
        }
        finally
        {
            _busy = false;
            DownloadBtn.IsEnabled = true;
            CancelBtn.Visibility = Visibility.Collapsed;
            _downloadCts?.Dispose();
            _downloadCts = null;
        }
    }

    private void CancelDownload_Click(object sender, RoutedEventArgs e)
    {
        try { _downloadCts?.Cancel(); } catch { }
        UpdateStatus.Text = "正在取消…";
    }

    private void Restart_Click(object sender, RoutedEventArgs e) => App.RestartForUpdate();

    /* ══════════════ 事件：令牌 ══════════════ */

    /// <summary>
    /// 事件：令牌 —— 把界面刷成与"当前有没有令牌"一致的状态。
    /// 做成 internal 是给 smoke 用：它要在无人工参与的情况下把界面推到
    /// "已保存令牌"那一态，再截图留证。
    /// </summary>
    internal void RefreshTokenUi()
    {
        var tok = Services.Store.CurrentToken();
        _loading = true;
        try
        {
            if (tok == null)
            {
                // 没保存过：可编辑状态
                PopupCleanup.CloseCurrentToolTip();
                TokenBox.IsEnabled = true;
                TokenBox.Password = "";
                TokenBox.ToolTip = "粘贴令牌后点「保存令牌」，例如 ghp_xxx 或 github_pat_xxx";
                EditTokenBtn.Visibility = Visibility.Collapsed;
                ClearTokenBtn.Visibility = Visibility.Collapsed;
                SaveTokenBtn.IsEnabled = true;
            }
            else
            {
                // 已保存：PasswordBox 没有 IsReadOnly，用禁用 + 一串圆点表示"锁着"
                PopupCleanup.CloseCurrentToolTip();
                TokenBox.IsEnabled = false;
                TokenBox.Password = Text.MaskToken(tok);
                TokenBox.ToolTip = "令牌已加密保存。要换一个请点「更换」。";
                EditTokenBtn.Visibility = Visibility.Visible;
                ClearTokenBtn.Visibility = Visibility.Visible;
                SaveTokenBtn.IsEnabled = false;
            }
        }
        finally { _loading = false; }
    }

    private void EditToken_Click(object sender, RoutedEventArgs e)
    {
        _loading = true;
        TokenBox.IsEnabled = true;
        TokenBox.Password = "";
        _loading = false;
        SaveTokenBtn.IsEnabled = true;
        EditTokenBtn.Visibility = Visibility.Collapsed;
        TokenBox.Focus();
        TokenStatus.Text = "把新的令牌粘进来，点「保存令牌」。";
    }

    private async void SaveToken_Click(object sender, RoutedEventArgs e)
    {
        if (!TokenBox.IsEnabled) return;
        var raw = TokenBox.Password;
        if (Text.SanitizeToken(raw) == null)
        {
            TokenStatus.Foreground = B("DangerBrush");
            TokenStatus.Text = "没填内容 —— 把 github_pat_… 或 ghp_… 粘进来再保存。";
            return;
        }

        Services.Store.SetToken(raw);
        RefreshTokenUi();
        TokenStatus.Foreground = B("TextSecondary");
        TokenStatus.Text = "已保存（DPAPI 加密，只在本机可解）。正在验证…";
        await VerifyCurrentTokenAsync();
    }

    private async void VerifyToken_Click(object sender, RoutedEventArgs e)
    {
        if (TokenBox.IsEnabled)
        {
            // 输入框里是新粘贴的：直接验证这一串，不落盘
            var raw = TokenBox.Password;
            if (Text.SanitizeToken(raw) == null)
            {
                TokenStatus.Foreground = B("DangerBrush");
                TokenStatus.Text = "还没填令牌 —— 粘进来再点验证。";
                return;
            }
            await VerifyAsync(raw);
            return;
        }
        await VerifyCurrentTokenAsync();
    }

    private async Task VerifyCurrentTokenAsync()
    {
        var tok = Services.Store.CurrentToken();
        if (tok == null)
        {
            TokenStatus.Foreground = B("DangerBrush");
            TokenStatus.Text = "还没保存令牌。";
            return;
        }
        await VerifyAsync(tok);
    }

    private async Task VerifyAsync(string token)
    {
        VerifyTokenBtn.IsEnabled = false;
        TokenStatus.Foreground = B("TextSecondary");
        TokenStatus.Text = "正在向 GitHub 验证…";
        try
        {
            var check = await Services.Updater.VerifyTokenAsync(token);
            TokenStatus.Foreground = B("SuccessBrush");
            TokenStatus.Text = "令牌有效：" + check.Display;

            try
            {
                var rate = await Services.Updater.GetRateStateAsync();
                RateText.Text = $"接口额度：本小时已用 {rate.Used} 次 / 共 {rate.Limit} 次" +
                                (rate.Authed ? "（已带令牌）" : "（匿名）");
            }
            catch (Exception ex)
            {
                RateText.Text = "接口额度：查询失败（" + ex.Message + "）";
            }
        }
        catch (Exception ex)
        {
            TokenStatus.Foreground = B("DangerBrush");
            TokenStatus.Text = "令牌无效：" + ex.Message;
        }
        finally { VerifyTokenBtn.IsEnabled = true; }
    }

    private void ClearToken_Click(object sender, RoutedEventArgs e)
    {
        Services.Store.SetToken("");
        RefreshTokenUi();
        TokenStatus.Foreground = B("TextSecondary");
        TokenStatus.Text = "已清除令牌。查更新会走匿名接口（每小时 60 次额度）。";
        RateText.Text = "接口额度：点「验证」可以查看。";
    }

    /* ══════════════ 事件：关于 / 诊断 ══════════════ */

    private void OpenDataDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.DataDir}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { UpdateStatus.Text = "打不开数据目录：" + ex.Message; }
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Log.Info("用户从「关于」页打开了日志");
            if (File.Exists(Log.FilePath))
                Process.Start(new ProcessStartInfo("notepad.exe", $"\"{Log.FilePath}\"") { UseShellExecute = true });
            else
                OpenDataDir_Click(sender, e);
        }
        catch (Exception ex) { UpdateStatus.Text = "打不开日志：" + ex.Message; }
    }

    private void RestartHook_Click(object sender, RoutedEventArgs e)
    {
        RestartHookBtn.IsEnabled = false;
        try
        {
            var ok = Services.Engine.Restart();
            UpdateStatus.Foreground = (Brush)FindResource(ok ? "SuccessBrush" : "DangerBrush");
            UpdateStatus.Text = ok ? "钩子已重启。" : "钩子重启失败，看日志。";
            RefreshStats();
        }
        finally { RestartHookBtn.IsEnabled = true; }
    }

    private void ResetDefaults_Click(object sender, RoutedEventArgs e)
    {
        var r = MessageBox.Show("把除 GitHub 令牌以外的所有设置恢复默认值？", Services.AppTitle,
            MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (r != MessageBoxResult.OK) return;

        Services.Store.ResetToDefaults();
        LoadFromConfig();
        RefreshApps();
        RefreshTokenUi();
        RedrawCurve();
        UpdateStatus.Foreground = B("SuccessBrush");
        UpdateStatus.Text = "已恢复默认设置。";
    }

    private void ExitApp_Click(object sender, RoutedEventArgs e) => App.ExitApp();

    /// <summary>
    /// 「权限提示」卡片上的按钮：以管理员身份重启。
    ///
    /// <para>
    /// 顺手把设置里的「以管理员身份运行」打开 —— 用户点这个按钮的意图就是
    /// "我要管理员"，只提这一次权的话，下次打开软件又变回普通权限，
    /// 他会再撞一次同样的墙（这次报障就是这么来的）。
    /// </para>
    /// </summary>
    private void RestartElevated_Click(object sender, RoutedEventArgs e)
    {
        if (!Services.Config.Behavior.RunAsAdmin)
        {
            Services.Config.Behavior.RunAsAdmin = true;
            Commit();
            RefreshStartupSwitches();
        }

        if (App.RelaunchElevated())
        {
            App.ExitApp();
            return;
        }

        MessageBox.Show("提权被取消或失败，本次仍是普通权限。", Services.AppTitle,
            MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /* ══════════════ 关闭 ══════════════ */

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        Services.Store.SaveNow();

        /* 窗口要没了，先把弹层都收掉。
         *
         * 提示气泡和右键菜单底层是 Popup，各自有独立的 HwndSource。
         * 窗口在它们还开着的时候被销毁/隐藏，那两个 HwndSource 就会处于
         * "内容已经脱离、但根 Visual 还挂着"的半吊子状态 ——
         * 之后任何一次布局更新都可能在这个状态下炸出
         * "VisualTarget 的根 Visual 不能具有父级"。
         *
         * 而且这个异常是从 NotifyIcon 的原生 WndProc 里冒出去的
         * （异常通道见 App.OnWinFormsThreadException 的说明），
         * 以前的结果是：用户看到框架默认弹窗，日志里一片空白。 */
        CloseOpenPopups();

        if (_allowClose) return; // 真正销毁这条路径，放手让 WPF 关

        if (!Services.Config.Behavior.CloseToTray)
        {
            App.ExitApp();
            return;
        }

        e.Cancel = true;
        Hide();
        Services.Tray?.ShowHintOnce();

        /* ⚠ 退到托盘之后要把窗口**真正销毁**，不能只是 Hide()。
         *
         * 实测：仅托盘常驻约 70 MB，而"打开过设置窗口"之后是 197 MB ——
         * 因为 WPF 的视觉树、D3D 表面、全部控件都还挂在这个隐藏窗口上，
         * 一直占到进程退出。用户点一下 × 只是把它藏起来，那 120 多 MB 就白占了。
         *
         * 这里排到 ApplicationIdle 再关（正在 Closing 里直接 Close 会重入），
         * 并且先确认它还是隐藏状态（用户可能已经又点开了托盘）。
         * 下次从托盘打开时 App.ShowMainWindow 会重新建一个，界面上看不出来。 */
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (IsVisible || _allowClose) return;
            _allowClose = true;
            Close();
        }), DispatcherPriority.ApplicationIdle);
    }

    /// <summary>
    /// 把当前开着的所有弹层（ToolTip / ContextMenu）收掉。
    ///
    /// <para>
    /// 窗口马上要没了的时候必须先做这件事 —— 理由见 <see cref="OnClosing"/> 里的注释。
    /// </para>
    /// </summary>
    private void CloseOpenPopups()
    {
        try
        {
            var n = PopupCleanup.CloseAllIn(this);
            if (n > 0) Log.Info($"关闭了 {n} 个弹层");
        }
        catch (Exception ex)
        {
            Log.Warn("清理弹层失败：" + ex.Message);
        }

        try { PopupCleanup.CloseCurrentToolTip(); }
        catch { /* 关不掉提示气泡无害 */ }
    }
}

/// <summary>应用名单列表里的一行（给 DataTemplate 绑定用，必须是可反射访问的类型）。</summary>
internal sealed class AppRuleRowData
{
    public string Exe { get; init; } = "";
    public string Note { get; init; } = "";
}
