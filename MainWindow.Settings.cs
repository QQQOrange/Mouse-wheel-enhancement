using System;
using System.Windows;
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
/// <para>
/// 「平滑滚动」页的控件事件不在这里：整页已搬进 <c>UI/SmoothPage.xaml.cs</c>，
/// 用页面自己的 <c>_syncing</c> 挡回写；这里只保留跨页的
/// <see cref="EnableFineModeFromPreset"/>。
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

    private void RestoreLines_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        Services.Config.Behavior.FineRestoreLines = (int)Math.Round(RestoreLinesSlider.Value);
        SystemWheelLines.RestoreTarget = Services.Config.Behavior.FineRestoreLines;
        UpdateRestoreLinesLabel();
        Commit(refreshDerived: false);
    }

    private void UpdateRestoreLinesLabel()
    {
        var v = (int)Math.Round(RestoreLinesSlider.Value);
        RestoreLinesValue.Text = v <= 0 ? "原值" : v + " 行";
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

    /* ══════════════ 事件：平滑滚动 ══════════════
     *
     * 平滑页自己的开关、滑块、手感预设和跟手曲线预览都已经搬进
     * UI/SmoothPage.xaml.cs（页面第一次被选中时才构造）。
     * 这里只留一个跨页入口：「平滑滚动」页的「触控板级」预设要顺手打开
     * 「加速」页的「细腻模式」开关，走的必须是用户手点开关时同一条路
     * （含确认弹窗与还原机制）。
     */

    /// <summary>「触控板级」预设用：把「细腻模式」开关打开（等同于用户自己点一下）。</summary>
    internal void EnableFineModeFromPreset()
    {
        if (FineModeSwitch.IsChecked != true) FineModeSwitch.IsChecked = true;
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

    /// <summary>「关于」页切换界面语言后由页面调用。</summary>
    internal void SetHelpLanguage(bool english)
    {
        Services.Config.Appearance.Language = english
            ? Core.Localization.English
            : Core.Localization.Chinese;
        Commit(false);

        UiText.ApplyAll();

        if (_helpRendered && HelpPanel != null)
        {
            HelpPanel.Children.Clear();
            _helpRendered = false;
            EnsureHelpRendered();
        }
        LocalizeHelpChrome();
        AboutPageView?.RefreshElevation();
        SetFooterHint(english ? "Language: English" : "界面语言：简体中文");
    }

}
