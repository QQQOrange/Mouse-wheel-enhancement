using System;
using MouseWheelEnhancement.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace MouseWheelEnhancement;

/// <summary>
/// 快速滚动时在光标旁边闪一下"当前倍率"（v1.8.0，v1.8.19 重做外观）。
///
/// <para>
/// 三条硬要求，缺一条就会帮倒忙：
/// </para>
/// <list type="bullet">
///   <item><b>不抢焦点</b>：<c>ShowActivated=false</c> + <c>WS_EX_NOACTIVATE</c>，
///         否则正在打字的光标会丢；</item>
///   <item><b>点不中</b>：<c>WS_EX_TRANSPARENT</c> + 全透明背景，鼠标直接穿过去；</item>
///   <item><b>不闪</b>：只在倍率 ≥ 1.5 时出现，且同一时刻只有一个实例，
///         连续快滚时更新数字而不是反复新建窗口。</item>
/// </list>
///
/// <para>
/// # 外观（v1.8.19）
/// </para>
/// <para>
/// 旧版是一个固定深色的方角小方块，浅色主题下像贴了块黑胶布，深色主题下又和背景糊在一起。
/// 现在改成跟随主题的**胶囊标签**：圆角 + 1px 描边 + 一层很淡的投影 + 左侧一条强调色竖条，
/// 数字用主题前景色、乘号用次要色。所有颜色都走 <c>DynamicResource</c>，
/// 所以切换浅色/深色（含"跟随系统"）会立刻跟着变 —— 不需要重建窗口。
/// </para>
/// <para>
/// 位置贴在光标右下 16px；碰到屏幕边缘会自动翻到另一侧。
/// </para>
/// </summary>
internal sealed class OsdWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    /// <summary>停留多久开始淡出。太短读不到，太长挡视线。</summary>
    private const int HoldMs = 520;

    private readonly Border _card;
    private readonly TextBlock _value;
    private readonly Run _times;
    private readonly ScaleTransform _scale;
    private readonly DispatcherTimer _hide;

    /// <summary>显示代次：每次 Flash 自增，旧淡出动画的收尾回调据此作废。</summary>
    private int _generation;

    public OsdWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        IsHitTestVisible = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        Opacity = 0;

        /* 数字和乘号放在**同一个 TextBlock 的两段 Run** 里：
         * 分成两个 TextBlock 时基线对不齐，「×」会飘成上标的样子（v1.8.19 修）。 */
        _value = new TextBlock
        {
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _value.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        _value.Inlines.Add(new Run("1.0"));
        _times = new Run("×") { FontSize = 12 };
        _times.SetResourceReference(TextElement.ForegroundProperty, "TextSecondary");
        _value.Inlines.Add(_times);

        /* 左侧那条 3px 的强调色：给"这是在加速"一个颜色锚点，
         * 比旧版整块深色更容易被余光捕捉到。 */
        var accent = new Border
        {
            Width = 3,
            Height = 16,
            CornerRadius = new CornerRadius(1.5),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        accent.SetResourceReference(Border.BackgroundProperty, "AccentBrush");

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(accent);
        row.Children.Add(_value);

        _card = new Border
        {
            CornerRadius = new CornerRadius(15),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(11, 5, 13, 5),
            Child = row,
            Effect = new DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 2,
                Direction = 270,
                Opacity = 0.30,
                Color = Colors.Black,
            },
        };
        _card.SetResourceReference(Border.BackgroundProperty, "CardBg");
        _card.SetResourceReference(Border.BorderBrushProperty, "BorderBrushSoft");

        _scale = new ScaleTransform(1, 1);
        _card.RenderTransform = _scale;
        _card.RenderTransformOrigin = new Point(0.5, 0.5);

        Content = _card;

        _hide = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HoldMs) };
        _hide.Tick += (_, _) =>
        {
            _hide.Stop();

            /* 记下当前代次：淡出期间如果又来了新的 Flash（连续快滚），
             * 代次会变，这次收尾就作废 —— 否则会把刚亮起来的 OSD 藏掉。 */
            var gen = _generation;
            Fade(0, 180, () =>
            {
                /* 先判代次再判可见：窗口可能已经被关闭（退出时 OSD 恰好还亮着），
                 * 对已关闭窗口调 Hide() 会抛 InvalidOperationException。 */
                if (gen == _generation && Opacity <= 0.01 && IsVisible) Hide();
            });
        };

        /* 关窗后必须把定时器停掉：否则它还会在已关闭的窗口上跑一次收尾。 */
        Closed += (_, _) => _hide.Stop();

        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            var ex = Native.GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(h, GWL_EXSTYLE,
                (IntPtr)(ex | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
        };
    }

    /// <summary>显示一次。已经显示时只更新文字并续命，不新建窗口。</summary>
    public void Flash(double multiplier)
    {
        if (_value.Inlines.FirstInline is Run number) number.Text = $"{multiplier:0.0}";
        _generation++;

        if (!IsVisible)
        {
            /* 先 Show 再定位：Show 之前 ActualWidth/ActualHeight 还是 0，
             * 贴边判断会按兜底尺寸算，OSD 可能压到屏幕边缘外。 */
            Show();
            UpdateLayout();
            PlaceNearCursor();
        }
        else
        {
            PlaceNearCursor();
        }

        _hide.Stop();

        /* 每次出现轻轻"弹"一下（0.94 → 1），比纯淡入更有存在感，
         * 但幅度很小，不会在余光里变成闪烁。 */
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, Pop());
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, Pop());
        Fade(1, 90, null);
        _hide.Start();
    }

    private static DoubleAnimation Pop() => new(1, TimeSpan.FromMilliseconds(110))
    {
        From = 0.94,
        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
    };

    private void Fade(double to, int ms, Action? done)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        if (done != null) a.Completed += (_, _) => done();
        BeginAnimation(OpacityProperty, a);
    }

    private void PlaceNearCursor()
    {
        if (!Native.GetCursorPos(out var p)) return;

        /* ⚠ 坐标系陷阱：GetCursorPos 给的是**物理像素**，
         * 而 WPF 的 Left/Top 是**设备无关单位（DIP）**。
         * 在 200% 缩放的机器上直接把物理值赋给 Left，窗口会跑到两倍远的地方；
         * 贴边判断也会因为拿物理值和 DIP 值比较而失效。这里统一换算成 DIP。 */
        var dpi = VisualTreeHelper.GetDpi(this);
        var cursorX = p.X / (dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX);
        var cursorY = p.Y / (dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY);

        UpdateLayout();
        var w = ActualWidth > 1 ? ActualWidth : 64;
        var h = ActualHeight > 1 ? ActualHeight : 28;

        var x = cursorX + 16.0;
        var y = cursorY + 18.0;

        // 虚拟桌面边界也是 DIP，和 Left/Top 同一套单位
        var left = SystemParameters.VirtualScreenLeft;
        var top = SystemParameters.VirtualScreenTop;
        var right = left + SystemParameters.VirtualScreenWidth;
        var bottom = top + SystemParameters.VirtualScreenHeight;

        if (x + w > right) x = cursorX - w - 12;
        if (y + h > bottom) y = cursorY - h - 12;
        if (x < left) x = left;
        if (y < top) y = top;

        Left = x;
        Top = y;
    }
}
