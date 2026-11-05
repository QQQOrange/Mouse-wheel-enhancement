using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using IOPath = System.IO.Path;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MouseWheelEnhancement.Core;
using MouseWheelEnhancement.Update;

namespace MouseWheelEnhancement;

/// <summary>
/// 设置窗口的「应用名单：列表刷新、右键覆盖菜单、导入导出」（v1.8.24 从 2292 行的 MainWindow.xaml.cs 拆出来）。
/// 同一类型的 partial 文件，字段与状态仍在主文件里，这里只放行为。
/// </summary>
public partial class MainWindow
{
    private void RefreshApps()
    {
        var apps = Services.Config.Filter.Apps;

        /* 指纹没变就直接返回。
         * 原来这段在**每次**配置变更时都跑（拖滑块一秒几十次），
         * 结果是列表被清空重建几十次：选中项丢失、滚动位置跳回顶部、
         * 还平白产生一堆 UI 对象。 */
        var signature = string.Join('\u0001', apps.Select(a =>
            a.Exe + '\u0002' + a.Note + '\u0003' + OverrideSummary(a.Override)));
        if (signature == _appsSignature) return;
        _appsSignature = signature;

        _rows.Clear();
        foreach (var a in apps.OrderBy(a => a.Exe, StringComparer.OrdinalIgnoreCase))
            _rows.Add(new AppRuleRowData
            {
                Exe = a.Exe,
                Note = string.IsNullOrWhiteSpace(a.Note) ? "（无备注）" : a.Note,
                Override = OverrideSummary(a.Override),
            });

        AppsCount.Text = $"{apps.Count} 项";
        AppsEmpty.Visibility = apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>把一份覆盖写成一句人话（空覆盖 → 空串）。</summary>
    private static string OverrideSummary(AppOverride? o)
    {
        if (o == null) return "";
        var parts = new List<string>();
        if (o.MaxMultiplier is double m) parts.Add($"上限 {m:0}×");
        if (o.Smooth is bool sm) parts.Add(sm ? "平滑开" : "平滑关");
        if (o.ScrollMode == "pixel") parts.Add("按像素");
        else if (o.ScrollMode == "whole") parts.Add("整格");
        return parts.Count == 0 ? "" : "· " + string.Join(" · ", parts);
    }

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
        AddFeel("跟手（τ16 / 惯性 200）", 16, 200);
        AddFeel("标准（τ30 / 惯性 420）", 30, 420);
        AddFeel("悠长（τ45 / 惯性 600）", 45, 600);
        menu.Items.Add(feelRoot);

        menu.Items.Add(new Separator());
        var reset = new MenuItem { Header = "清除这个程序的覆盖" };
        reset.Click += (_, _) => ApplyOverride(rule, _ => { }, clear: true);
        menu.Items.Add(reset);

        menu.PlacementTarget = item;
        menu.IsOpen = true;
        e.Handled = true;
    }

    /* ══════════════ 事件：导出 / 导入（v1.8.0） ══════════════ */

    /// <summary>
    /// 一键导出诊断包：配置 + 日志 + 环境信息打成一个 zip，直接定位到资源管理器。
    ///
    /// <para>
    /// 目的很实际：报障时不用再教用户"去哪个文件夹找 config.json 和 app.log"，
    /// 也不用猜对方跑的是哪个版本、是不是管理员、多少 DPI。
    /// </para>
    /// </summary>
    private void ExportDiag_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Services.Store.SaveNow();

            var path = DiagPack.DefaultPath();
            var dpi = VisualTreeHelper.GetDpi(this);
            DiagPack.Build(path, dpi.DpiScaleX, dpi.DpiScaleY);

            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            SetFooterHint($"诊断包已导出：{IOPath.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"导出失败：{ex.Message}", Services.AppTitle,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ExportConfig_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出配置",
            Filter = "配置文件 (*.json)|*.json",
            FileName = $"mouse-wheel-config-{DateTime.Now:yyyyMMdd-HHmm}.json",
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            Services.Store.SaveNow();
            File.Copy(Services.Store.ConfigPath, dlg.FileName, true);
            SetFooterHint($"配置已导出：{IOPath.GetFileName(dlg.FileName)}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"导出失败：{ex.Message}", Services.AppTitle,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ImportConfig_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入配置",
            Filter = "配置文件 (*.json)|*.json|所有文件 (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var cfg = ConfigStore.Parse(File.ReadAllText(dlg.FileName, Encoding.UTF8));
            if (cfg == null)
            {
                MessageBox.Show(this, "这个文件不是有效的配置文件（JSON 解析失败）。",
                    Services.AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            /* 导入的是别人机器上的配置时，launcherPath 很可能指向一个本机不存在的路径 ——
             * 那会让"开机自启"任务指向空气。这里保留本机那份有效的路径。 */
            var localLauncher = Services.Config.Behavior.LauncherPath;

            Services.Store.Replace(cfg);   // 归一化 + 落盘 + 通知引擎

            var importedLauncher = Services.Config.Behavior.LauncherPath;
            if (!string.IsNullOrEmpty(localLauncher) && File.Exists(localLauncher)
                && (string.IsNullOrEmpty(importedLauncher) || !File.Exists(importedLauncher)))
            {
                Services.Config.Behavior.LauncherPath = localLauncher;
                Services.Store.Touch();
                SetFooterHint("配置已导入（启动器路径保留本机值）");
            }

            LoadFromConfig();
            RefreshApps();
            RefreshTokenUi();
            RedrawCurve();
            RefreshStats();
            SetFooterHint("配置已导入并生效");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"导入失败：{ex.Message}", Services.AppTitle,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>底部右侧那行小字的正式内容（版本 + 权限）。</summary>
    private static string FooterInfo() => Services.IsElevated
        ? $"v{Services.Version} · 管理员"
        : $"v{Services.Version} · 普通权限";

    /// <summary>
    /// 底部右侧那行小字临时换成一句提示，6 秒后自动还原。
    ///
    /// <para>
    /// 以前是直接改文字就不管了 —— 于是导一次诊断包，底部就会永久停在
    /// "诊断包已导出：xxx.zip"，版本号和权限提示再也看不到。
    /// </para>
    /// </summary>
}
