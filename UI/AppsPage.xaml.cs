using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using IOPath = System.IO.Path;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MouseWheelEnhancement.Core;

namespace MouseWheelEnhancement.UI;

/// <summary>
/// 「应用名单」页。独立成 UserControl 后，主窗口在第一次切到该页时才构造它；
/// 名单行、筛选、运行中图标缓存、右键覆盖菜单和标题正则提示都在这里维护。
/// </summary>
public partial class AppsPage : UserControl
{
    private readonly MainWindow _host;
    private readonly ObservableCollection<AppRuleRowData> _rows = new();

    /// <summary>名单内容的指纹，用来避免"拖一下滑块就把列表重建一遍"。</summary>
    private string _signature = "\u0000";

    /// <summary>名单页未打开时置位；切到该页再真正枚举进程/取图标。</summary>
    private bool _dirty = true;

    /// <summary>程序化回填控件时置位，防止 Checked/TextChanged 反向写回配置。</summary>
    private bool _syncing;

    public AppsPage(MainWindow host)
    {
        _host = host;
        InitializeComponent();

        AppsList.ItemsSource = _rows;
        SyncFromConfig();
        UpdateRegexHint();
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
            ModeBlack.IsChecked = c.Filter.Mode == FilterMode.Blacklist;
            ModeWhite.IsChecked = c.Filter.Mode == FilterMode.Whitelist;
            TitleRegexBox.Text = c.Filter.TitleRegex;
        }
        finally { _syncing = false; }
    }

    /// <summary>正则写错时在输入框下面提示（以配置里那份为准）。</summary>
    internal void UpdateRegexHint()
    {
        if (RegexHint == null) return;
        var pattern = Services.Config.Filter.TitleRegex;
        if (string.IsNullOrWhiteSpace(pattern))
        {
            RegexHint.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            _ = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            RegexHint.Visibility = Visibility.Collapsed;
        }
        catch (ArgumentException ex)
        {
            RegexHint.Text = (UiText.IsEnglish ? "Invalid regex, disabled for now: " : "这个正则写错了，暂时不生效：") + ex.Message;
            RegexHint.Visibility = Visibility.Visible;
        }
    }

    /* ══════════════ 列表刷新 / 筛选 ══════════════ */

    /// <summary>
    /// 配置变了：页开着就立刻重建，没开就只记一个脏标记，切回来再补。
    /// （主窗口的配置通知、导入、恢复默认都会走这里。）
    /// </summary>
    internal void Invalidate()
    {
        if (!_host.IsAppsTabSelected)
        {
            _dirty = true;
            return;
        }
        RefreshApps();
    }

    /// <summary>刚切到本页时调用：上次没补的刷新在这里补。</summary>
    internal void OnActivated()
    {
        if (!_dirty) return;
        RefreshApps();
    }

    private void RefreshApps()
    {
        /* 懒加载：应用名单没打开时不做进程枚举/图标提取/列表重建。
         * 这些在启动时并不需要，等用户真的切到这一页再做；切页时再补。 */
        _dirty = false;

        var apps = Services.Config.Filter.Apps;

        /* 指纹没变就直接返回。
         * 原来这段在**每次**配置变更时都跑（拖滑块一秒几十次），
         * 结果是列表被清空重建几十次：选中项丢失、滚动位置跳回顶部、
         * 还平白产生一堆 UI 对象。 */
        var signature = string.Join('\u0001', apps.Select(a =>
            a.Exe + '\u0002' + a.Note + '\u0003' + OverrideSummary(a.Override)));
        if (signature == _signature) return;
        _signature = signature;

        _rows.Clear();
        var paths = RunningProcessPaths();
        foreach (var a in apps.OrderBy(a => a.Exe, StringComparer.OrdinalIgnoreCase))
            _rows.Add(new AppRuleRowData
            {
                Exe = a.Exe,
                Note = string.IsNullOrWhiteSpace(a.Note) ? UiText.Translate("（无备注）") : a.Note,
                Override = OverrideSummary(a.Override),
                Icon = TryIconFor(a.Exe, paths),
            });

        AppsCount.Text = UiText.IsEnglish ? $"{apps.Count} items" : $"{apps.Count} 项";
        AppsEmpty.Visibility = apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyAppsFilter();
    }

    private void AppsFilter_Changed(object sender, TextChangedEventArgs e) => ApplyAppsFilter();

    private void ApplyAppsFilter()
    {
        if (AppsList == null) return;
        var text = (AppsFilterBox?.Text ?? "").Trim();
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(AppsList.ItemsSource);
        if (view == null) return;

        if (text.Length == 0)
        {
            view.Filter = null;
        }
        else
        {
            view.Filter = o =>
            {
                if (o is not AppRuleRowData row) return false;
                return row.Exe.Contains(text, StringComparison.OrdinalIgnoreCase)
                       || row.Note.Contains(text, StringComparison.OrdinalIgnoreCase)
                       || row.Override.Contains(text, StringComparison.OrdinalIgnoreCase);
            };
        }

        if (AppsNoMatch != null)
        {
            var total = _rows.Count;
            var shown = view.Cast<object>().Count();
            AppsNoMatch.Visibility = total > 0 && shown == 0 && text.Length > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    /// <summary>把一份覆盖写成一句人话（空覆盖 → 空串）。</summary>
    private static string OverrideSummary(AppOverride? o)
    {
        if (o == null) return "";
        var parts = new List<string>();
        if (o.MaxMultiplier is double m)
            parts.Add(UiText.IsEnglish ? $"max {m:0}×" : $"上限 {m:0}×");
        if (o.Smooth is bool sm)
            parts.Add(UiText.IsEnglish ? (sm ? "smooth on" : "smooth off") : (sm ? "平滑开" : "平滑关"));
        if (o.ScrollMode == "pixel") parts.Add(UiText.IsEnglish ? "pixel" : "按像素");
        else if (o.ScrollMode == "whole") parts.Add(UiText.IsEnglish ? "whole notch" : "整格");
        return parts.Count == 0 ? "" : "· " + string.Join(" · ", parts);
    }

    /* ── 运行中程序的图标（v1.9.11） ──
     * 只给"当前正在运行"的名单项查图标；没运行/取不到就显示占位符。
     * 进程枚举和图标都缓存，刷新名单时不会每次都扫一遍系统。 */

    private static readonly object IconGate = new();
    private static readonly Dictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, string>? _runningPaths;
    private static long _runningPathsAt;

    private static Dictionary<string, string> RunningProcessPaths()
    {
        if (_runningPaths != null && Environment.TickCount64 - _runningPathsAt < 10_000)
            return _runningPaths;

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    var path = p.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(path))
                    {
                        var name = IOPath.GetFileName(path);
                        if (!string.IsNullOrEmpty(name)) map[name] = path;
                    }
                }
                catch { /* 系统进程/位数不同/权限不足：跳过 */ }
                finally { p.Dispose(); }
            }
        }
        catch { /* 枚举失败：保留空表，图标显示占位符 */ }

        _runningPaths = map;
        _runningPathsAt = Environment.TickCount64;
        return map;
    }

    private static ImageSource? TryIconFor(string exe, IReadOnlyDictionary<string, string> paths)
    {
        if (string.IsNullOrEmpty(exe)) return null;
        lock (IconGate)
        {
            if (IconCache.TryGetValue(exe, out var cached)) return cached;
        }

        if (!paths.TryGetValue(exe, out var path)) return null;
        ImageSource? icon = null;
        try
        {
            using var ico = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (ico != null)
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(
                    ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                icon = src;
            }
        }
        catch { /* 没有图标资源/路径失效：占位符 */ }

        lock (IconGate)
        {
            if (IconCache.Count > 256) IconCache.Clear();
            IconCache[exe] = icon;
        }
        return icon;
    }

    /* ══════════════ 事件：名单模式 / 添加 / 移除 / 筛选 ══════════════ */

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        Services.Config.Filter.Mode = ModeWhite.IsChecked == true
            ? FilterMode.Whitelist
            : FilterMode.Blacklist;
        _host.Commit();
    }

    private void AddApp_Click(object sender, RoutedEventArgs e)
    {
        var raw = AddAppBox.Text;
        var name = AppRuleName.Normalize(raw);
        if (name.Length == 0)
        {
            /* 提示写在**底栏**（常驻、切页也看得见）。
             * 以前写进「更新」页的 UpdateStatus —— 用户在名单页点「添加」，
             * 屏幕上不会有任何变化，只能怀疑按钮坏了。 */
            _host.SetFooterHint("先填一个程序名，例如 chrome.exe");
            return;
        }

        var apps = Services.Config.Filter.Apps;
        if (apps.Any(a => string.Equals(a.Exe, name, StringComparison.OrdinalIgnoreCase)))
        {
            _host.SetFooterHint($"{name} 已经在名单里了");
            return;
        }

        apps.Add(new AppRule { Exe = name, Note = "手动添加" });
        AddAppBox.Text = "";
        _host.Commit();
        _host.SetFooterHint($"已加入名单：{name}");
    }

    private void RemoveApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string exe) return;
        var apps = Services.Config.Filter.Apps;
        var hit = apps.FirstOrDefault(a => string.Equals(a.Exe, exe, StringComparison.OrdinalIgnoreCase));
        if (hit == null) return;
        apps.Remove(hit);
        _host.Commit();
        _host.SetFooterHint($"已从名单移除：{exe}");
    }

    private void TitleRegex_Changed(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        Services.Config.Filter.TitleRegex = TitleRegexBox.Text.Trim();
        _host.Commit();
    }

    /// <summary>"从运行中的应用选择"：列当前有可见窗口的进程。</summary>
    private void PickRunning_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = PickRunningBtn };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        IEnumerable<Process> procs;
        try { procs = Process.GetProcesses(); }
        catch { procs = Array.Empty<Process>(); }

        var items = new List<(string Exe, string Title)>();
        foreach (var p in procs)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(p.MainWindowTitle)) continue;
                var exe = AppRuleName.Normalize(p.MainModule?.FileName ?? p.ProcessName);
                if (exe.Length == 0 || !seen.Add(exe)) continue;
                items.Add((exe, p.MainWindowTitle));
            }
            catch { /* 受保护进程读不到 MainModule，跳过 */ }
            finally { p.Dispose(); }
        }

        if (items.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "（没有找到带窗口的程序）", IsEnabled = false });
        }
        else
        {
            foreach (var (exe, title) in items.OrderBy(i => i.Exe).Take(50))
            {
                var mi = new MenuItem { Header = $"{exe}    —    {Trim(title, 46)}" };
                mi.Click += (_, _) =>
                {
                    AddAppBox.Text = exe;
                    AddAppBox.Focus();
                    AddAppBox.CaretIndex = AddAppBox.Text.Length;
                };
                menu.Items.Add(mi);
            }
        }

        menu.IsOpen = true;
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /* ══════════════ 事件：右键覆盖菜单 ══════════════ */

    /// <summary>
    /// 名单里右键某个程序 → 设这个程序专属的倍率上限 / 平滑开关（v1.8.0）。
    ///
    /// <para>
    /// 用代码临时建菜单而不是写进 XAML：菜单项要根据当前值打勾，
    /// 还要在点完之后立刻刷新列表摘要，代码里建比模板绑定更直接。
    /// </para>
    /// </summary>
    private void AppsList_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(AppsList, e.OriginalSource as DependencyObject)
            is not ListBoxItem item || item.DataContext is not AppRuleRowData row) return;

        var rule = Services.Config.Filter.Apps
            .FirstOrDefault(a => string.Equals(a.Exe, row.Exe, StringComparison.OrdinalIgnoreCase));
        if (rule == null) return;

        var menu = new ContextMenu();

        var multRoot = new MenuItem { Header = $"「{row.Exe}」倍率上限" };
        void AddMult(string label, double? value)
        {
            var mi = new MenuItem
            {
                Header = label,
                IsCheckable = true,
                IsChecked = rule.Override?.MaxMultiplier is double cur
                    ? Math.Abs(cur - (value ?? -1)) < 0.01
                    : value == null,
            };
            mi.Click += (_, _) => ApplyOverride(rule, o => o.MaxMultiplier = value);
            multRoot.Items.Add(mi);
        }
        AddMult("跟随全局", null);
        for (var i = 2; i <= 8; i++) AddMult($"{i}×", i);
        menu.Items.Add(multRoot);

        var smoothRoot = new MenuItem { Header = "平滑" };
        void AddSmooth(string label, bool? value)
        {
            var mi = new MenuItem
            {
                Header = label,
                IsCheckable = true,
                IsChecked = rule.Override?.Smooth == value,
            };
            mi.Click += (_, _) => ApplyOverride(rule, o => o.Smooth = value);
            smoothRoot.Items.Add(mi);
        }
        AddSmooth("跟随全局", null);
        AddSmooth("开", true);
        AddSmooth("关", false);
        menu.Items.Add(smoothRoot);

        var semRoot = new MenuItem { Header = "滚动语义" };
        void AddSem(string label, string? value, string tip)
        {
            var mi = new MenuItem
            {
                Header = label,
                IsCheckable = true,
                IsChecked = string.Equals(rule.Override?.ScrollMode, value, StringComparison.OrdinalIgnoreCase),
                ToolTip = tip,
            };
            mi.Click += (_, _) => ApplyOverride(rule, o => o.ScrollMode = value);
            semRoot.Items.Add(mi);
        }
        AddSem("自动（按窗口类型猜）", null, "WPF/WinForms/Qt 默认按整档发，最稳");
        AddSem("按事件（整格）", "whole", "一个事件滚一档，列表类应用用这个");
        AddSem("按像素（平滑）", "pixel",
            "按 delta 比例滚，文档/网页类的 WPF 程序切到这一档会明显更顺");
        menu.Items.Add(semRoot);

        var feelRoot = new MenuItem { Header = "手感（跟手 τ / 惯性）" };
        void AddFeel(string label, double? tau, int? glide)
        {
            var mi = new MenuItem
            {
                Header = label,
                IsCheckable = true,
                IsChecked = rule.Override?.TauMs is double t && Math.Abs(t - (tau ?? -1)) < 0.01
                            || tau == null && rule.Override?.TauMs == null && rule.Override?.GlideMs == null,
            };
            mi.Click += (_, _) => ApplyOverride(rule, o => { o.TauMs = tau; o.GlideMs = glide; });
            feelRoot.Items.Add(mi);
        }
        AddFeel("跟随全局", null, null);
        AddFeel("跟手（τ30 / 惯性 150）", 30, 150);
        AddFeel("标准（τ40 / 惯性 320）", 40, 320);
        AddFeel("悠长（τ55 / 惯性 500）", 55, 500);
        menu.Items.Add(feelRoot);

        menu.Items.Add(new Separator());
        var reset = new MenuItem { Header = "清除这个程序的覆盖" };
        reset.Click += (_, _) => ApplyOverride(rule, _ => { }, clear: true);
        menu.Items.Add(reset);

        menu.PlacementTarget = item;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void ApplyOverride(AppRule rule, Action<AppOverride> mutate, bool clear = false)
    {
        if (clear)
        {
            rule.Override = null;
        }
        else
        {
            rule.Override ??= new AppOverride();
            mutate(rule.Override);
            // 两项都空 → 当成没设，配置文件更干净
            if (rule.Override.MaxMultiplier == null && rule.Override.Smooth == null)
                rule.Override = null;
        }

        Services.Store.Touch();
        RefreshApps();
    }
}

/// <summary>应用名单列表里的一行（给 DataTemplate 绑定用，必须是可反射访问的类型）。</summary>
internal sealed class AppRuleRowData
{
    public string Exe { get; init; } = "";
    public string Note { get; init; } = "";

    /// <summary>覆盖摘要（v1.8.0），例如"· 上限 3× · 平滑关"。空串 = 完全跟随全局。</summary>
    public string Override { get; init; } = "";

    /// <summary>运行中程序的图标；没在运行/取不到时为 null，模板显示占位符。</summary>
    public ImageSource? Icon { get; init; }
}
