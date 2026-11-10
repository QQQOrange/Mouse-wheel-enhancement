using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MouseWheelEnhancement.Core;

namespace MouseWheelEnhancement.UI;

/// <summary>
/// 「平滑滚动」页。独立成 UserControl 后，主窗口在第一次切到该页时才构造它；
/// 手感曲线预设、跟手曲线预览、每格滚动格数（系统设置的唯一真相）、
/// 施加范围 / 注入帧率 / 每格拆分、τ / 惯性 / 积压和「细腻模式」预设联动都在这里维护。
///
/// <para>
/// 页面内部的 <c>_syncing</c> 对应主窗口的 <c>_loading</c>：每个 <c>*_Changed</c>
/// 都是"读控件 → 写 <c>Services.Config</c> → <c>_host.Commit()</c>"，程序化回填时
/// 必须整条跳过，否则载入配置会被控件事件反向写回去。
/// </para>
/// </summary>
public partial class SmoothPage : UserControl
{
    private readonly MainWindow _host;

    /// <summary>
    /// 程序化回填控件时置位，防止 Checked/ValueChanged 反向写回配置。
    /// 初值必须是 true：XAML 解析时给 Slider 设 Minimum/Maximum 就会触发 ValueChanged，
    /// 而那时同一个页面里后面的控件还没建出来。
    /// </summary>
    private bool _syncing = true;

    /// <summary>当前选中的手感预设按钮；手动拖 τ / 惯性后清掉选中态。</summary>
    private Button? _activePreset;

    /// <summary>跟手曲线预览的重绘合并标记：拖动滑块时一轮布局只重画一次。</summary>
    private bool _previewPending;

    public SmoothPage(MainWindow host)
    {
        _host = host;
        InitializeComponent();

        SyncFromConfig();
        UiText.Apply(this);
    }

    /* ══════════════ 配置 → 界面 ══════════════ */

    /// <summary>把当前配置回填到本页控件（主窗口导入/重置/外部变更后调用）。</summary>
    internal void SyncFromConfig()
    {
        var c = Services.Config;
        _syncing = true;
        try
        {
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
            AutoFpsSwitch.IsChecked = c.Smooth.AutoFps;
        }
        finally { _syncing = false; }

        UpdateCurveSummary();
        RefreshLabels();
    }

    /// <summary>只更新"显示出来的数字"，不碰控件值（拖动时逐帧调它）。</summary>
    internal void RefreshLabels()
    {
        if (TauValue == null || GlideValue == null || GlideSlider == null ||
            BacklogValue == null || FpsValue == null || FpsHint == null ||
            FpsManualGroup == null || GlideHint == null)
            return;

        var c = Services.Config;
        TauValue.Text = $"{c.Smooth.TailTauMs:0} ms";
        GlideValue.Text = c.Smooth.GlideMs <= 0 ? "关闭" : $"{c.Smooth.GlideMs:0} ms";

        /* 这两项是有依赖的：滑行只能把"松手时还没发完的量"慢慢放出来。
         * 惯性长度太小 → 一次滚动两三帧就发完了 → 松手时手里是空的 → 滑行形同虚设。
         * 用户不会自己想到这一层，直接在界面上指出来。 */
        var clash = c.Smooth.GlideMs > 0 && c.Smooth.TailTauMs < 20;
        GlideHint.Visibility = clash ? Visibility.Visible : Visibility.Collapsed;
        GlideHint.Text = clash
            ? $"「惯性长度」只有 {c.Smooth.TailTauMs:0} ms，一次滚动在几帧内就发完了，" +
              "松手时手里没有剩余量，这一项滑不起来 —— 把它调到 30 以上才会有尾巴。"
            : "";

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
    }

    /* ══════════════ 「每格滚动格数」：系统设置的唯一真相 ══════════════ */

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
        if (_syncing) return;

        var lines = (int)Math.Round(e.NewValue);

        if (!SystemWheelLines.Set(lines))
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
    /// 载入界面和"从系统设置面板回来"（主窗口的 OnActivated）都走它。
    /// 分开写的话，两边只要有一处漏判（比如"一次一页"只在其中一处处理），
    /// 界面就会出现"滑块停在 1、标签却写着一次一页"这种自相矛盾的状态 ——
    /// 这种不一致比数字错了更让人不敢相信软件。
    /// </para>
    /// </summary>
    internal void SyncWheelLinesFromSystem()
    {
        var lines = SystemWheelLines.Current();
        var usable = SystemWheelLines.IsUsable(lines);

        /* 保存并恢复外层状态：SyncFromConfig 里本来就把 _syncing 置成 true，
         * 这里若无条件在 finally 里写 false，会把外层的保护提前拆掉 ——
         * 后面几十行赋值就会真的触发 Commit（把半载入状态写进配置）。 */
        var prev = _syncing;
        _syncing = true;
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
        finally { _syncing = prev; }

        RefreshWheelLinesLabel();
    }

