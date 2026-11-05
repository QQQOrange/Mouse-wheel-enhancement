using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using IOPath = System.IO.Path;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
        var bootSw = Stopwatch.StartNew();
        InitializeComponent();
        var initMs = bootSw.ElapsedMilliseconds;

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
        RestoreWindowPlacement();

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

        Log.Info($"主窗口构造完成：XAML {initMs} ms / 总计 {bootSw.ElapsedMilliseconds} ms");

        /* 小屏兜底：默认 1120×830 在 1366×768 的笔记本上底部会被切掉
         * （底栏和关于页最后一排按钮都在屏幕外）。第一次打开时按工作区收一下，
         * 之后用户自己拉过的尺寸由 RestoreWindowPlacement 负责。 */
        try
        {
            var wa = SystemParameters.WorkArea;
            if (Width > wa.Width - 24) Width = Math.Max(MinWidth, wa.Width - 24);
            if (Height > wa.Height - 24) Height = Math.Max(MinHeight, wa.Height - 24);
        }
        catch { /* 取不到工作区就按 XAML 的默认值 */ }
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
            FlickPageSwitch.IsChecked = c.Boost.FlickPage;
        FineModeSwitch.IsChecked = c.Behavior.FineMode;
        RestoreLinesSlider.Value = c.Behavior.FineRestoreLines;
        UpdateRestoreLinesLabel();
            HorizontalSwitch.IsChecked = c.Input.HorizontalBoost;
            OsdSwitch.IsChecked = c.Behavior.OsdMultiplier;
            WatchdogSwitch.IsChecked = c.Behavior.HookWatchdog;

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

    /// <summary>「最近识别的程序」：每次刷新重建最多 6 行（数量很小，重建比增量维护简单可靠）。</summary>
    private void RefreshRecentTargets()
    {
        var panel = RecentPanel;
        if (panel == null) return;

        var rows = WheelEngine.RecentTargets();
        if (rows.Count == 0)
        {
            panel.Children.Clear();
            var empty = new TextBlock
            {
                Text = "还没识别到程序 —— 在别的窗口里滚一下就会出现。",
                Opacity = 0.6,
                Margin = new Thickness(0, 2, 0, 0),
            };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            empty.SetResourceReference(TextBlock.FontSizeProperty, 12.0);
            panel.Children.Add(empty);
            return;
        }

        var key = string.Join(";", rows.Select(r => r.Exe + r.LastTicks));
        if (key == _recentKey) return;      // 内容没变就不重建，避免每帧重建 UI
        _recentKey = key;

        panel.Children.Clear();
        foreach (var r in rows.Take(6))
        {
            var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel();
            var name = new TextBlock { Text = r.Exe, FontWeight = FontWeights.SemiBold, FontSize = 13 };
            name.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            var cls = new TextBlock { Text = r.Class, FontSize = 11, Opacity = 0.65 };
            cls.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            left.Children.Add(name);
            left.Children.Add(cls);
            grid.Children.Add(left);

            var right = new TextBlock
            {
                Text = $"{r.Semantics} · {r.Events} 次" + (r.Boosted > 0 ? $"（加速 {r.Boosted}）" : ""),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
            };
            right.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);

            panel.Children.Add(grid);
        }
    }

    private string _recentKey = "";

    private void RefreshStats()
    {
        RefreshRecentTargets();
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
        var barValue = Math.Min(40.0, Math.Max(0.0, rate));
        // 值变化时用 260ms 补间，进度条不会每 400ms 硬跳一下
        if (Math.Abs(_rateBarTarget - barValue) > 0.3)
        {
            _rateBarTarget = barValue;
            RateBar.BeginAnimation(ProgressBar.ValueProperty,
                new DoubleAnimation(barValue, TimeSpan.FromMilliseconds(260))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                });
        }

        var on = Services.Config.Enabled;
        _loading = true;
        MasterSwitch.IsChecked = on;
        _loading = false;

        StatusText.Text = !on ? "已暂停" : running ? "已启用" : "钩子异常";
        StatusDot.Fill = B(!on ? "TextMuted" : running ? "SuccessBrush" : "DangerBrush");
        HookHint.Text = !on
            ? "总开关关闭 · 完全走系统默认"
            : running
                ? $"钩子正常 · 已接管 {e.BoostedCount} 次"
                : "钩子未启动，滚动不受影响";
        var today = $"本次已接管 {e.BoostedCount} 次 · 放行 {e.PassedCount} 次";
        if (StatusToday.Text != today) StatusToday.Text = today;

        /* 有临时提示正在显示时不要覆盖：RefreshStats 每 400ms 跑一次，
         * 否则"诊断包已导出"会被立刻刷回版本号，用户根本看不见。 */
        if (!_footerHintOn) FooterRight.Text = FooterInfo();
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

        AnimateTabIn();
    }

    /// <summary>
    /// 切页时让新内容轻轻淡入并上移 6px —— 比"啪"一下整页换掉自然。
    ///
    /// <para>
    /// 只用 Opacity + TranslateTransform 两个合成层属性，不触发布局，
    /// 160~190 ms 也不会拖慢操作；构造/恢复页签（窗口还没显示）时不播。
    /// </para>
    /// </summary>
    private void AnimateTabIn()
    {
        if (!IsLoaded) return;
        if (Nav.SelectedContent is not FrameworkElement content) return;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        content.Opacity = 0;

        var shift = new TranslateTransform(0, 6);
        content.RenderTransform = shift;

        content.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(165)) { EasingFunction = ease });
        shift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(190)) { EasingFunction = ease });
    }

    /// <summary>
    /// 标题栏的深浅色按钮：**三态循环** —— 跟随系统 → 固定浅色 → 固定深色 → 跟随系统。
    ///
    /// <para>
    /// 以前只做浅↔深两态：点一下就变成"固定"，而"恢复跟随系统"只能去
    /// 概览 → 外观里找 —— 用户点了标题栏按钮之后就"回不去跟随"了。
    /// 现在三态闭环，图标和 ToolTip 都跟着当前状态走，下一次点会发生什么一目了然。
    /// </para>
    /// </summary>
    private void ThemeBtn_Click(object sender, RoutedEventArgs e)
    {
        var cur = Theme.Normalize(Services.Config.Appearance.Theme);
        var next = cur == Theme.System ? Theme.Light
                 : cur == Theme.Light ? Theme.Dark
                 : Theme.System;

        Services.Config.Appearance.Theme = next;
        Theme.SetMode(next);
        UpdateThemeHints();
        Commit(refreshDerived: false);

        // 换掉 ToolTip 之前先把这个按钮的提示气泡收掉。
        // 用户就是悬停在按钮上点它的，此时气泡正开着；
        // 直接换字符串会触发 WPF 去重挂正在显示的 Popup，
        // 那是"根 Visual 不能具有父级"最容易冒头的时机。
        PopupCleanup.CloseCurrentToolTip();
        RefreshThemeButton();
    }

    /// <summary>标题栏主题按钮的图标与提示，跟着当前模式走。</summary>
    private void RefreshThemeButton()
    {
        if (ThemeBtn == null || ThemeGlyph == null) return;

        var mode = Theme.Normalize(Services.Config.Appearance.Theme);
        var (glyph, tip) = mode switch
        {
            Theme.Light => ("\uE706", "固定浅色 · 点击切到固定深色（再点一次回到跟随系统）"),
            Theme.Dark => ("\uE708", "固定深色 · 点击回到跟随系统"),
            _ => ("\uE793", $"跟随系统（现在是{(Theme.IsLightNow ? "浅色" : "深色")}）· 点击切到固定浅色"),
        };
        ThemeGlyph.Text = glyph;
        ThemeBtn.ToolTip = tip;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    /// <summary>关闭按钮：走正常的 Closing 流程（默认收进托盘，可在设置里改成真退出）。</summary>
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>帮助页的页签序号（0 概览 / 1 加速 / 2 平滑 / 3 应用 / 4 更新 / 5 帮助 / 6 关于）。</summary>
    internal const int HelpTabIndex = 5;

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        // 窗口还没建好时（构造期间）不要碰控件
        if (WheelLinesSlider != null) SyncWheelLinesFromSystem();
    }

    private void SetFooterHint(string text) => SetFooterHint(text, true);

    /// <summary>同上，但可以标出这是"成功"还是"失败"（只影响一行字的颜色，不弹窗）。</summary>
    private void SetFooterHint(string text, bool success)
    {
        if (FooterRight == null) return;

        _footerHintOn = true;
        FooterRight.Text = text;
        /* 用 SetResourceReference 而不是赋 Brush 对象：换主题时它自己会重新取值，
         * 否则深色的成功绿会留在浅色底上（对比度 1.9:1，几乎看不见）。 */
        FooterRight.SetResourceReference(TextBlock.ForegroundProperty,
            success ? "TextSecondary" : "DangerBrush");

        _footerHintTimer?.Stop();
        _footerHintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _footerHintTimer.Tick += (_, _) =>
        {
            _footerHintTimer?.Stop();
            _footerHintOn = false;
            if (FooterRight != null)
            {
                FooterRight.Text = FooterInfo();
                FooterRight.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            }
        };
        _footerHintTimer.Start();
    }

    /// <summary>
    /// 手感曲线预设（v1.8.1）：只动「收尾时间常数」和「惯性长度」两个滑块。
    ///
    /// <para>
    /// 这两个决定了"收尾有多黏"，是普通用户唯一能听出差别的一对参数；
    /// 阈值、满速点、摊帧数保持不动 —— 预设覆盖太多项会让人搞不清是谁在起作用。
    /// </para>
    /// </summary>
    private void CurvePreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string key) return;

        var (tau, glide) = key switch
        {
            "crisp" => (20.0, 120.0),
            "long" => (45.0, 400.0),
            "trackpad" => (16.0, 600.0),
            _ => (30.0, 300.0),
        };

        if (key == "trackpad-plus")
        {
            /* 一步到位：手感参数 + 细腻模式。
             * 细腻模式会改系统设置，所以走的是用户已经点过的那条路（FineModeSwitch 的 Checked 事件），
             * 包括它的确认弹窗和还原机制 —— 这里不重复实现。 */
            TauSlider.Value = 16;
            GlideSlider.Value = 600;
            if (FineModeSwitch.IsChecked != true) FineModeSwitch.IsChecked = true;
            SetFooterHint("已应用「触控板级」：16ms 跟手 + 600ms 惯性 + 细腻模式");
            return;
        }

        TauSlider.Value = tau;
        GlideSlider.Value = glide;
        SetFooterHint($"已应用「{b.Content}」手感");
    }

    private void ApplyOverride(AppRule rule, Action<AppOverride> mutate, bool clear = false)
    {
        if (clear)
        {
            rule.Override = null;
        }
        else
        {
            rule.Override ??= new AppOverride();
            mutate(rule.Override);
            // 两项都空 → 当成没设，配置文件更干净
            if (rule.Override.MaxMultiplier == null && rule.Override.Smooth == null)
                rule.Override = null;
        }

        Services.Store.Touch();
        RefreshApps();
    }

    private void AddApp_Click(object sender, RoutedEventArgs e)
    {
        var raw = AddAppBox.Text;
        var name = AppRuleName.Normalize(raw);
        if (name.Length == 0)
        {
            /* 提示写在**底栏**（常驻、切页也看得见）。
             * 以前写进「更新」页的 UpdateStatus —— 用户在名单页点「添加」，
             * 屏幕上不会有任何变化，只能怀疑按钮坏了。 */
            SetFooterHint("先填一个程序名，例如 chrome.exe");
            return;
        }

        var apps = Services.Config.Filter.Apps;
        if (apps.Any(a => string.Equals(a.Exe, name, StringComparison.OrdinalIgnoreCase)))
        {
            SetFooterHint($"{name} 已经在名单里了");
            return;
        }

        apps.Add(new AppRule { Exe = name, Note = "手动添加" });
        AddAppBox.Text = "";
        Commit();
        SetFooterHint($"已加入名单：{name}");
    }

    private void RemoveApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string exe) return;
        var apps = Services.Config.Filter.Apps;
        var hit = apps.FirstOrDefault(a => string.Equals(a.Exe, exe, StringComparison.OrdinalIgnoreCase));
        if (hit == null) return;
        apps.Remove(hit);
        Commit();
        SetFooterHint($"已从名单移除：{exe}");
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
        catch (Exception ex) { SetFooterHint("打不开数据目录：" + ex.Message); }
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
        catch (Exception ex) { SetFooterHint("打不开日志：" + ex.Message); }
    }

    private void RestartHook_Click(object sender, RoutedEventArgs e)
    {
        RestartHookBtn.IsEnabled = false;
        try
        {
            var ok = Services.Engine.Restart();
            SetFooterHint(ok ? "钩子已重启。" : "钩子重启失败，看日志。", ok);
            RefreshStats();
        }
        finally { RestartHookBtn.IsEnabled = true; }
    }

    private void ResetDefaults_Click(object sender, RoutedEventArgs e)
    {
        var r = MessageBox.Show(this, "把除 GitHub 令牌以外的所有设置恢复默认值？", Services.AppTitle,
            MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (r != MessageBoxResult.OK) return;

        Services.Store.ResetToDefaults();
        LoadFromConfig();
        RefreshApps();
        RefreshTokenUi();
        RedrawCurve();
        SetFooterHint("已恢复默认设置。");
    }

    private void ExitApp_Click(object sender, RoutedEventArgs e)
    {
        /* 误点"退出"就等于把增强整个关掉（托盘图标也会消失），
         * 而它就挨着"恢复默认设置"，值得问一句。 */
        var r = MessageBox.Show(this,
            "退出后滚轮增强会停止，托盘图标也会消失。\r\n确定要退出吗？",
            Services.AppTitle, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (r != MessageBoxResult.Yes) return;
        _allowClose = true;
        App.ExitApp();
    }

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

    /* ══════════════ 窗口位置记忆 ══════════════ */

    /// <summary>
    /// 恢复上次的窗口大小/位置/最大化状态，以及停留的页签。
    ///
    /// <para>
    /// 只在"记录过且还落在虚拟屏内"时才恢复：换了显示器、拔了副屏、
    /// 或者分辨率变小之后，旧坐标可能完全在屏幕外 —— 那种情况下宁可回默认居中，
    /// 也不要让窗口"打开了但看不见"。
    /// </para>
    /// </summary>
    private void RestoreWindowPlacement()
    {
        var a = Services.Config.Appearance;

        if (a.WindowWidth >= MinWidth && a.WindowHeight >= MinHeight
            && a.WindowWidth <= 10000 && a.WindowHeight <= 10000)
        {
            var vl = SystemParameters.VirtualScreenLeft;
            var vt = SystemParameters.VirtualScreenTop;
            var vr = vl + SystemParameters.VirtualScreenWidth;
            var vb = vt + SystemParameters.VirtualScreenHeight;

            // 至少留 120×80 在虚拟屏内，才算"看得见"
            var visible = a.WindowLeft + a.WindowWidth > vl + 120
                       && a.WindowLeft < vr - 120
                       && a.WindowTop + 80 > vt
                       && a.WindowTop < vb - 80;

            if (visible)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Width = a.WindowWidth;
                Height = a.WindowHeight;
                Left = a.WindowLeft;
                Top = a.WindowTop;
            }
        }

        if (a.WindowMaximized) WindowState = WindowState.Maximized;

        if (a.LastTab >= 0 && a.LastTab < Nav.Items.Count) SelectTab(a.LastTab);
    }

    /// <summary>关闭前记下窗口大小/位置/最大化与当前页签（随后由 OnClosing 的 SaveNow 落盘）。</summary>
    private void SaveWindowPlacement()
    {
        try
        {
            var a = Services.Config.Appearance;

            /* 最大化时 Left/Top/Width/Height 是最大化后的值，要恢复的是 RestoreBounds
             * （还原尺寸）。正常状态直接用当前值。 */
            var bounds = WindowState == WindowState.Normal
                ? new Rect(Left, Top,
                    ActualWidth > 0 ? ActualWidth : Width,
                    ActualHeight > 0 ? ActualHeight : Height)
                : RestoreBounds;

            if (bounds.Width >= MinWidth && bounds.Height >= MinHeight
                && !double.IsNaN(bounds.Left) && !double.IsNaN(bounds.Top)
                && !double.IsInfinity(bounds.Left) && !double.IsInfinity(bounds.Top))
            {
                a.WindowWidth = bounds.Width;
                a.WindowHeight = bounds.Height;
                a.WindowLeft = bounds.Left;
                a.WindowTop = bounds.Top;
            }

            a.WindowMaximized = WindowState == WindowState.Maximized;
            a.LastTab = Math.Max(0, Nav.SelectedIndex);
        }
        catch (Exception ex)
        {
            // 位置记忆只是体验优化，失败不该影响关闭
            Log.Warn("保存窗口位置失败：" + ex.Message);
        }
    }

    /* ══════════════ 关闭 ══════════════ */

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        void Mark(string label) => Log.Info($"关闭·{label}：{sw.ElapsedMilliseconds} ms");

        /* 定时器会把自己挂在 Dispatcher 的计时器表上 —— 只要它还开着，
         * 这个窗口就**收不掉**（关闭到托盘是靠销毁窗口来还内存的）。
         * 6 秒的底部提示、180ms 的搜索防抖都顺手停掉。 */
        _footerHintTimer?.Stop();
        _footerHintTimer = null;
        _helpFilterTimer?.Stop();
        _helpFilterTimer = null;

        SaveWindowPlacement();
        Mark("保存窗口位置");

        Services.Store.SaveNow();
        Mark("保存配置");

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
        Mark("关闭弹层");

        if (_allowClose) return; // 真正销毁这条路径，放手让 WPF 关

        if (!Services.Config.Behavior.CloseToTray)
        {
            /* 真退出这条路径也要先放行，和托盘路径（下面 _allowClose = true）对称：
             * ExitApp 内部会 Shutdown，而 Shutdown 会再尝试关一次这个窗口，
             * 那时 OnClosing 里的保存/弹层清理会被重复执行。 */
            _allowClose = true;
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

    /// <summary>覆盖摘要（v1.8.0），例如"· 上限 3× · 平滑关"。空串 = 完全跟随全局。</summary>
    public string Override { get; init; } = "";
}
