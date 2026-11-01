using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using MouseWheelEnhancement.Core;

namespace MouseWheelEnhancement;

/// <summary>
/// 诊断包：把「配置 + 日志 + 环境信息」打成一个 zip。
///
/// # 为什么要单独拆出来
///
/// 原来这段逻辑直接写在「关于」页的按钮事件里，于是它**只能在有窗口、有 Services 的时候跑**，
/// 自检和 <c>--featurecheck</c> 都碰不到 —— 而它恰恰是最容易"写完就坏"的那类代码
/// （路径不存在、日志还没建、zip 里条目名字写错）。
///
/// 拆成静态类之后：UI 和命令行诊断走**同一份实现**，能自动验证。
/// </summary>
internal static class DiagPack
{
    /// <summary>系统信息里那一行行的内容（不含 DPI，DPI 由调用方给）。</summary>
    private static IEnumerable<string> SystemLines(double dpiX, double dpiY)
    {
        yield return $"版本        : v{Services.Version}";
        yield return $"导出时间    : {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
        yield return $"系统        : {Environment.OSVersion}";
        yield return $"运行时      : .NET {Environment.Version} / 64位={Environment.Is64BitProcess}";
        yield return $"管理员权限  : {Services.IsElevated}";
        yield return $"数据目录    : {AppPaths.DataDir}";
        yield return $"配置路径    : {Services.Store?.ConfigPath ?? "（未初始化）"}";
        yield return $"窗口 DPI    : {dpiX:0.##}x / {dpiY:0.##}x";
        yield return $"虚拟桌面    : {SystemParameters.VirtualScreenWidth:0}×{SystemParameters.VirtualScreenHeight:0}（DIP）";

        /* 迭代器里不能在 try/catch 中 yield（编译器直接拒绝），
         * 所以先算好再统一输出。RefreshRateMonitor 在极端情况下会抛，这里兜一层。 */
        string frameLine;
        try
        {
            frameLine = $"帧间隔      : {RefreshRateMonitor.FrameIntervalMs:0.##} ms" +
                        $"（自动跟随={Services.Config.Smooth.AutoFps}）";
        }
        catch
        {
            frameLine = "帧间隔      : （读取失败）";
        }
        yield return frameLine;

        var engine = Services.Engine;
        yield return engine == null
            ? "钩子        : （引擎未初始化）"
            : $"钩子        : running={engine.IsRunning} thread={engine.HookThreadAlive} 轮换={engine.Rotations}";

        yield return engine == null
            ? "目标应用    : （引擎未初始化）"
            : $"目标应用    : {engine.LastTarget.Exe}（{engine.LastTarget.Class}）";
        yield return "说明        : 上面那行如果为空，表示这次启动还没滚动过";
    }

    /// <summary>
    /// 生成诊断包，返回实际写进去的条目名（给验证用）。
    ///
    /// <para>
    /// 配置和日志用 <c>CreateEntryFromFile</c> 直接搬，**不读进内存**：
    /// 日志可能有几 MB，全部读进来只为写一遍没必要。
    /// </para>
    /// </summary>
    public static List<string> Build(string path, double dpiX = 1, double dpiY = 1)
    {
        var written = new List<string>();

        // 目标目录可能不存在（用户把它删了 / 桌面被重定向到没挂载的盘）
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        /* 时间戳只到秒：同一秒连点两次会撞名，而 `ZipFile.Open(Create)`
         * 遇到已存在的文件是直接抛 IOException 的。 */
        try { if (File.Exists(path)) File.Delete(path); } catch { }

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        var configPath = Services.Store?.ConfigPath;
        if (!string.IsNullOrEmpty(configPath) && File.Exists(configPath))
        {
            zip.CreateEntryFromFile(configPath, "config.json");
            written.Add("config.json");
        }

        if (File.Exists(Log.FilePath))
        {
            zip.CreateEntryFromFile(Log.FilePath, "logs/app.log");
            written.Add("logs/app.log");
        }

        var system = zip.CreateEntry("system.txt");
        using (var w = new StreamWriter(system.Open(), new UTF8Encoding(false)))
        {
            foreach (var line in SystemLines(dpiX, dpiY)) w.WriteLine(line);
        }
        written.Add("system.txt");

        return written;
    }

    /// <summary>默认落盘位置：桌面（取不到就用数据目录）。</summary>
    public static string DefaultPath()
    {
        var dir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!Directory.Exists(dir)) dir = AppPaths.DataDir;
        return Path.Combine(dir, $"鼠标滚轮增强-诊断-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
    }
}
