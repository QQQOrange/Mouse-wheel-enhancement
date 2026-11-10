using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using IOPath = System.IO.Path;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MouseWheelEnhancement.Core;
using MouseWheelEnhancement.UI;
using MouseWheelEnhancement.Update;

namespace MouseWheelEnhancement;

public partial class MainWindow : Window
{
    /// <summary>
    /// 程序化设置控件时置位，防止 Checked/ValueChanged 反向写回配置。
    /// 初值必须是 true：XAML 解析时给 Slider 设 Minimum/Maximum 就会触发
    /// ValueChanged，而那时后半个页面的控件还没建出来。
    /// </summary>
    private bool _loading = true;

    /// <summary>这次变更来自界面自己 —— 用来决定要不要整页回填（避免拖动滑块时被回填打断）。</summary>
    private bool _updatingFromUi;

    private readonly DispatcherTimer _tick;

    /// <summary>画刷缓存：每 400 ms 的刷新里不必反复走资源字典查找。</summary>
    private readonly Dictionary<string, Brush> _brushes = new();

    /// <summary>帮助页是懒渲染的：二十多个参数块几百个控件，不影响启动才合理。</summary>
    private bool _helpRendered;

    /// <summary>
    /// XAML 解析期间 TabControl 会先落在默认的 index 0 上并触发一次 SelectionChanged。
    /// 那一刻不能构造概览页：窗口位置记忆可能把选中项恢复到别的页，
    /// 概览页应当保持懒加载。构造完成后由 RestoreWindowPlacement / SelectTab
    /// 按最终选中的页决定建不建。
    /// </summary>
    private bool _allowLazyPages;

    /// <summary>「概览」页第一次被选中时才构造（内容见 UI/OverviewPage.xaml）。</summary>
    private OverviewPage? OverviewPageView;

    /// <summary>「加速」页第一次被选中时才构造（内容见 UI/AccelerationPage.xaml）。</summary>
    private AccelerationPage? AccelerationPageView;

    /// <summary>「应用名单」页第一次被选中时才构造（内容见 UI/AppsPage.xaml）。</summary>
    private AppsPage? AppsPageView;

    /// <summary>「关于」页第一次被选中时才构造。</summary>
    private AboutPage? AboutPageView;

    /// <summary>「更新」页第一次被选中时才构造（内容见 UI/UpdatePage.xaml）。</summary>
    private UpdatePage? UpdatePageView;

    /// <summary>「平滑滚动」页第一次被选中时才构造（内容见 UI/SmoothPage.xaml）。</summary>
    private SmoothPage? SmoothPageView;

    /// <summary>更新页还没建时后台自动检查发现的版本，等页面首次打开再补显示。</summary>
    private UpdateInfo? _pendingUpdateInfo;
    private bool _pendingUpdateFromAutoCheck;

    /// <summary>置位后关闭窗口就是真的销毁（释放资源），而不是退回托盘。</summary>
    private bool _allowClose;

    public MainWindow()
    {
        var bootSw = Stopwatch.StartNew();
        InitializeComponent();
        _allowLazyPages = true;
        var initMs = bootSw.ElapsedMilliseconds;

        SubtitleText.Text = $"{Services.AppTitleEn} · v{Services.Version}";
        PageBadge.Text = $"v{Services.Version}";
        FooterDataDir.Text = AppPaths.DataDir;

        LoadFromConfig();
        RefreshStats();
        RestoreWindowPlacement();

        /* LastTab 不合法时 RestoreWindowPlacement 不会改选中项，界面仍停在
         * XAML 的默认页 0 —— 这时也得把概览页建出来，否则会是一页空白。 */
        if (Nav.SelectedIndex == OverviewTabIndex) EnsureOverviewPage();

        UiText.Apply(this);

        Services.StateChanged += OnServicesStateChanged;

        _tick = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
        _tick.Tick += (_, _) => RefreshStats();
        _tick.Start();

        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { _tick.Start(); RefreshStats(); }
            else _tick.Stop();
        };

        Closing += OnClosing;
        Theme.Changed += OnThemeChanged;
        Closed += (_, _) =>
        {
            Services.StateChanged -= OnServicesStateChanged;
            Theme.Changed -= OnThemeChanged;
            _tick.Stop();
            WheelEngine.TraceEnabled = false;

            /* 窗口（连同视觉树、D3D 表面、字形缓存）刚被销毁，此刻是把工作集
             * 还给系统的唯一合适时机。放在窗口自己身上而不是创建方，是为了
             * "谁打开的就谁负责还" —— 任何路径销毁窗口都会走到这里。 */
            MemoryTrim.TrimIfLarge();
        };
        Loaded += (_, _) =>
        {
            ApplyTitleBar(Theme.IsLightNow);
            ApplyWindowCorner();
        };

