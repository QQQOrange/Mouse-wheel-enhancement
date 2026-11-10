using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using MouseWheelEnhancement.Core;

namespace MouseWheelEnhancement.UI;

/// <summary>
/// 「概览」页。独立成 UserControl 后，主窗口在第一次选中该页时才构造它
/// （首页默认就是它，所以正常启动会立刻构造一次；配置里停在别的页时则保持懒加载）。
///
/// <para>
/// 运行状态统计（钩子 / 目标应用 / 接管次数 / 速度条）、最近识别的程序、
/// 行为开关（自启 / 管理员 / 关闭到托盘 / 暂停 / 横向 / 倍率提示 / 看门狗）、
/// 外观主题和权限提示都归这一页维护。400 ms 的刷新节拍仍由主窗口的
/// <c>RefreshStats</c> 驱动，页面只负责把读到的值铺到自己的控件上。
/// </para>
/// <para>
/// 页面内部的 <c>_syncing</c> 对应主窗口的 <c>_loading</c>：每个 <c>*_Changed</c>
/// 都是"读控件 → 写 <c>Services.Config</c> → <c>_host.Commit()</c>"，程序化回填时
/// 必须整条跳过，否则载入配置会被控件事件反向写回去。
/// </para>
/// </summary>
public partial class OverviewPage : UserControl
{
    private readonly MainWindow _host;

    /// <summary>
    /// 程序化回填控件时置位，防止 Checked 反向写回配置。
    /// 初值 true：XAML 解析期间单选框归组、开关默认值都可能触发一次 Checked。
    /// </summary>
    private bool _syncing = true;

    /// <summary>速度条当前的目标值（动画补间用，避免每 400ms 读动画中间值）。</summary>
    private double _rateBarTarget;

    /// <summary>「最近识别的程序」上一次渲染的内容指纹，没变就不重建。</summary>
    private string _recentKey = "";

    /// <summary>连续点启动开关时只弹一次提权确认，避免 UAC 连闪。</summary>
    private bool _elevatePrompted;

    public OverviewPage(MainWindow host)
    {
        _host = host;
        InitializeComponent();

        SyncFromConfig();
        RefreshStats();
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
            // 「开机自启」「以管理员身份运行」显示系统真实状态，不看配置里的意图
            RefreshStartupSwitches();

            CloseToTraySwitch.IsChecked = c.Behavior.CloseToTray;
            PauseSelfSwitch.IsChecked = c.Behavior.PauseWhileSettingsOpen;
            HorizontalSwitch.IsChecked = c.Input.HorizontalBoost;
            OsdSwitch.IsChecked = c.Behavior.OsdMultiplier;
            WatchdogSwitch.IsChecked = c.Behavior.HookWatchdog;

            var th = Theme.Normalize(c.Appearance.Theme);
            ThemeSystem.IsChecked = th == Theme.System;
            ThemeLight.IsChecked = th == Theme.Light;
            ThemeDark.IsChecked = th == Theme.Dark;
        }
        finally { _syncing = false; }

