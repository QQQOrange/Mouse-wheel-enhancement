using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MouseWheelEnhancement.Core;

namespace MouseWheelEnhancement.UI;

/// <summary>
/// 「加速」页。独立成 UserControl 后，主窗口在第一次切到该页时才构造它；
/// 快速滚动加速开关、速度 → 倍率曲线、触发阈值 / 满速点 / 倍率上限、
/// 反向滚动纠正 / 甩动翻页 / 细腻模式 / 还原行数 / 反转方向 / 高精度输入
/// 都在这里维护。
///
/// <para>
/// 页面内部的 <c>_syncing</c> 对应主窗口的 <c>_loading</c>：每个 <c>*_Changed</c>
/// 都是"读控件 → 写 <c>Services.Config</c> → <c>_host.Commit()</c>"，程序化回填时
/// 必须整条跳过，否则载入配置会被控件事件反向写回去。
/// </para>
/// </summary>
public partial class AccelerationPage : UserControl
{
    private readonly MainWindow _host;

    /// <summary>
    /// 程序化回填控件时置位，防止 Checked/ValueChanged 反向写回配置。
    /// 初值必须是 true：XAML 解析时给 Slider 设 Minimum/Maximum 就会触发 ValueChanged，
    /// 而那时同一个页面里后面的控件还没建出来。
    /// </summary>
    private bool _syncing = true;

    /// <summary>曲线重绘合并标记：拖动滑块时一轮布局只重画一次。</summary>
    private bool _curvePending;

