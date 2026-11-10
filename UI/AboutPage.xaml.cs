using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MouseWheelEnhancement.Core;

namespace MouseWheelEnhancement.UI;

/// <summary>
/// 「关于」页。独立成 UserControl 后，主窗口在第一次切到该页时才构造它；
/// 页面自己的版本信息、权限说明、导出动作和实时输入监视都在这里维护。
/// </summary>
public partial class AboutPage : UserControl
{
    private readonly MainWindow _host;
    private DispatcherTimer? _inputMonitorTimer;
    private bool _syncing;

    public AboutPage(MainWindow host)
    {
        _host = host;
        InitializeComponent();

        AboutVersion.Text = $"v{Services.Version}（build {MainWindow.BuildStamp()}）";
        AboutAuthor.Text = "QQQOrange";
        AboutDataDir.Text = AppPaths.DataDir;
        AboutLogPath.Text = Log.FilePath;
        HomeText.Text = $"https://github.com/{Services.Updater.Repo}";
        GitHubText.Text = $"https://github.com/{Services.Updater.Repo}";
        ReleasesText.Text = Services.Updater.ReleasesUrl;

        SyncLanguage();
        RefreshElevation();
        UiText.Apply(this);
        Loaded += (_, _) => RefreshElevation();
        Unloaded += (_, _) => StopInputMonitor();
    }

    /// <summary>配置里的语言变化后，同步两个单选按钮。</summary>
    internal void SyncLanguage()
    {
        _syncing = true;
        try
        {
            var lang = Core.Localization.Normalize(Services.Config.Appearance.Language);
            var english = lang == Core.Localization.English && HelpContent.HasEnglish;
            LangZh.IsChecked = !english;
            LangEn.IsChecked = english;
            LangEn.IsEnabled = HelpContent.HasEnglish;
            LangEn.ToolTip = HelpContent.HasEnglish ? null : "英文帮助资源未安装";
        }
        finally { _syncing = false; }
    }

    /// <summary>刷新权限说明；主窗口启动/提权重试后会调。</summary>
    internal void RefreshElevation()
    {
        if (AboutElevation == null) return;
        var elevated = Services.IsElevated;
        var wantsAdmin = Services.Config.Behavior.RunAsAdmin;
        AboutElevation.Text = elevated
            ? UiText.Translate("管理员") + (UiText.IsEnglish ? " (can capture elevated windows)" : "（可以接管以管理员身份运行的窗口）")
            : wantsAdmin
                ? (UiText.IsEnglish
                    ? "Standard user — \"Run as administrator\" is on, but this launch did not get elevation"
                    : "普通用户 —— 设置里已开启「以管理员身份运行」，但这次启动没拿到权限")
                : (UiText.IsEnglish
                    ? "Standard user (scrolling inside elevated windows is not enhanced)"
                    : "普通用户（管理员窗口内的滚动不会被增强）");
    }

    /* ══════════════ 动作：转给主窗口（窗口所有者/页脚状态都在主窗口） ══════════════ */

    private void ExportDiag_Click(object sender, RoutedEventArgs e) => _host.ExportDiag_Click(sender, e);
    private void ExportConfig_Click(object sender, RoutedEventArgs e) => _host.ExportConfig_Click(sender, e);
    private void ExportHelp_Click(object sender, RoutedEventArgs e) => _host.ExportHelp_Click(sender, e);
    private void ImportConfig_Click(object sender, RoutedEventArgs e) => _host.ImportConfig_Click(sender, e);
    private void OpenDataDir_Click(object sender, RoutedEventArgs e) => _host.OpenDataDir_Click(sender, e);
    private void OpenLog_Click(object sender, RoutedEventArgs e) => _host.OpenLog_Click(sender, e);
    private void OpenHome_Click(object sender, RoutedEventArgs e) => _host.OpenHome_Click(sender, e);
    private void OpenGitHub_Click(object sender, RoutedEventArgs e) => _host.OpenGitHub_Click(sender, e);
    private void OpenReleases_Click(object sender, RoutedEventArgs e) => _host.OpenReleases_Click(sender, e);
    private void RestartHook_Click(object sender, RoutedEventArgs e) => _host.RestartHook_Click(sender, e);
    private void ResetDefaults_Click(object sender, RoutedEventArgs e) => _host.ResetDefaults_Click(sender, e);
    private void ExitApp_Click(object sender, RoutedEventArgs e) => _host.ExitApp_Click(sender, e);

    private void Language_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        if (LangEn.IsChecked == true && !HelpContent.HasEnglish)
        {
            SyncLanguage();
            return;
        }
        _host.SetHelpLanguage(LangEn.IsChecked == true);
    }

    /* ══════════════ 实时输入监视 ══════════════ */

    private void InputMonitor_Toggled(object sender, RoutedEventArgs e)
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
