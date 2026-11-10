using System;
using System.Windows.Threading;
using MouseWheelEnhancement.Core;

namespace MouseWheelEnhancement;

/// <summary>
/// 设置窗口的「实时输入监视」。
///
/// <para>
/// 复用 <see cref="WheelEngine.TraceLine"/>：平时钩子不拼这个字符串（零分配），
/// 只有用户在这里打开开关时才置 <see cref="WheelEngine.TraceEnabled"/>。
/// </para>
/// </summary>
public partial class MainWindow
{
    private DispatcherTimer? _inputMonitorTimer;

    private void InputMonitor_Toggled(object sender, System.Windows.RoutedEventArgs e)
    {
        if (InputMonitorSwitch.IsChecked == true)
        {
            WheelEngine.TraceEnabled = true;
            WheelEngine.TraceLine = "";
            InputMonitorText.Text = "监视中…把鼠标放到目标窗口上滚一下。";

            _inputMonitorTimer ??= new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(350),
            };
            _inputMonitorTimer.Tick -= InputMonitor_Tick;
            _inputMonitorTimer.Tick += InputMonitor_Tick;
            _inputMonitorTimer.Start();
        }
        else
        {
            StopInputMonitor();
        }
    }

    private void InputMonitor_Tick(object? sender, EventArgs e)
    {
        if (InputMonitorText == null) return;
        var line = WheelEngine.TraceLine;
        InputMonitorText.Text = string.IsNullOrEmpty(line)
            ? "监视中…还没有滚轮事件。"
            : line;
    }

    private void StopInputMonitor()
    {
        try { _inputMonitorTimer?.Stop(); } catch { }
        WheelEngine.TraceEnabled = false;
        if (InputMonitorText != null)
            InputMonitorText.Text = "已关闭。打开开关后在这里滚动一下。";
        if (InputMonitorSwitch != null)
            InputMonitorSwitch.IsChecked = false;
    }
}
