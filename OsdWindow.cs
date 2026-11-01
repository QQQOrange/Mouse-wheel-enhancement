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
            Fade(0, 180, () => { if (Opacity <= 0.01) Hide(); });
        };

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

        if (!IsVisible)
        {
            PlaceNearCursor();
            Show();
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

        UpdateLayout();
        var w = ActualWidth > 1 ? ActualWidth : 64;
        var h = ActualHeight > 1 ? ActualHeight : 28;

        var x = p.X + 18.0;
        var y = p.Y + 18.0;

        // 碰边就翻到另一侧，别让 OSD 跑出屏幕
        var vw = SystemParameters.VirtualScreenWidth + SystemParameters.VirtualScreenLeft;
        var vh = SystemParameters.VirtualScreenHeight + SystemParameters.VirtualScreenTop;
        if (x + w > vw) x = p.X - w - 12;
        if (y + h > vh) y = p.Y - h - 12;
        if (x < SystemParameters.VirtualScreenLeft) x = SystemParameters.VirtualScreenLeft;
        if (y < SystemParameters.VirtualScreenTop) y = SystemParameters.VirtualScreenTop;

        Left = x;
        Top = y;
    }
}
