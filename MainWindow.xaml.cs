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
        AboutElevation.Text = Services.IsElevated
            ? "管理员（可以接管以管理员身份运行的窗口）"
            : "普通用户（管理员窗口内的滚动不会被增强）";
        ElevationCard.Visibility = Services.IsElevated ? Visibility.Collapsed : Visibility.Visible;
        FooterRight.Text = Services.IsElevated ? "管理员模式" : "普通权限";

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
        Closed += (_, _) =>
        {
            Services.StateChanged -= OnServicesStateChanged;
            _tick.Stop();

            /* 窗口（连同视觉树、D3D 表面、字形缓存）刚被销毁，此刻是把工作集
             * 还给系统的唯一合适时机。放在窗口自己身上而不是创建方，是为了
             * "谁打开的就谁负责还" —— 任何路径销毁窗口都会走到这里。 */
            MemoryTrim.TrimIfLarge();
        };
        Loaded += (_, _) => ApplyDarkTitleBar();
    }

    /// <summary>
    /// 构建日期。用 exe 文件自身的修改时间，而不是 Assembly.Location ——
    /// 单文件发布时后者恒为空串（IL3000）。
    /// </summary>
    /// <summary>取画刷（带缓存）。</summary>
    private Brush B(string key)
    {
        if (_brushes.TryGetValue(key, out var b)) return b;
        b = (Brush)FindResource(key);
        _brushes[key] = b;
        return b;
    }

    private static string BuildStamp()
    {
        try
        {
            var exe = UpdateService.ExePath;
            return File.Exists(exe) ? File.GetLastWriteTime(exe).ToString("yyyy-MM-dd") : "local";
        }
        catch { return "local"; }
    }

    /// <summary>让标题栏跟着深色界面走（Win10 1809+ 支持，取不到就算了）。</summary>
    private void ApplyDarkTitleBar()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            var on = 1;
            // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE；老 build 上是 19
            if (Native.DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
                Native.DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
        }
        catch { /* 非关键美化 */ }
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
            LatencySlider.Value = c.Smooth.MaxLatencyMs;
            BacklogSlider.Value = c.Smooth.MaxBacklogNotches;

            ModeBlack.IsChecked = c.Filter.Mode == FilterMode.Blacklist;
            ModeWhite.IsChecked = c.Filter.Mode == FilterMode.Whitelist;
            TitleRegexBox.Text = c.Filter.TitleRegex;

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
            CurveHint == null || TauValue == null || LatencyValue == null ||
            BacklogValue == null || FpsValue == null || FpsManualGroup == null ||
            RegexHint == null)
            return;

        var c = Services.Config;
        // 一律按整格显示：格数带小数点既不好读也不好调
        ThresholdValue.Text = $"{c.Boost.FastThresholdPerSec:0} 格/秒";
        SatValue.Text = $"{c.Boost.SaturationPerSec:0} 格/秒";
        MaxMultValue.Text = $"{c.Boost.MaxMultiplier:0}×";
        CurveHint.Text = $"拨 1 格 → 滚 {c.Boost.MaxMultiplier:0} 格";
        TauValue.Text = $"{c.Smooth.TailTauMs:0} ms";
        LatencyValue.Text = $"{c.Smooth.MaxLatencyMs:0} ms";
        BacklogValue.Text = $"{c.Smooth.MaxBacklogNotches:0} 格";

        // 自动时显示显示器实际刷新率（刷新率一变这里会跟着变），手动时显示选定值
        FpsValue.Text = c.Smooth.AutoFps
            ? RefreshRateMonitor.Describe()
            : $"手动 · {c.Smooth.TargetFps} Hz（{1000.0 / c.Smooth.TargetFps:0.00} ms/帧）";
        FpsManualGroup.IsEnabled = !c.Smooth.AutoFps;
        FpsManualGroup.Opacity = c.Smooth.AutoFps ? 0.45 : 1.0;

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
        StatusPill.Background = (Brush)FindResource(!on ? "CardBg2" : running ? "AccentFaint" : "CardBg2");

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
        var gridBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x2C, 0x39));
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
                Stroke = new SolidColorBrush(Color.FromArgb(0x66, 0x5B, 0x8C, 0xFF)),
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
            Fill = new SolidColorBrush(Color.FromArgb(0x22, 0x5B, 0x8C, 0xFF)),
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

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedIndex == HelpTabIndex) EnsureHelpRendered();
        if (Nav.SelectedIndex == 1) InvalidateCurve();
    }

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
        // 父开关一关，从属项自动失效（Normalize 里也会强制拉平，这里先让界面立刻对）
        if (!on) Services.Config.Behavior.RunAtLoginAsAdmin = false;

        UpdateAdminStartRowEnabled();
        Commit();
        ApplyStartup(allowElevate: true);
    }

    private void AdminStart_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var want = AdminStartSwitch.IsChecked == true;
        if (want && AutoStartSwitch.IsChecked != true)
        {
            // 父开关没开，这一项不成立（界面上已禁用，这里是第二道保险）
            var prev = _loading;
            _loading = true;
            AdminStartSwitch.IsChecked = false;
            _loading = prev;
            return;
        }

        Services.Config.Behavior.RunAtLoginAsAdmin = want;
        Commit();
        ApplyStartup(allowElevate: true);
    }

    /// <summary>
    /// 让两个开关反映**系统的真实状态**，而不是配置文件写的值。
    /// 计划任务可能被用户在「任务计划程序」里手动删掉 —— 那种情况下以系统为准更诚实。
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
            AdminStartSwitch.IsChecked = adminTask;
        }
        finally { _loading = prev; }

        UpdateAdminStartRowEnabled();
    }

    /// <summary>「以管理员身份启动」从属于「开机自启」，父开关关着就整行变灰。</summary>
    private void UpdateAdminStartRowEnabled()
    {
        if (AdminStartRow == null) return;

        var on = AutoStartSwitch.IsChecked == true;
        AdminStartRow.IsEnabled = on;
        AdminStartRow.Opacity = on ? 1.0 : 0.45;
        AdminStartSwitch.IsEnabled = on;
        AdminStartHint.Text = on
            ? "登录后直接以管理员运行，对管理员程序的窗口也生效（普通权限会被系统拦住）。Windows 只会弹一次确认。"
            : "先打开上面的「开机自启」，这一项才有意义。";
    }

    /// <summary>
    /// 把「开机自启」的实际形态落到系统里。
    ///
    /// <para>
    /// 涉及计划任务的两个分支（要建、或者要删已有的）都必须有管理员权限 ——
    /// 注册表 Run 项能写，计划任务不能。没有权限就提权重启自己，
    /// 由新进程带 <c>--apply-startup</c> 回来把这一步做完。
    /// </para>
    /// </summary>
    private void ApplyStartup(bool allowElevate)
    {
        var b = Services.Config.Behavior;
        var needAdmin = b.RunAtLoginAsAdmin || AdminStart.IsEnabled();

        if (needAdmin && !Services.IsElevated)
        {
            if (allowElevate) ElevateToApplyStartup();
            else RefreshStartupSwitches();
            return;
        }

        if (!AutoStart.Apply(b.RunAtLogin, b.RunAtLoginAsAdmin, out var error))
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
            "「以管理员身份启动」要靠 Windows 计划任务来实现，设置它需要管理员权限。\n\n" +
            "点「确定」后系统会弹出一次确认，请选「是」。程序会自动重启，把设置应用好。",
            Services.AppTitle, MessageBoxButton.OKCancel, MessageBoxImage.Information);

        if (r != MessageBoxResult.OK)
        {
            _elevatePrompted = false;
            RefreshStartupSwitches();
            return;
        }

        try
        {
            // 带 --apply-startup 但不带 --minimized：重启后把设置窗口打开，
            // 让用户看见开关真的变成"开"，而不是"点完之后一片安静"。
            var psi = new ProcessStartInfo(UpdateService.ExePath, "--apply-startup")
            {
                UseShellExecute = true,
                Verb = "runas",
            };
            Process.Start(psi);
            App.ExitApp();
        }
        catch (Exception ex)
        {
            _elevatePrompted = false;
            Log.Warn("提权被取消或失败：" + ex.Message);
            MessageBox.Show(this, "提权被取消，设置没有生效。", Services.AppTitle,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            RefreshStartupSwitches();
        }
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

    private void Scope_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Services.Config.Smooth.Scope = ScopeAlways.IsChecked == true
            ? SmoothScope.Always
            : SmoothScope.BoostedOnly;
        Commit();
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

    private void Tau_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        Services.Config.Smooth.TailTauMs = Math.Round(e.NewValue);
        Commit();
    }

    private void Latency_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        Services.Config.Smooth.MaxLatencyMs = Math.Round(e.NewValue);
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

    private void RefreshTokenUi()
    {
        var tok = Services.Store.CurrentToken();
        _loading = true;
        try
        {
            if (tok == null)
            {
                TokenBox.IsReadOnly = false;
                TokenBox.Text = "";
                TokenBox.ToolTip = "粘贴令牌后点「保存令牌」，例如 ghp_xxx 或 github_pat_xxx";
                EditTokenBtn.Visibility = Visibility.Collapsed;
                ClearTokenBtn.Visibility = Visibility.Collapsed;
                SaveTokenBtn.IsEnabled = true;
            }
            else
            {
                TokenBox.IsReadOnly = true;
                TokenBox.Text = Text.MaskToken(tok);
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
        TokenBox.IsReadOnly = false;
        TokenBox.Text = "";
        _loading = false;
        SaveTokenBtn.IsEnabled = true;
        EditTokenBtn.Visibility = Visibility.Collapsed;
        TokenBox.Focus();
        TokenStatus.Text = "把新的令牌粘进来，点「保存令牌」。";
    }

    private async void SaveToken_Click(object sender, RoutedEventArgs e)
    {
        if (TokenBox.IsReadOnly) return;
        var raw = TokenBox.Text;
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
        if (!TokenBox.IsReadOnly)
        {
            // 输入框里是新粘贴的：直接验证这一串，不落盘
            var raw = TokenBox.Text;
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

    private void RestartElevated_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var exe = UpdateService.ExePath;
            var psi = new ProcessStartInfo(exe, "--minimized") { UseShellExecute = true, Verb = "runas" };
            Process.Start(psi);
            App.ExitApp();
        }
        catch (Exception ex)
        {
            MessageBox.Show("提权失败（可能点了取消）：" + ex.Message, Services.AppTitle,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /* ══════════════ 关闭 ══════════════ */

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        Services.Store.SaveNow();

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
}

/// <summary>应用名单列表里的一行（给 DataTemplate 绑定用，必须是可反射访问的类型）。</summary>
internal sealed class AppRuleRowData
{
    public string Exe { get; init; } = "";
    public string Note { get; init; } = "";
}