    /// <summary>标签 + 说明文字。值一律现读系统，不读任何缓存。</summary>
    private void RefreshWheelLinesLabel()
    {
        var lines = SystemWheelLines.Current();
        var usable = SystemWheelLines.IsUsable(lines);

        if (WheelLinesValue != null)
        {
            WheelLinesValue.Text =
                lines == SystemWheelLines.PageMode ? "一次一页"
                : usable ? lines + " 行"
                : "读取不到";
        }

        if (WheelLinesHint == null) return;

        WheelLinesHint.Text =
            lines == SystemWheelLines.PageMode
                ? "系统当前设成「一次滚一页」，没有「行」可调。先在系统鼠标设置里选回「每次滚动以下行数」，回来就能调这里。"
                : usable ? ""
                : "读不到系统当前的滚动行数（可能被组策略锁定），这一项暂时不可用。";
        WheelLinesHint.Visibility = usable ? Visibility.Collapsed : Visibility.Visible;
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

    /* ══════════════ 事件：平滑参数 ══════════════ */

    private void Smooth_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        Services.Config.Smooth.Enabled = SmoothSwitch.IsChecked == true;
        _host.Commit();
    }

    private void Scope_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        Services.Config.Smooth.Scope = ScopeAlways.IsChecked == true
            ? SmoothScope.Always
            : SmoothScope.BoostedOnly;
        _host.Commit();
    }

    private void AutoFps_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        Services.Config.Smooth.AutoFps = AutoFpsSwitch.IsChecked == true;
        _host.Commit();
    }

    private void Fps_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        int fps = 120;
        if (Fps60.IsChecked == true) fps = 60;
        else if (Fps90.IsChecked == true) fps = 90;
        else if (Fps144.IsChecked == true) fps = 144;
        Services.Config.Smooth.TargetFps = fps;
        _host.Commit();
    }

    private void Fpn_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        int fpn = 4;
        if (Fpn2.IsChecked == true) fpn = 2;
        else if (Fpn3.IsChecked == true) fpn = 3;
        else if (Fpn6.IsChecked == true) fpn = 6;
        else if (Fpn8.IsChecked == true) fpn = 8;
        Services.Config.Smooth.FramesPerNotch = fpn;
        _host.Commit();
    }

    private void Glide_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing) return;
        Services.Config.Smooth.GlideMs = (int)Math.Round(e.NewValue);
        ClearPresetSelection();
        UpdateCurveSummary();
        _host.Commit();
    }

    private void Tau_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing) return;
        Services.Config.Smooth.TailTauMs = Math.Round(e.NewValue);
        ClearPresetSelection();
        UpdateCurveSummary();
        _host.Commit();
    }

    private void Backlog_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing) return;
        Services.Config.Smooth.MaxBacklogNotches = Math.Round(e.NewValue);
        _host.Commit();
    }

    private void AdvancedBacklog_Click(object sender, RoutedEventArgs e)
    {
        if (AdvancedBacklogPanel == null || AdvancedBacklogToggle == null) return;
        var show = AdvancedBacklogPanel.Visibility != Visibility.Visible;
        AdvancedBacklogPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        AdvancedBacklogToggle.Content = show ? "收起：积压上限" : "高级参数：积压上限";
    }

    /// <summary>「手感曲线」卡片下面那行当前值 + 曲线预览。</summary>
    private void UpdateCurveSummary()
    {
        if (CurveSummary == null || TauSlider == null || GlideSlider == null) return;
        var glide = GlideSlider.Value <= 0 ? "关闭" : $"{GlideSlider.Value:0} ms";
        CurveSummary.Text = $"当前：跟手 {TauSlider.Value:0} ms · 惯性 {glide}";
        InvalidateSmoothPreview();
    }

    /* ══════════════ 手感曲线预设 ══════════════ */

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
            "crisp" => (30.0, 100.0),
            "long" => (60.0, 450.0),
            /* 实测触控板手势：120 单位 ≈ 100ms 的连续推进，约 8px/帧（160Hz）。
             * τ=45ms 配合速度域二阶低通，单格正好落在 90~110ms；16ms 会在
             * 40~50ms 内冲完，首帧跳到半格，是"和触控板相去甚远"的主因。 */
            "trackpad" => (45.0, 300.0),
            _ => (40.0, 320.0),
        };

        if (key == "trackpad-plus")
        {
            /* 一步到位：实测触控板曲线 + 细腻模式。
             * 细腻模式的开关属于「加速」页，走宿主那个入口（和用户自己点开关
             * 完全同一条路），包括它的确认弹窗和还原机制 —— 这里不重复实现。 */
            TauSlider.Value = 45;
            GlideSlider.Value = 300;
            _host.EnableFineModeFromPreset();
            MarkPreset(b);
            _host.SetFooterHint("已应用「触控板级」：45ms 跟手 + 300ms 惯性 + 细腻模式");
            return;
        }

        TauSlider.Value = tau;
        GlideSlider.Value = glide;
        MarkPreset(b);
        _host.SetFooterHint($"已应用「{b.Content}」手感");
    }

    /* ── 预设选中态 ── */

    private void MarkPreset(Button selected)
    {
        if (!ReferenceEquals(_activePreset, selected)) ClearPresetSelection();
        _activePreset = selected;
        selected.SetResourceReference(Control.BackgroundProperty, "AccentSoft");
        selected.SetResourceReference(Control.BorderBrushProperty, "AccentSoftBorder");
        selected.FontWeight = FontWeights.SemiBold;
    }

    private void ClearPresetSelection()
    {
        if (_activePreset == null) return;
        _activePreset.ClearValue(Control.BackgroundProperty);
        _activePreset.ClearValue(Control.BorderBrushProperty);
        _activePreset.FontWeight = FontWeights.Normal;
        _activePreset = null;
    }

    /* ══════════════ 跟手曲线预览 ══════════════ */

    private void SmoothPreview_SizeChanged(object sender, SizeChangedEventArgs e) => InvalidateSmoothPreview();

    /// <summary>请求重绘预览；同一轮消息里多次请求只真正画一次（拖滑块时一秒几十次）。</summary>
    internal void InvalidateSmoothPreview()
    {
        if (_previewPending || SmoothPreview == null) return;
        _previewPending = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _previewPending = false;
            DrawSmoothPreview();
        }), DispatcherPriority.Background);
    }

    private void DrawSmoothPreview()
    {
        if (SmoothPreview == null || TauSlider == null) return;
        var w = SmoothPreview.ActualWidth;
        var h = SmoothPreview.ActualHeight;
        if (w < 40 || h < 30) return;

        SmoothPreview.Children.Clear();
        var tau = Math.Max(1.0, TauSlider.Value);
        var span = Math.Max(180.0, tau * 4);
        var left = 12.0;
        var right = 44.0;
        var top = 10.0;
        var bottom = 18.0;
        var pw = Math.Max(10.0, w - left - right);
        var ph = Math.Max(10.0, h - top - bottom);

        double X(double ms) => left + pw * Math.Clamp(ms / span, 0, 1);
        double Y(double v) => top + ph * (1 - Math.Clamp(v, 0, 1));

        for (var i = 0; i <= 3; i++)
        {
            var y = top + ph * i / 3.0;
            SmoothPreview.Children.Add(new Line
            {
                X1 = left, X2 = left + pw, Y1 = y, Y2 = y,
                Stroke = _host.B("CurveGrid"), StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 2, 3 },
            });
        }

        var pts = new PointCollection();
        const int steps = 80;
        for (var i = 0; i <= steps; i++)
        {
            var t = span * i / steps;
            pts.Add(new Point(X(t), Y(1 - Math.Exp(-t / tau))));
        }
        var area = new PointCollection(pts)
        {
            new Point(X(span), top + ph),
            new Point(X(0), top + ph),
        };
        SmoothPreview.Children.Add(new Polygon { Points = area, Fill = _host.B("AccentSoft") });
        SmoothPreview.Children.Add(new Polyline
        {
            Points = pts, Stroke = _host.B("AccentBrush"), StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
        });

        var mx = X(tau);
        SmoothPreview.Children.Add(new Line
        {
            X1 = mx, X2 = mx, Y1 = top, Y2 = top + ph,
            Stroke = _host.B("CurveGuide"), StrokeThickness = 1,
        });

        void Label(string text, double x, double y, double width = 40)
        {
            var lbl = new TextBlock { Text = text, FontSize = 9.5, Foreground = _host.B("TextMuted"), Width = width };
            Canvas.SetLeft(lbl, x);
            Canvas.SetTop(lbl, y);
            SmoothPreview.Children.Add(lbl);
        }

        Label("τ", mx - 4, 1);
        Label($"0", left, top + ph + 3);
        Label($"{span:0} ms", left + pw - 32, top + ph + 3, 40);
        Label("位置", left + pw + 4, top - 2, 38);
    }
}