        UpdateThemeHints();
    }

    /* ══════════════ 运行状态 ══════════════ */

    /// <summary>
    /// 刷新运行状态卡片和「最近识别的程序」。主窗口的 400ms 定时器每拍调一次；
    /// 页面刚构造时也调一次，避免四格统计停在 "—"。
    /// </summary>
    internal void RefreshStats()
    {
        RefreshRecentTargets();

        var e = Services.Engine;
        var running = e.IsRunning;

        StatHook.Text = UiText.Translate(running ? "运行中" : "未运行");
        StatHook.Foreground = running
            ? _host.B("SuccessBrush")
            : _host.B("DangerBrush");

        var t = e.LastTarget;
        StatApp.Text = t.IsKnown ? t.Exe : "—";

        StatCount.Text = $"{e.BoostedCount} / {e.PassedCount}";

        var rate = e.LastRatePerSec;
        StatRate.Text = rate > 0.05
            ? (UiText.IsEnglish ? $"{rate:0.0} notches/s" : $"{rate:0.0} 格/秒")
            : "—";
        var barValue = Math.Min(40.0, Math.Max(0.0, rate));
        // 值变化时用 260ms 补间，进度条不会每 400ms 硬跳一下
        if (Math.Abs(_rateBarTarget - barValue) > 0.3)
        {
            _rateBarTarget = barValue;
            RateBar.BeginAnimation(ProgressBar.ValueProperty,
                new DoubleAnimation(barValue, TimeSpan.FromMilliseconds(260))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                });
        }
    }

    /// <summary>
    /// 「最近识别的程序」：每次刷新重建最多 6 行（数量很小，重建比增量维护简单可靠）。
    /// </summary>
    private void RefreshRecentTargets()
    {
        var panel = RecentPanel;
        if (panel == null) return;

        var rows = WheelEngine.RecentTargets();
        if (rows.Count == 0)
        {
            panel.Children.Clear();
            var empty = new TextBlock
            {
                Text = UiText.Translate("还没识别到程序 —— 在别的窗口里滚一下就会出现。"),
                Opacity = 0.6,
                Margin = new Thickness(0, 2, 0, 0),
            };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            empty.SetResourceReference(TextBlock.FontSizeProperty, 12.0);
            panel.Children.Add(empty);
            return;
        }

        var key = string.Join(";", rows.Select(r => r.Exe + r.LastTicks));
        if (key == _recentKey) return;      // 内容没变就不重建，避免每帧重建 UI
        _recentKey = key;

        panel.Children.Clear();
        foreach (var r in rows.Take(6))
        {
            var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel();
            var name = new TextBlock { Text = r.Exe, FontWeight = FontWeights.SemiBold, FontSize = 13 };
            name.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            var cls = new TextBlock { Text = r.Class, FontSize = 11, Opacity = 0.65 };
            cls.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            left.Children.Add(name);
            left.Children.Add(cls);
            grid.Children.Add(left);

            var right = new TextBlock
            {
                Text = $"{r.Semantics} · {r.Events} 次" + (r.Boosted > 0 ? $"（加速 {r.Boosted}）" : ""),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
            };
            right.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);

            panel.Children.Add(grid);
        }
    }

    /* ══════════════ 事件：行为开关 ══════════════ */

    private void AutoStart_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;

        var on = AutoStartSwitch.IsChecked == true;
        Services.Config.Behavior.RunAtLogin = on;

        /* 注意：**不要**顺手把「以管理员身份运行」也关掉。
         *
         * 旧版本里它俩是从属关系，父开关一关就连带拉平。现在管理员开关管的是
         * 每一次启动（手动双击也提权），跟"开不开机自启"是两件事 ——
         * 用户完全可能想要"平时以管理员跑，但别开机自己启动"。
         * 真在这里清掉的话，用户关一次开机自启就悄悄丢了管理员模式，
         * 下次打开又看到"当前不是管理员身份"。 */
        UpdateAdminStartHint();
        _host.Commit();
        ApplyStartup(allowElevate: true);
    }

    private void AdminStart_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;

        Services.Config.Behavior.RunAsAdmin = AdminStartSwitch.IsChecked == true;
        _host.Commit();
        ApplyStartup(allowElevate: true);
    }

    private void CloseToTray_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        Services.Config.Behavior.CloseToTray = CloseToTraySwitch.IsChecked == true;
        _host.Commit();
    }

    private void PauseSelf_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        Services.Config.Behavior.PauseWhileSettingsOpen = PauseSelfSwitch.IsChecked == true;
        _host.Commit();
    }

    private void Horizontal_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        Services.Config.Input.HorizontalBoost = HorizontalSwitch.IsChecked == true;
        _host.Commit();
    }

    private void Osd_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        Services.Config.Behavior.OsdMultiplier = OsdSwitch.IsChecked == true;
        _host.Commit();
    }

    private void Watchdog_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        Services.Config.Behavior.HookWatchdog = WatchdogSwitch.IsChecked == true;
        _host.Commit();
    }

    /* ══════════════ 开机自启 / 管理员运行 ══════════════ */

    private void AdminStartFix_Click(object sender, RoutedEventArgs e)
    {
        if (Services.IsElevated)
        {
            ApplyStartup(allowElevate: false);
            return;
        }

        ElevateToApplyStartup();
    }

    /// <summary>
    /// 把两种开机自启方式按配置同步到系统。
    ///
    /// <para>
    /// 涉及计划任务的分支（要建、或者要删已有的）都必须有管理员权限 ——
    /// 注册表 Run 项能写，计划任务不能。没有权限就提权重启自己，
    /// 由新进程带 <c>--apply-startup</c> 回来把这一步做完。
    /// </para>
    /// </summary>
    private void ApplyStartup(bool allowElevate)
    {
        var b = Services.Config.Behavior;

        /* 需要管理员权限的两种情形：
         *   ① 要建任务（开机自启 + 以管理员运行都开着）；
         *   ② 任务已经存在，无论如何都要删一次。
         * 只开「以管理员身份运行」、不开机自启时**不需要**动系统 ——
         * 那时候提权纯属白弹一次确认框（提权是在下次启动时自己做的）。 */
        var needAdmin = AdminStart.IsEnabled() || (b.RunAtLogin && b.RunAsAdmin);

        if (needAdmin && !Services.IsElevated)
        {
            if (allowElevate) ElevateToApplyStartup();
            else RefreshStartupSwitches();
            return;
        }

        if (!AutoStart.Apply(b.RunAtLogin, b.RunAsAdmin, out var error))
        {
            Log.Warn("应用开机自启失败：" + error);
            MessageBox.Show(_host, error ?? "设置开机自启失败。", Services.AppTitle,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        RefreshStartupSwitches();
    }

    private void ElevateToApplyStartup()
    {
        if (_elevatePrompted) { RefreshStartupSwitches(); return; }
        _elevatePrompted = true;

        var r = MessageBox.Show(_host,
            "这一项要写 Windows 计划任务（开机那次由它直接给管理员权限），设置它需要管理员权限。\n\n" +
            "点「确定」后系统会弹出一次确认，请选「是」。程序会自动重启并把设置应用好。",
            Services.AppTitle, MessageBoxButton.OKCancel, MessageBoxImage.Information);

        if (r != MessageBoxResult.OK)
        {
            _elevatePrompted = false;
            RefreshStartupSwitches();
            return;
        }

        /* 带 --apply-startup 但不带 --minimized：重启后把设置窗口打开，
         * 让用户看见开关真的变成"开"，而不是"点完之后一片安静"。
         *
         * 拉起目标、参数转义、防循环标记都交给 App.RelaunchElevated ——
         * 它用的是**稳定入口**（启动器），而不是版本目录里的主程序本体
         * （后者换个版本就丢了"启动器在哪"，自启任务又会指回版本目录）。 */
        if (App.RelaunchElevated("--apply-startup"))
        {
            App.ExitApp();
            return;
        }

        _elevatePrompted = false;
        MessageBox.Show(_host, "提权被取消或失败，设置没有生效。", Services.AppTitle,
            MessageBoxButton.OK, MessageBoxImage.Warning);
        RefreshStartupSwitches();
    }

    /// <summary>
    /// 让两个开关反映**真实状态**。
    ///
    /// <para>
    /// 「开机自启」看系统：注册表项或计划任务任一存在就算开着 ——
    /// 计划任务可能被用户在「任务计划程序」里手动删掉，以系统为准更诚实。
    /// </para>
    /// <para>
    /// 「以管理员身份运行」看**配置意图**，不看计划任务。因为它现在管的是每一次启动，
    /// 手动启动那条路根本不经过计划任务 —— 拿任务存在性来显示，会出现
    /// "明明开着却显示关"（用户报的就是这个）。
    /// 意图与实际不符时，由下面的提示条负责说清楚。
    /// </para>
    /// </summary>
    private void RefreshStartupSwitches()
    {
        var adminTask = AdminStart.IsEnabled();
        var runKey = AutoStart.IsRunKeyEnabled();

        var prev = _syncing;
        _syncing = true;
        try
        {
            AutoStartSwitch.IsChecked = adminTask || runKey;
            AdminStartSwitch.IsChecked = Services.Config.Behavior.RunAsAdmin;
        }
        finally { _syncing = prev; }

        UpdateAdminStartHint();
        UpdateAdminStartBrokenBar(adminTask);
        RefreshElevationCard();
    }

    /// <summary>
    /// 「以管理员身份运行」下面那行说明。两种组合说实话：
    /// 开了开机自启，开机那次是不弹确认的（计划任务）；没开，就只有手动启动才提权。
    /// </summary>
    private void UpdateAdminStartHint()
    {
        if (AdminStartHint == null) return;

        var runAtLogin = AutoStartSwitch.IsChecked == true;
        AdminStartHint.Text = runAtLogin
            ? "每次启动都以管理员身份运行。开机那次由计划任务直接给权限、不弹确认；手动打开时系统会问一次。"
            : "每次手动打开都会请求管理员权限。打开上面的「开机自启」后，开机那次也会是管理员 —— 而且不弹确认。";
    }

    /// <summary>
    /// 设置意图与系统实际不一致时的提示条。三种情况：
    /// <list type="number">
    ///   <item><b>该有任务却没有</b>（开关开着、任务没建成功）→ 提示补建；</item>
    ///   <item><b>任务失效</b>（指向的文件没了 / 指向版本目录）→ 提示重建；</item>
    ///   <item><b>不该有任务却还在</b>（用户关掉了）→ 提示清理，否则下次登录仍会被无声拉起。</item>
    /// </list>
    /// 三者都"看上去一切正常"，所以必须显式说出来。
    /// </summary>
    private void UpdateAdminStartBrokenBar(bool adminTask)
    {
        if (AdminStartBrokenBar == null) return;

        var b = Services.Config.Behavior;
        var wantTask = b.RunAtLogin && b.RunAsAdmin;

        string? message = null;
        var stale = false;

        if (adminTask && !wantTask)
        {
            message = UiText.IsEnglish
                ? "A scheduled task still exists, but \"Start with Windows + Run as administrator\" is now off — " +
                  "it will still launch this app at your next sign-in (with elevation). Click Fix now to clean it up."
                : "计划任务还在，但设置里已经关掉了「开机自启 + 以管理员身份运行」—— " +
                  "它会让你下次登录时仍然被拉起来（而且是管理员）。点「立即修复」按当前设置清理。";
        }
        else if (adminTask)
        {
            try { stale = AdminStart.IsStale(); }
            catch { /* COM 查询失败就当没坏，别给用户假警报 */ }

            if (stale)
            {
                var target = AdminStart.TaskTargetPath();
                var missing = string.IsNullOrWhiteSpace(target) || !File.Exists(target);

                /* 两种失效要说清楚是哪一种 —— 用户看到"文件不存在"和
                 * "指向的不是启动器"，能做的事其实不一样。 */
                message = missing
                    ? "开机自启任务已失效：它指向的程序文件已经不存在（" +
                      (string.IsNullOrWhiteSpace(target) ? "任务里没有可执行动作" : target) +
                      "），多半是升级后旧版本目录被清理了。下次开机不会自动启动 —— 点「立即修复」重建它。"
                    : "开机自启任务还在，但指向的不是启动器（" + target +
                      "）—— 这个位置在下次升级时会被清理掉，到时候开机就起不来了。点「立即修复」重建它。";
            }
        }
        else if (wantTask)
        {
            message = "设置里开着「开机自启 + 以管理员身份运行」，但计划任务不存在 —— " +
                      "开机那次不会自动启动（多半是上次建任务时没拿到管理员权限）。点「立即修复」补上。";
        }

        AdminStartBrokenBar.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible;
        if (message != null) AdminStartBrokenText.Text = message;
    }

    /// <summary>
    /// 「关于」页的权限说明 + 本页的权限提示卡片。
    ///
    /// <para>
    /// 关键在于把两种情况分开说：<b>没开这个功能</b>（正常，普通权限够用）和
    /// <b>开了却没提权成功</b>（异常，要说清楚为什么、怎么重试）。
    /// 以前两种情况共用一句"当前不是管理员身份"，用户明明开了开关，
    /// 看到这句只会觉得"程序没生效"。
    /// </para>
    /// </summary>
    internal void RefreshElevationCard()
    {
        if (ElevationCard == null)
        {
            _host.RefreshAboutElevation();
            return;
        }

        var elevated = Services.IsElevated;
        var wantsAdmin = Services.Config.Behavior.RunAsAdmin;

        _host.RefreshAboutElevation();

        /* 页脚那行由主窗口的 RefreshStats 统一写（它要带版本号），这里不要抢着改 ——
         * 两处格式不一样，先写的会被后写的覆盖，改的人还以为没生效。 */
        ElevationCard.Visibility = elevated ? Visibility.Collapsed : Visibility.Visible;

        ElevationHint.Text = wantsAdmin
            ? "设置里已经开启了「以管理员身份运行」，但这次启动没能拿到管理员权限 —— " +
              "可能是在系统弹出的确认里点了「取消」，或者被安全策略拦下了。" +
              "点下面的按钮重试；如果每次都这样，检查一下系统的 UAC 设置。"
            : "当前不是管理员身份。Windows 不允许普通程序拦截更高权限窗口的输入，" +
              "所以「以管理员身份运行的窗口」里的滚轮不会被增强（任务管理器、部分游戏启动器、安装程序等）。" +
              "下面的按钮会打开「以管理员身份运行」并重启，之后每次启动都保持。";
    }

    /// <summary>
    /// 「权限提示」卡片上的按钮：以管理员身份重启。
    ///
    /// <para>
    /// 顺手把设置里的「以管理员身份运行」打开 —— 用户点这个按钮的意图就是
    /// "我要管理员"，只提这一次权的话，下次打开软件又变回普通权限，
    /// 他会再撞一次同样的墙（这次报障就是这么来的）。
    /// </para>
    /// </summary>
    private void RestartElevated_Click(object sender, RoutedEventArgs e)
    {
        if (!Services.Config.Behavior.RunAsAdmin)
        {
            Services.Config.Behavior.RunAsAdmin = true;
            _host.Commit();
            RefreshStartupSwitches();
        }

        if (App.RelaunchElevated())
        {
            App.ExitApp();
            return;
        }

        MessageBox.Show(_host, "提权被取消或失败，本次仍是普通权限。", Services.AppTitle,
            MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /* ══════════════ 事件：外观 ══════════════ */

    private void Theme_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;

        var mode = ThemeLight.IsChecked == true ? Theme.Light
                 : ThemeDark.IsChecked == true ? Theme.Dark
                 : Theme.System;

        Services.Config.Appearance.Theme = mode;

        // 立刻生效，不等配置保存那条链路 —— 切主题如果是"点了过半秒才变"，很像卡了
        Theme.SetMode(mode);
        UpdateThemeHints();
        _host.RefreshThemeButton();

        // 主题跟引擎无关，不必重算派生标签
        _host.Commit(refreshDerived: false);
    }

    /// <summary>
    /// 刷新「外观」那一行右侧的状态文字，并把单选框对齐到配置。
    ///
    /// <para>
    /// 之所以要单独写这么一段：选"跟随系统"时，用户看不到系统当前到底是浅还是深，
    /// 界面上就会变成"我选了跟随系统，但不知道它跟成了什么"。
    /// </para>
    /// </summary>
    internal void UpdateThemeHints()
    {
        if (ThemeNow == null || ThemeSystem == null || ThemeLight == null || ThemeDark == null) return;

        var mode = Theme.Normalize(Services.Config.Appearance.Theme);

        var prev = _syncing;
        _syncing = true;
        try
        {
            ThemeSystem.IsChecked = mode == Theme.System;
            ThemeLight.IsChecked = mode == Theme.Light;
            ThemeDark.IsChecked = mode == Theme.Dark;
        }
        finally { _syncing = prev; }

        ThemeNow.Text = mode == Theme.System
            ? $"跟随系统 · 现在是{(Theme.SystemIsLight() ? "浅色" : "深色")}"
            : $"固定{(Theme.IsLightNow ? "浅色" : "深色")}，系统换了也不跟";
    }

    /* ══════════════ smoke 用 ══════════════ */

    /// <summary>smoke 用：两个自启开关和「管理员」整行是否可用。</summary>
    internal bool AutoStartOn => AutoStartSwitch.IsChecked == true;

    internal bool AdminStartOn => AdminStartSwitch.IsChecked == true;

    internal bool AdminStartRowEnabled => AdminStartRow.IsEnabled;
}
