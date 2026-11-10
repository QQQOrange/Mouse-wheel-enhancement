using System;
using System.Diagnostics;
using System.IO;
using IOPath = System.IO.Path;
using System.Text;
using System.Windows;
using System.Windows.Media;
using MouseWheelEnhancement.Core;

namespace MouseWheelEnhancement;

/// <summary>
/// 设置窗口的「应用名单：导入导出」（v1.8.24 从 2292 行的 MainWindow.xaml.cs 拆出来）。
/// 名单列表、筛选和右键覆盖菜单已迁到 UI/AppsPage.xaml；这里只剩文件级的导出/导入。
/// </summary>
public partial class MainWindow
{
    /* ══════════════ 事件：导出 / 导入（v1.8.0） ══════════════ */

    /// <summary>
    /// 一键导出诊断包：配置 + 日志 + 环境信息打成一个 zip，直接定位到资源管理器。
    ///
    /// <para>
    /// 目的很实际：报障时不用再教用户"去哪个文件夹找 config.json 和 app.log"，
    /// 也不用猜对方跑的是哪个版本、是不是管理员、多少 DPI。
    /// </para>
    /// </summary>
    internal void ExportDiag_Click(object sender, RoutedEventArgs e)
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

    internal void ExportConfig_Click(object sender, RoutedEventArgs e)
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

    internal void ExportHelp_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出帮助文案",
            Filter = "JSON (*.json)|*.json",
            FileName = $"mouse-wheel-help-{DateTime.Now:yyyyMMdd-HHmm}.json",
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            File.WriteAllText(dlg.FileName, HelpContent.ExportJson(), new UTF8Encoding(false));
            SetFooterHint($"帮助文案已导出：{IOPath.GetFileName(dlg.FileName)}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"导出失败：{ex.Message}", Services.AppTitle,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    internal void ImportConfig_Click(object sender, RoutedEventArgs e)
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
            AppsPageView?.Invalidate();
            RefreshTokenUi();
            AccelerationPageView?.InvalidateCurve();
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
        ? $"v{Services.Version} · {(UiText.IsEnglish ? "Administrator" : "管理员")}"
        : $"v{Services.Version} · {(UiText.IsEnglish ? "Standard user" : "普通权限")}";

    /// <summary>
    /// 底部右侧那行小字临时换成一句提示，6 秒后自动还原。
    ///
    /// <para>
    /// 以前是直接改文字就不管了 —— 于是导一次诊断包，底部就会永久停在
    /// "诊断包已导出：xxx.zip"，版本号和权限提示再也看不到。
    /// </para>
    /// </summary>
}