        Log.Info($"主窗口构造完成：XAML {initMs} ms / 总计 {bootSw.ElapsedMilliseconds} ms");

        /* 小屏兜底：默认 1120×830 在 1366×768 的笔记本上底部会被切掉
         * （底栏和关于页最后一排按钮都在屏幕外）。第一次打开时按工作区收一下，
         * 之后用户自己拉过的尺寸由 RestoreWindowPlacement 负责。 */
        try
        {
            var wa = SystemParameters.WorkArea;
            if (Width > wa.Width - 24) Width = Math.Max(MinWidth, wa.Width - 24);
            if (Height > wa.Height - 24) Height = Math.Max(MinHeight, wa.Height - 24);
        }
        catch { /* 取不到工作区就按 XAML 的默认值 */ }
    }

    /// <summary>取画刷（带缓存）。各页共用；给 UpdatePage 的状态文字上色用。</summary>
    internal Brush B(string key)
    {
        if (_brushes.TryGetValue(key, out var b)) return b;
        b = (Brush)FindResource(key);
        _brushes[key] = b;
        return b;
    }

    /// <summary>
    /// 构建日期。用 exe 文件自身的修改时间，而不是 Assembly.Location ——
    /// 单文件发布时后者恒为空串（IL3000）。
    /// </summary>
    internal static string BuildStamp()
    {
        try
        {
            var exe = UpdateService.ExePath;
            return File.Exists(exe) ? File.GetLastWriteTime(exe).ToString("yyyy-MM-dd") : "local";
        }
        catch { return "local"; }
    }

    /// <summary>
    /// 让**标题栏**跟着界面主题走（Win10 1809+ 支持，取不到就算了）。
    ///
    /// <para>
    /// 浅色时必须显式写 0，不能"什么都不做"：这个属性是**按窗口**保存的，
    /// 从深色切回浅色时如果只是不再设置，标题栏会停在深色，
    /// 于是窗口主体是白的、标题栏是黑的，很难看。
    /// </para>
    /// </summary>
    private void ApplyTitleBar(bool light)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            var dark = light ? 0 : 1;
            // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE；老 build 上是 19
            if (Native.DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)) != 0)
                Native.DwmSetWindowAttribute(hwnd, 19, ref dark, sizeof(int));
        }
        catch { /* 非关键美化 */ }
    }

    /// <summary>
    /// 窗口外框四角变圆角（Win11 22000+；取不到就保持直角，不报错）。
    ///
    /// <para>
    /// 自绘标题栏之后窗口不再有标准边框，Win11 的默认圆角就不生效了，
    /// 得显式把 DWMWA_WINDOW_CORNER_PREFERENCE 设成 DWMWCP_ROUND。
    /// </para>
    /// <para>
    /// 圆角只影响**窗口外形**：左侧栏是直角方块，不跟着收角的话，
    /// 左上/左下会露出两块缺口（后面是桌面）。所以这里顺手把侧栏的
    /// 左外角也打成同一个半径 —— 只有真的圆上时才打，Win10 上仍是直角，
    /// 不会出现"窗口方、侧栏圆"的割裂感。
    /// </para>
    /// </summary>
    private void ApplyWindowCorner()
    {
        try
        {
            if (Environment.OSVersion.Version.Build < 22000) return;   // 只有 Win11 有得设

            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            var pref = Native.DWMWCP_ROUND;
            if (Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE,
                    ref pref, sizeof(int)) != 0)
                return;   // 设不上（旧版 DWM、远程桌面之类）就老老实实直角

            // 侧栏左外角跟窗口圆角对齐，半径取窗口圆角的 8px 同款
            if (Sidebar != null) Sidebar.CornerRadius = new CornerRadius(8, 0, 0, 8);
        }
        catch { /* 纯美化，任何失败都不该影响启动 */ }
    }

    /// <summary>
    /// 主题变了：把"现画的东西"重画一遍。
    ///
    /// <para>
    /// 走 DynamicResource 的控件会自己更新，但有三处不会：
    /// </para>
    /// <list type="number">
    ///   <item><see cref="B"/> 里的画刷缓存 —— 存的是旧主题的 Brush 对象；</item>
    ///   <item>倍率曲线 —— Canvas 里的 Line/Polygon/TextBlock 是代码建的，颜色写死在对象上；</item>
    ///   <item>帮助页 —— 文案里的前景色是渲染时查出来的，不重建就一直是旧色。</item>
    /// </list>
    /// </summary>
    private void OnThemeChanged()
    {
        _brushes.Clear();
        ApplyTitleBar(Theme.IsLightNow);
        RefreshStats();   // 状态点/徽章的颜色是 FindResource 取完写死在控件上的

        if (_helpRendered && HelpPanel != null)
        {
            HelpPanel.Children.Clear();
            _helpRendered = false;
            EnsureHelpRendered();
        }

        OverviewPageView?.UpdateThemeHints();
        RefreshThemeButton();
        AccelerationPageView?.InvalidateCurve();
        SmoothPageView?.InvalidateSmoothPreview();

        /* 主题切换的轻量过渡：只做整窗透明度回落，不动布局。
         * 比逐控件淡入便宜，也不会在页面重建时闪。 */
        try
        {
            Opacity = 0.94;
            BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(130))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        }
        catch { /* 动画失败不影响主题本身 */ }
    }

    /// <summary>冒烟测试用：把关键尺寸打出来，确认页面真的被布局了（不是全 0）。</summary>
    internal string LayoutReport()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"win={ActualWidth:0}x{ActualHeight:0}");
        sb.Append($" nav={Nav.ActualWidth:0}x{Nav.ActualHeight:0}");
        sb.Append($" curve={AccelerationPageView?.CurveSizeText ?? "0x0"}");
        sb.Append($" line={AccelerationPageView?.CurveChildCount ?? 0}");
        sb.Append($" help={HelpPanel.Children.Count}");
        sb.Append($" tab={Nav.SelectedIndex}");
        sb.Append($" 概览={(OverviewPageView == null ? "未建" : "已建")}");
        sb.Append($" 自启={(OverviewPageView?.AutoStartOn == true ? "开" : "关")}");
        sb.Append($" 管理员启动={(OverviewPageView?.AdminStartOn == true ? "开" : "关")}");
        sb.Append($" 管理员行可用={(OverviewPageView?.AdminStartRowEnabled == true ? "是" : "否")}");
        return sb.ToString();
    }

    public void SelectTab(int index)
    {
        if (index >= 0 && index < Nav.Items.Count) Nav.SelectedIndex = index;
        /* 已经停在该页时再点一次（截图/冒烟/恢复位置都会）不会触发
         * SelectionChanged，所以这里显式保证概览页存在。 */
        if (index == OverviewTabIndex) EnsureOverviewPage();
        if (index == HelpTabIndex) EnsureHelpRendered();
    }

    /// <summary>测量/测试用：立刻真正销毁窗口（绕过"退到托盘"）。</summary>
    internal void CloseAndRelease()
    {
        _allowClose = true;
        Close();
    }

    /* ══════════════ 配置 → 界面 ══════════════ */

    private void LoadFromConfig()
    {
        _loading = true;
        try
        {
            var c = Services.Config;

            MasterSwitch.IsChecked = c.Enabled;

            /* 「概览」页的自启 / 行为 / 外观控件和状态卡归页面自己维护；
             * 页面没建时整段跳过，建好时页面构造里会先按当前配置回填一次。 */
            OverviewPageView?.SyncFromConfig();

            AppsPageView?.SyncFromConfig();
            UpdatePageView?.SyncFromConfig();
            AccelerationPageView?.SyncFromConfig();
            SmoothPageView?.SyncFromConfig();

            var lang = Core.Localization.Normalize(c.Appearance.Language);
            if (lang == Core.Localization.English && !HelpContent.HasEnglish)
                c.Appearance.Language = Core.Localization.Chinese;
            AboutPageView?.SyncLanguage();
        }
        finally { _loading = false; }

        UpdateDerivedLabels();
    }

    /// <summary>只更新"显示出来的数字"，不碰控件值（拖动时逐帧调它）。</summary>
    private void UpdateDerivedLabels()
    {
        /* 「加速」页的数字和速度 → 倍率曲线归页面自己维护；页面没建时整段跳过，
         * 建好时页面构造里会先按当前配置回填一次。 */
        AccelerationPageView?.RefreshLabels();

        /* 「平滑滚动」页的数字和说明归页面自己维护；页面没建时整段跳过，
         * 建好时页面构造里会先按当前配置回填一次。 */
        SmoothPageView?.RefreshLabels();

        /* 「概览」页的外观单选框和状态文字同样归页面所有；标题栏的主题按钮
         * 留在主窗口，页面没建时也要保持图标/提示与配置一致。 */
        OverviewPageView?.UpdateThemeHints();
        RefreshThemeButton();

        /* 正则写错与否不再由主窗口判断 —— 输入框和提示行归应用名单页所有。 */
        AppsPageView?.UpdateRegexHint();
    }

    private void RefreshStats()
    {
        /* 运行状态四格和「最近识别的程序」归「概览」页自己维护；
         * 页面没建时整段跳过，建好时页面构造里会立刻铺一次。 */
        OverviewPageView?.RefreshStats();

        var e = Services.Engine;
        var running = e.IsRunning;

        var on = Services.Config.Enabled;
        _loading = true;
        MasterSwitch.IsChecked = on;
        _loading = false;

        StatusText.Text = UiText.Translate(!on ? "已暂停" : running ? "已启用" : "钩子异常");
        StatusDot.Fill = B(!on ? "TextMuted" : running ? "SuccessBrush" : "DangerBrush");
        if (StatusPillText != null)
        {
            StatusPillText.Text = UiText.Translate(!on ? "已暂停" : running ? "增强中" : "钩子异常");
            StatusPillText.Foreground = B(!on ? "TextMuted" : running ? "AccentHover" : "DangerBrush");
        }
        HookHint.Text = !on
            ? UiText.Translate("总开关关闭 · 完全走系统默认")
            : running
                ? (UiText.IsEnglish
                    ? $"Hook OK · {e.BoostedCount} boosted"
                    : $"钩子正常 · 已接管 {e.BoostedCount} 次")
                : UiText.Translate("钩子未启动，滚动不受影响");
        var today = UiText.IsEnglish
            ? $"{e.BoostedCount} boosted · {e.PassedCount} passed"
            : $"本次已接管 {e.BoostedCount} 次 · 放行 {e.PassedCount} 次";
        if (StatusToday.Text != today) StatusToday.Text = today;

        /* 有临时提示正在显示时不要覆盖：RefreshStats 每 400ms 跑一次，
         * 否则"诊断包已导出"会被立刻刷回版本号，用户根本看不见。 */
        if (!_footerHintOn) FooterRight.Text = FooterInfo();
    }

    private void OnServicesStateChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(OnServicesStateChanged));
            return;
        }

        if (!_updatingFromUi) LoadFromConfig();

        UpdateDerivedLabels();
        AppsPageView?.Invalidate();
        RefreshStats();
        AccelerationPageView?.InvalidateCurve();
    }

    /// <summary>界面改动 → 写配置 → 通知引擎 → 合并写盘。</summary>
    internal void Commit(bool refreshDerived = true)
    {
        if (Services.Store == null) return;

        _updatingFromUi = true;
        try { Services.Store.Touch(); }
        finally { _updatingFromUi = false; }

        if (refreshDerived) UpdateDerivedLabels();
        AccelerationPageView?.InvalidateCurve();
    }

    /* ══════════════ 事件：总开关与行为 ══════════════ */

    private bool _navSyncing;

    /// <summary>
    /// 侧栏导航 → 页面。
    ///
    /// <para>
    /// 左边是 ListBox、右边是（没有标签条的）TabControl，两者要双向同步：
    /// 用户点导航时推 TabControl，代码调用 <see cref="SelectTab"/> 时推回导航。
    /// 用 <c>_navSyncing</c> 挡住回环 —— 否则 A 改 B、B 又改 A，选中项会抖。
    /// </para>
    /// </summary>
    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_navSyncing || NavList.SelectedIndex < 0) return;

        _navSyncing = true;
        try { Nav.SelectedIndex = NavList.SelectedIndex; }
        finally { _navSyncing = false; }
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 标题栏显示当前页名（TabItem.Header 仍是纯文本，正好拿来当标题）
        if (Nav.SelectedItem is TabItem ti && ti.Header != null)
            PageTitle.Text = ti.Header.ToString();

        if (!_navSyncing && Nav.SelectedIndex >= 0)
        {
            _navSyncing = true;
            try { NavList.SelectedIndex = Nav.SelectedIndex; }
            finally { _navSyncing = false; }
        }

        if (Nav.SelectedIndex == HelpTabIndex) EnsureHelpRendered();
        if (Nav.SelectedIndex == OverviewTabIndex && _allowLazyPages)
        {
            EnsureOverviewPage();
        }
        if (Nav.SelectedIndex == AccelerationTabIndex)
        {
            EnsureAccelerationPage().InvalidateCurve();
        }
        if (Nav.SelectedIndex == SmoothTabIndex)
        {
            if (SmoothPageView == null)
            {
                Log.Info("「平滑滚动」页首次选中，按需构造");
                SmoothPageView = new SmoothPage(this);
                SmoothTab.Content = SmoothPageView;
            }
            SmoothPageView.InvalidateSmoothPreview();
        }
        if (Nav.SelectedIndex == AppsTabIndex)
        {
            if (AppsPageView == null)
            {
                Log.Info("「应用名单」页首次选中，按需构造");
                AppsPageView = new AppsPage(this);
                AppsTab.Content = AppsPageView;
            }
            AppsPageView.OnActivated();
        }
        if (Nav.SelectedIndex == UpdateTabIndex)
        {
            if (UpdatePageView == null)
            {
                Log.Info("「更新」页首次选中，按需构造");
                UpdatePageView = new UpdatePage(this);
                UpdateTab.Content = UpdatePageView;

                /* 自动检查可能在页面存在之前就完成了。结果先攒在主窗口，
                 * 页面一建好立刻补上，不能因为懒加载丢掉一次"发现新版"。 */
                if (_pendingUpdateInfo != null)
                {
                    UpdatePageView.ShowUpdateInfo(_pendingUpdateInfo, _pendingUpdateFromAutoCheck);
                    _pendingUpdateInfo = null;
                }
            }
        }
        if (Nav.SelectedIndex == AboutTabIndex && AboutPageView == null)
        {
            Log.Info("「关于」页首次选中，按需构造");
            AboutPageView = new AboutPage(this);
            AboutTab.Content = AboutPageView;
        }

        AnimateTabIn();
    }

    /// <summary>
    /// 「概览」页按需构造：第一次切到该页时建。只建一次，之后复用同一实例。
    /// 正常启动时 XAML 的默认选中页就是它，所以这台窗口几乎总会立刻建一次；
    /// 配置恢复到别的页时则一直保持未建，直到用户真的点开。
    /// </summary>
    private OverviewPage EnsureOverviewPage()
    {
        if (OverviewPageView != null) return OverviewPageView;

        Log.Info("「概览」页首次选中，按需构造");
        OverviewPageView = new OverviewPage(this);
        OverviewTab.Content = OverviewPageView;
        return OverviewPageView;
    }

    /// <summary>
    /// 「加速」页按需构造：第一次切到该页、或「平滑滚动」页的预设需要动这里的
    /// 「细腻模式」开关时才建。只建一次，之后复用同一实例。
    /// </summary>
    private AccelerationPage EnsureAccelerationPage()
    {
        if (AccelerationPageView != null) return AccelerationPageView;

        Log.Info("「加速」页首次需要，按需构造");
        AccelerationPageView = new AccelerationPage(this);
        AccelerationTab.Content = AccelerationPageView;
        return AccelerationPageView;
    }

    /// <summary>
    /// 切页时让新内容轻轻淡入并上移 6px —— 比"啪"一下整页换掉自然。
    ///
    /// <para>
    /// 只用 Opacity + TranslateTransform 两个合成层属性，不触发布局，
    /// 160~190 ms 也不会拖慢操作；构造/恢复页签（窗口还没显示）时不播。
    /// </para>
    /// </summary>
    private void AnimateTabIn()
    {
        if (!IsLoaded) return;
        if (Nav.SelectedContent is not FrameworkElement content) return;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        content.Opacity = 0;

        var shift = new TranslateTransform(0, 6);
        content.RenderTransform = shift;

        content.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(165)) { EasingFunction = ease });
        shift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(190)) { EasingFunction = ease });
    }

    /// <summary>
    /// 标题栏的深浅色按钮：**三态循环** —— 跟随系统 → 固定浅色 → 固定深色 → 跟随系统。
    ///
    /// <para>
    /// 以前只做浅↔深两态：点一下就变成"固定"，而"恢复跟随系统"只能去
    /// 概览 → 外观里找 —— 用户点了标题栏按钮之后就"回不去跟随"了。
    /// 现在三态闭环，图标和 ToolTip 都跟着当前状态走，下一次点会发生什么一目了然。
    /// </para>
    /// </summary>
    private void ThemeBtn_Click(object sender, RoutedEventArgs e)
    {
        var cur = Theme.Normalize(Services.Config.Appearance.Theme);
        var next = cur == Theme.System ? Theme.Light
                 : cur == Theme.Light ? Theme.Dark
                 : Theme.System;

        Services.Config.Appearance.Theme = next;
        Theme.SetMode(next);
        OverviewPageView?.UpdateThemeHints();
        Commit(refreshDerived: false);

        // 换掉 ToolTip 之前先把这个按钮的提示气泡收掉。
        // 用户就是悬停在按钮上点它的，此时气泡正开着；
        // 直接换字符串会触发 WPF 去重挂正在显示的 Popup，
        // 那是"根 Visual 不能具有父级"最容易冒头的时机。
        PopupCleanup.CloseCurrentToolTip();
        RefreshThemeButton();
    }

    /// <summary>标题栏主题按钮的图标与提示，跟着当前模式走。</summary>
    internal void RefreshThemeButton()
    {
        if (ThemeBtn == null || ThemeGlyph == null) return;

        var mode = Theme.Normalize(Services.Config.Appearance.Theme);
        var (glyph, tip) = mode switch
        {
            Theme.Light => ("\uE706", "固定浅色 · 点击切到固定深色（再点一次回到跟随系统）"),
            Theme.Dark => ("\uE708", "固定深色 · 点击回到跟随系统"),
            _ => ("\uE793", $"跟随系统（现在是{(Theme.IsLightNow ? "浅色" : "深色")}）· 点击切到固定浅色"),
        };
        ThemeGlyph.Text = glyph;
        ThemeBtn.ToolTip = tip;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    /// <summary>关闭按钮：走正常的 Closing 流程（默认收进托盘，可在设置里改成真退出）。</summary>
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>帮助页的页签序号（0 概览 / 1 加速 / 2 平滑 / 3 应用 / 4 更新 / 5 帮助 / 6 关于）。</summary>
    internal const int OverviewTabIndex = 0;
    internal const int AccelerationTabIndex = 1;
    internal const int SmoothTabIndex = 2;
    internal const int HelpTabIndex = 5;
    internal const int AppsTabIndex = 3;
    internal const int UpdateTabIndex = 4;
    internal const int AboutTabIndex = 6;

    /// <summary>应用名单页是不是当前页（页面据此决定要不要立刻重建列表）。</summary>
    internal bool IsAppsTabSelected => Nav != null && Nav.SelectedIndex == AppsTabIndex;

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        // 窗口还没建好时（构造期间）不要碰控件；页面没建时也不用读系统值
        SmoothPageView?.SyncWheelLinesFromSystem();
    }

    internal void SetFooterHint(string text) => SetFooterHint(text, true);

    /// <summary>同上，但可以标出这是"成功"还是"失败"（只影响一行字的颜色，不弹窗）。</summary>
    private void SetFooterHint(string text, bool success)
    {
        if (FooterRight == null) return;

        _footerHintOn = true;
        FooterRight.Text = text;
        /* 用 SetResourceReference 而不是赋 Brush 对象：换主题时它自己会重新取值，
         * 否则深色的成功绿会留在浅色底上（对比度 1.9:1，几乎看不见）。 */
        FooterRight.SetResourceReference(TextBlock.ForegroundProperty,
            success ? "TextSecondary" : "DangerBrush");

        _footerHintTimer?.Stop();
        _footerHintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _footerHintTimer.Tick += (_, _) =>
        {
            _footerHintTimer?.Stop();
            _footerHintOn = false;
            if (FooterRight != null)
            {
                FooterRight.Text = FooterInfo();
                FooterRight.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            }
        };
        _footerHintTimer.Start();
    }

    /* ══════════════ 更新 / 关于：外部链接 ══════════════ */

    internal void OpenHome_Click(object sender, RoutedEventArgs e) => App.OpenExternal(Services.Updater.RepoUrl);
    internal void OpenGitHub_Click(object sender, RoutedEventArgs e)
        => App.OpenExternal($"https://github.com/{Services.Updater.Repo}");
    internal void OpenReleases_Click(object sender, RoutedEventArgs e) => App.OpenExternal(Services.Updater.ReleasesUrl);

    /* ══════════════ 更新页：后台结果 → 懒加载页面 ══════════════ */

    /// <summary>
    /// 把一次检查结果铺到「更新」页上（自动检查与手动检查共用）。
    ///
    /// <para>
    /// 自动检查在启动 2.5 秒后跑，此时用户很可能还没点过「更新」页。
    /// 页面没建时先把结果攒着，等 <see cref="Nav_SelectionChanged"/> 首次构造页面再补；
    /// 页面自己也会读 <see cref="Services.PendingUpdate"/>，两条路都走不丢结果。
    /// </para>
    /// </summary>
    public void ShowUpdateInfo(UpdateInfo info, bool fromAutoCheck)
    {
        if (UpdatePageView != null)
        {
            UpdatePageView.ShowUpdateInfo(info, fromAutoCheck);
            return;
        }

        _pendingUpdateInfo = info;
        _pendingUpdateFromAutoCheck = fromAutoCheck;
    }

    /// <summary>
    /// 令牌界面归更新页所有；主窗口保留这个入口给 smoke / 导入 / 重置调用。
    /// 页面还没建时不用做事 —— 页面构造时会按当前令牌刷新一次。
    /// </summary>
    internal void RefreshTokenUi() => UpdatePageView?.RefreshTokenUi();

    /// <summary>smoke 用：先 SelectTab(4) 再取懒加载出来的更新页。</summary>
    internal UpdatePage? UpdatePageForSmoke => UpdatePageView;

    /// <summary>smoke 用：先 SelectTab(2) 再取懒加载出来的平滑页。</summary>
    internal SmoothPage? SmoothPageForSmoke => SmoothPageView;

    /// <summary>smoke 用：先 SelectTab(1) 再取懒加载出来的加速页。</summary>
    internal AccelerationPage? AccelerationPageForSmoke => AccelerationPageView;

    /* ══════════════ 事件：关于 / 诊断 ══════════════ */

    internal void OpenDataDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.DataDir}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { SetFooterHint("打不开数据目录：" + ex.Message); }
    }

    internal void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Log.Info("用户从「关于」页打开了日志");
            if (File.Exists(Log.FilePath))
                Process.Start(new ProcessStartInfo("notepad.exe", $"\"{Log.FilePath}\"") { UseShellExecute = true });
            else
                OpenDataDir_Click(sender, e);
        }
        catch (Exception ex) { SetFooterHint("打不开日志：" + ex.Message); }
    }

    internal void RestartHook_Click(object sender, RoutedEventArgs e)
    {
        var button = sender as Button;
        if (button != null) button.IsEnabled = false;
        try
        {
            var ok = Services.Engine.Restart();
            SetFooterHint(ok ? "钩子已重启。" : "钩子重启失败，看日志。", ok);
            RefreshStats();
        }
        finally { if (button != null) button.IsEnabled = true; }
    }

    internal void ResetDefaults_Click(object sender, RoutedEventArgs e)
    {
        var r = MessageBox.Show(this, "把除 GitHub 令牌以外的所有设置恢复默认值？", Services.AppTitle,
            MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (r != MessageBoxResult.OK) return;

        Services.Store.ResetToDefaults();
        LoadFromConfig();
        AppsPageView?.Invalidate();
        RefreshTokenUi();
        AccelerationPageView?.InvalidateCurve();
        SetFooterHint("已恢复默认设置。");
    }

    internal void ExitApp_Click(object sender, RoutedEventArgs e)
    {
        /* 误点"退出"就等于把增强整个关掉（托盘图标也会消失），
         * 而它就挨着"恢复默认设置"，值得问一句。 */
        var r = MessageBox.Show(this,
            "退出后滚轮增强会停止，托盘图标也会消失。\r\n确定要退出吗？",
            Services.AppTitle, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (r != MessageBoxResult.Yes) return;
        _allowClose = true;
        App.ExitApp();
    }

    /* ══════════════ 窗口位置记忆 ══════════════ */

    /// <summary>
    /// 恢复上次的窗口大小/位置/最大化状态，以及停留的页签。
    ///
    /// <para>
    /// 只在"记录过且还落在虚拟屏内"时才恢复：换了显示器、拔了副屏、
    /// 或者分辨率变小之后，旧坐标可能完全在屏幕外 —— 那种情况下宁可回默认居中，
    /// 也不要让窗口"打开了但看不见"。
    /// </para>
    /// </summary>
    private void RestoreWindowPlacement()
    {
        var a = Services.Config.Appearance;

        if (a.WindowWidth >= MinWidth && a.WindowHeight >= MinHeight
            && a.WindowWidth <= 10000 && a.WindowHeight <= 10000)
        {
            var vl = SystemParameters.VirtualScreenLeft;
            var vt = SystemParameters.VirtualScreenTop;
            var vr = vl + SystemParameters.VirtualScreenWidth;
            var vb = vt + SystemParameters.VirtualScreenHeight;

            // 至少留 120×80 在虚拟屏内，才算"看得见"
            var visible = a.WindowLeft + a.WindowWidth > vl + 120
                       && a.WindowLeft < vr - 120
                       && a.WindowTop + 80 > vt
                       && a.WindowTop < vb - 80;

            if (visible)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Width = a.WindowWidth;
                Height = a.WindowHeight;
                Left = a.WindowLeft;
                Top = a.WindowTop;
            }
        }

        if (a.WindowMaximized) WindowState = WindowState.Maximized;

        if (a.LastTab >= 0 && a.LastTab < Nav.Items.Count) SelectTab(a.LastTab);
    }

    /// <summary>关闭前记下窗口大小/位置/最大化与当前页签（随后由 OnClosing 的 SaveNow 落盘）。</summary>
    private void SaveWindowPlacement()
    {
        try
        {
            var a = Services.Config.Appearance;

            /* 最大化时 Left/Top/Width/Height 是最大化后的值，要恢复的是 RestoreBounds
             * （还原尺寸）。正常状态直接用当前值。 */
            var bounds = WindowState == WindowState.Normal
                ? new Rect(Left, Top,
                    ActualWidth > 0 ? ActualWidth : Width,
                    ActualHeight > 0 ? ActualHeight : Height)
                : RestoreBounds;

            if (bounds.Width >= MinWidth && bounds.Height >= MinHeight
                && !double.IsNaN(bounds.Left) && !double.IsNaN(bounds.Top)
                && !double.IsInfinity(bounds.Left) && !double.IsInfinity(bounds.Top))
            {
                a.WindowWidth = bounds.Width;
                a.WindowHeight = bounds.Height;
                a.WindowLeft = bounds.Left;
                a.WindowTop = bounds.Top;
            }

            a.WindowMaximized = WindowState == WindowState.Maximized;
            a.LastTab = Math.Max(0, Nav.SelectedIndex);
        }
        catch (Exception ex)
        {
            // 位置记忆只是体验优化，失败不该影响关闭
            Log.Warn("保存窗口位置失败：" + ex.Message);
        }
    }

    /* ══════════════ 关闭 ══════════════ */

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        void Mark(string label) => Log.Info($"关闭·{label}：{sw.ElapsedMilliseconds} ms");

        /* 定时器会把自己挂在 Dispatcher 的计时器表上 —— 只要它还开着，
         * 这个窗口就**收不掉**（关闭到托盘是靠销毁窗口来还内存的）。
         * 6 秒的底部提示、180ms 的搜索防抖都顺手停掉。 */
        _footerHintTimer?.Stop();
        _footerHintTimer = null;
        _helpFilterTimer?.Stop();
        _helpFilterTimer = null;

        SaveWindowPlacement();
        Mark("保存窗口位置");

        Services.Store.SaveNow();
        Mark("保存配置");

        /* 窗口要没了，先把弹层都收掉。
         *
         * 提示气泡和右键菜单底层是 Popup，各自有独立的 HwndSource。
         * 窗口在它们还开着的时候被销毁/隐藏，那两个 HwndSource 就会处于
         * "内容已经脱离、但根 Visual 还挂着"的半吊子状态 ——
         * 之后任何一次布局更新都可能在这个状态下炸出
         * "VisualTarget 的根 Visual 不能具有父级"。
         *
         * 而且这个异常是从 NotifyIcon 的原生 WndProc 里冒出去的
         * （异常通道见 App.OnWinFormsThreadException 的说明），
         * 以前的结果是：用户看到框架默认弹窗，日志里一片空白。 */
        CloseOpenPopups();
        Mark("关闭弹层");

        if (_allowClose) return; // 真正销毁这条路径，放手让 WPF 关

        if (!Services.Config.Behavior.CloseToTray)
        {
            /* 真退出这条路径也要先放行，和托盘路径（下面 _allowClose = true）对称：
             * ExitApp 内部会 Shutdown，而 Shutdown 会再尝试关一次这个窗口，
             * 那时 OnClosing 里的保存/弹层清理会被重复执行。 */
            _allowClose = true;
            App.ExitApp();
            return;
        }

        e.Cancel = true;
        Hide();
        Services.Tray?.ShowHintOnce();

        /* ⚠ 退到托盘之后要把窗口**真正销毁**，不能只是 Hide()。
         *
         * 实测：仅托盘常驻约 70 MB，而"打开过设置窗口"之后是 197 MB ——
         * 因为 WPF 的视觉树、D3D 表面、全部控件都还挂在这个隐藏窗口上，
         * 一直占到进程退出。用户点一下 × 只是把它藏起来，那 120 多 MB 就白占了。
         *
         * 这里排到 ApplicationIdle 再关（正在 Closing 里直接 Close 会重入），
         * 并且先确认它还是隐藏状态（用户可能已经又点开了托盘）。
         * 下次从托盘打开时 App.ShowMainWindow 会重新建一个，界面上看不出来。 */
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (IsVisible || _allowClose) return;
            _allowClose = true;
            Close();
        }), DispatcherPriority.ApplicationIdle);
    }

    /// <summary>
    /// 把当前开着的所有弹层（ToolTip / ContextMenu）收掉。
    ///
    /// <para>
    /// 窗口马上要没了的时候必须先做这件事 —— 理由见 <see cref="OnClosing"/> 里的注释。
    /// </para>
    /// </summary>
    private void CloseOpenPopups()
    {
        try
        {
            var n = PopupCleanup.CloseAllIn(this);
            if (n > 0) Log.Info($"关闭了 {n} 个弹层");
        }
        catch (Exception ex)
        {
            Log.Warn("清理弹层失败：" + ex.Message);
        }

        try { PopupCleanup.CloseCurrentToolTip(); }
        catch { /* 关不掉提示气泡无害 */ }
    }
}
