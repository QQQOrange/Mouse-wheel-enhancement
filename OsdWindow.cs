using System;
using MouseWheelEnhancement.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace MouseWheelEnhancement;

/// <summary>
/// 快速滚动时在光标旁边闪一下"当前倍率"（v1.8.0）。
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
/// <para>
/// 位置贴在光标右下 18px；碰到屏幕边缘会自动翻到另一侧。
/// </para>
/// </summary>
internal sealed class OsdWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private readonly TextBlock _label;
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
        Opacity = 0;

        _label = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            Margin = new Thickness(10, 4, 10, 4),
        };

        Content = new Border
        {
            CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x1E, 0x22, 0x2C)),
            Child = _label,
            Effect = null,
        };

        _hide = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(340) };
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
        _label.Text = $"{multiplier:0.0}×";
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
        Fade(1, 90, null);
        _hide.Start();
    }

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

        var x = cursorX + 18.0;
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