    public AccelerationPage(MainWindow host)
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
            BoostSwitch.IsChecked = c.Boost.Enabled;
            ThresholdSlider.Value = c.Boost.FastThresholdPerSec;
            SatSlider.Value = c.Boost.SaturationPerSec;
            MaxMultSlider.Value = c.Boost.MaxMultiplier;
            FlushReverseSwitch.IsChecked = c.Boost.FlushOnReverse;
            FlickPageSwitch.IsChecked = c.Boost.FlickPage;
            FineModeSwitch.IsChecked = c.Behavior.FineMode;
            RestoreLinesSlider.Value = c.Behavior.FineRestoreLines;
            InvertSwitch.IsChecked = c.Input.InvertDirection;
            PrecisionSwitch.IsChecked = c.Input.EnhancePrecisionInput;
        }
        finally { _syncing = false; }

        RefreshLabels();
        InvalidateCurve();
    }

    /// <summary>只更新"显示出来的数字"，不碰控件值（拖动时逐帧调它）。</summary>
    internal void RefreshLabels()
    {
        if (ThresholdValue == null || SatValue == null || MaxMultValue == null ||
            CurveHint == null || RestoreLinesValue == null || RestoreLinesSlider == null)
            return;

        var c = Services.Config;
        // 一律按整格显示：格数带小数点既不好读也不好调
        ThresholdValue.Text = $"{c.Boost.FastThresholdPerSec:0} 格/秒";
        SatValue.Text = $"{c.Boost.SaturationPerSec:0} 格/秒";
        MaxMultValue.Text = $"{c.Boost.MaxMultiplier:0}×";
        CurveHint.Text = $"拨 1 格 → 滚 {c.Boost.MaxMultiplier:0} 格";

        UpdateRestoreLinesLabel();
    }

    /* ══════════════ 事件：加速 ══════════════ */

    private void Boost_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        Services.Config.Boost.Enabled = BoostSwitch.IsChecked == true;
        _host.Commit();
    }

    private void Threshold_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing) return;
        Services.Config.Boost.FastThresholdPerSec = Math.Round(e.NewValue);
        // 满速点必须和阈值拉开至少 5 格，否则曲线被压成阶跃（一过阈值就顶格）
        if (SatSlider != null && SatSlider.Value < e.NewValue + 5)
            SatSlider.Value = Math.Min(SatSlider.Maximum, Math.Round(e.NewValue) + 5);
        _host.Commit();
    }

    private void Sat_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing) return;
        Services.Config.Boost.SaturationPerSec = Math.Round(e.NewValue);
        _host.Commit();
    }

    private void MaxMult_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing) return;
        Services.Config.Boost.MaxMultiplier = Math.Round(e.NewValue);
        _host.Commit();
    }

    private void FlushReverse_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        Services.Config.Boost.FlushOnReverse = FlushReverseSwitch.IsChecked == true;
        _host.Commit();
    }

    private void Invert_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        Services.Config.Input.InvertDirection = InvertSwitch.IsChecked == true;
        _host.Commit();
    }

    private void Precision_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        Services.Config.Input.EnhancePrecisionInput = PrecisionSwitch.IsChecked == true;
        _host.Commit();
    }

    /* ══════════════ 事件：细腻模式 / 甩动翻页 ══════════════ */

    /// <summary>
    /// 「细腻模式」会改系统设置，所以开关走确认弹窗；用户取消时把 Switch 拨回去，
    /// 但用 <c>_syncing</c> 挡住这次回拨触发的 Unchecked 事件，
    /// 不让它写进配置（配置本来就还是 false）。
    /// </summary>
    private void FineMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;

        var on = FineModeSwitch.IsChecked == true;
        if (on && !Services.Config.Behavior.FineMode)
        {
            var answer = MessageBox.Show(_host,
                "细腻模式会在运行期间把系统「每次滚动几行」改成 1 行，退出本程序时还原。\n\n" +
                "· 量化更细（0.5px → 0.17px），尾巴更顺；\n" +
                "· 这期间所有滚动都由本程序转发；\n" +
                "· 少数不读系统行数的程序可能滚得偏快，关掉这个开关即可恢复。\n\n" +
                "确定开启吗？",
                "细腻模式", MessageBoxButton.OKCancel, MessageBoxImage.Information);
            if (answer != MessageBoxResult.OK)
            {
                _syncing = true;
                FineModeSwitch.IsChecked = false;
                _syncing = false;
                return;
            }
        }

        Services.Config.Behavior.FineMode = on;
        _host.Commit();
    }

    private void RestoreLines_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing) return;
        Services.Config.Behavior.FineRestoreLines = (int)Math.Round(RestoreLinesSlider.Value);
        SystemWheelLines.RestoreTarget = Services.Config.Behavior.FineRestoreLines;
        UpdateRestoreLinesLabel();
        _host.Commit(refreshDerived: false);
    }

    private void UpdateRestoreLinesLabel()
    {
        var v = (int)Math.Round(RestoreLinesSlider.Value);
        RestoreLinesValue.Text = v <= 0 ? "原值" : v + " 行";
    }

    private void FlickPage_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        Services.Config.Boost.FlickPage = FlickPageSwitch.IsChecked == true;
        Services.Config.Behavior.FineMode = FineModeSwitch.IsChecked == true;
        _host.Commit();
    }

    /// <summary>
    /// 「平滑滚动」页的「触控板级」预设用：把本页的「细腻模式」开关打开
    /// （等同于用户自己点一下，包括确认弹窗和还原机制）。
    /// </summary>
    internal void EnableFineModeFromPreset()
    {
        if (FineModeSwitch.IsChecked != true) FineModeSwitch.IsChecked = true;
    }

    /* ══════════════ 倍率曲线预览 ══════════════ */

    /// <summary>smoke 用：曲线画布的实际尺寸与元素数（页面没建时主窗口不读）。</summary>
    internal string CurveSizeText => $"{CurveCanvas?.ActualWidth ?? 0:0}x{CurveCanvas?.ActualHeight ?? 0:0}";

    /// <summary>smoke 用：曲线画布里的元素数。</summary>
    internal int CurveChildCount => CurveCanvas?.Children.Count ?? 0;

    private void CurveCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => InvalidateCurve();

    /// <summary>
    /// 请求重绘曲线：同一轮消息里多次请求只真正画一次。
    /// 拖滑块时 ValueChanged 一秒能来几十次，每次都重建 121 个点的 Polyline
    /// 会把 UI 线程占满（拖动明显发涩）。
    /// </summary>
    internal void InvalidateCurve()
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
        var gridBrush = _host.B("CurveGrid");
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
                Foreground = _host.B("TextMuted"),
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
                Stroke = _host.B("CurveGuide"),
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
            Fill = _host.B("CurveArea"),
        });

        CurveCanvas.Children.Add(new Polyline
        {
            Points = pts,
            Stroke = _host.B("AccentBrush"),
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
                Foreground = _host.B("TextMuted"),
            };
            Canvas.SetLeft(lbl, Math.Min(X(rate) - 6, left + pw - 12));
            Canvas.SetTop(lbl, top + ph + 3);
            CurveCanvas.Children.Add(lbl);
        }

        var axis = new TextBlock
        {
            Text = "格/秒",
            FontSize = 9.5,
            Foreground = _host.B("TextMuted"),
        };
        Canvas.SetLeft(axis, left + pw - 26);
        Canvas.SetTop(axis, top + ph + 3);
        CurveCanvas.Children.Add(axis);
    }
}
