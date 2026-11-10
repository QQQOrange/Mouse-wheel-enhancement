using System.Windows;
using MouseWheelEnhancement.Core;

namespace MouseWheelEnhancement;

/// <summary>
/// 设置窗口里仍由主窗口持有的「配置 ↔ 控件」部分（v1.8.28 从 MainWindow.xaml.cs 拆出来）。
///
/// <para>
/// 这一块只剩两条约定：标题栏的总开关（不属于任何页面），以及几个跨页入口。
/// 「概览」页的自启 / 管理员 / 行为 / 外观绑定已迁到
/// <c>UI/OverviewPage.xaml.cs</c>；「平滑滚动」「加速」「应用名单」「更新」「关于」
/// 的控件也都在各自页面里，用页面自己的 <c>_syncing</c> 挡回写。
/// </para>
/// </summary>
public partial class MainWindow
{
    /* ══════════════ 标题栏：总开关 ══════════════ */

    private void Master_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Services.SetEnabled(MasterSwitch.IsChecked == true);
    }

    /* ══════════════ 跨页入口 ══════════════ */

    /// <summary>
    /// 「触控板级」预设用：把「细腻模式」开关打开（等同于用户自己点一下）。
    /// 加速页可能还没建（用户直接切到平滑页），先按需构造再交给页面拨开关。
    /// </summary>
    internal void EnableFineModeFromPreset()
    {
        EnsureAccelerationPage().EnableFineModeFromPreset();
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
