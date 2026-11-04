using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MouseWheelEnhancement.Core;

namespace MouseWheelEnhancement;

/// <summary>
/// 设置窗口的「配置 ↔ 控件」双向绑定（v1.8.28 从 MainWindow.xaml.cs 拆出来）。
///
/// <para>
/// 这里每个 <c>*_Changed</c> 都是同一条流水线：读控件 → 写 <c>Services.Config</c> → <c>Commit()</c>。
/// 它们最容易写错的地方是"程序化赋值触发回调"，所以每个方法第一行都是 <c>if (_loading) return;</c> ——
/// 这条约定集中在这个文件里，比散落在 1700 行里安全。
/// </para>
/// </summary>
public partial class MainWindow
{
    private void Master_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Services.SetEnabled(MasterSwitch.IsChecked == true);
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

    private void FineMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var on = FineModeSwitch.IsChecked == true;
        if (on && !Services.Config.Behavior.FineMode)
        {
            var answer = MessageBox.Show(this,
                "细腻模式会在运行期间把系统「每次滚动几行」改成 1 行，退出本程序时还原。\n\n" +
                "· 量化更细（0.5px → 0.17px），尾巴更顺；\n" +
                "· 这期间所有滚动都由本程序转发；\n" +
                "· 少数不读系统行数的程序可能滚得偏快，关掉这个开关即可恢复。\n\n" +
                "确定开启吗？",
                "细腻模式", MessageBoxButton.OKCancel, MessageBoxImage.Information);
            if (answer != MessageBoxResult.OK)
            {
                _loading = true;
                FineModeSwitch.IsChecked = false;
                _loading = false;
                return;
            }
        }

        Services.Config.Behavior.FineMode = on;
        Commit();
    }

    private void FlickPage_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Services.Config.Boost.FlickPage = FlickPageSwitch.IsChecked == true;
        Services.Config.Behavior.FineMode = FineModeSwitch.IsChecked == true;
        Commit();
    }

    private void Horizontal_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Services.Config.Input.HorizontalBoost = HorizontalSwitch.IsChecked == true;
        Commit();
    }

    private void Osd_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Services.Config.Behavior.OsdMultiplier = OsdSwitch.IsChecked == true;
        Commit();
    }

    private void Watchdog_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Services.Config.Behavior.HookWatchdog = WatchdogSwitch.IsChecked == true;
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

        RefreshThemeButton();
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

}
