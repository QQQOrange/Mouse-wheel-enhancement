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
            /* 配置**不能原样打包**：DPAPI 不可用的极端环境里令牌是明文存在
             * config.json 的，而这个包的设计用途就是"发给开发者看"。
             * 用户在公开 issue 里贴一次就等于把令牌公开了。所以这里写一份脱敏副本。 */
            var entry = zip.CreateEntry("config.json");
            try
            {
                var text = File.ReadAllText(configPath, Encoding.UTF8);
                text = RedactSecrets(text);
                using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                w.Write(text);
                written.Add("config.json");
            }
            catch (Exception ex)
            {
                written.Add("config.json（写入失败：" + ex.Message + "）");
            }
        }

        if (File.Exists(Log.FilePath))
        {
            /* 日志可能正被后台线程写着（FileShare.ReadWrite）。CreateEntryFromFile
             * 只按 FileShare.Read 打开，会撞共享冲突 —— 偏偏"刚出过问题"的那一刻
             * 正是最需要诊断包的时候。所以自己按 ReadWrite 打开再拷。 */
            var entry = zip.CreateEntry("logs/app.log");
            try
            {
                using var src = new FileStream(Log.FilePath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var dst = entry.Open();
                src.CopyTo(dst);
                written.Add("logs/app.log");
            }
            catch (Exception ex)
            {
                written.Add("logs/app.log（写入失败：" + ex.Message + "）");
            }
        }

        var system = zip.CreateEntry("system.txt");
        using (var w = new StreamWriter(system.Open(), new UTF8Encoding(false)))
        {
            foreach (var line in SystemLines(dpiX, dpiY)) w.WriteLine(line);
        }
        written.Add("system.txt");

        /* v1.9.2：把「证据文件」一起打包 —— 用户报某个程序手感不对时，
         * targettrace.txt 直接给出那个窗口被识别成了哪种语义，省一轮来回。 */
        foreach (var name in new[] { "targettrace.txt", "keyprobe.txt", "selftest.txt",
                                        "featurecheck.txt", "scrollsmooth.txt", "wheellines.txt" })
        {
            try
            {
                var evidence = System.IO.Path.Combine(Core.AppPaths.DataDir, name);
                if (!System.IO.File.Exists(evidence)) continue;
                if (new System.IO.FileInfo(evidence).Length > 2 * 1024 * 1024) continue;
                using var es = zip.CreateEntry("diag/" + name).Open();
                using var src = System.IO.File.OpenRead(evidence);
                src.CopyTo(es);
                written.Add("diag/" + name);
            }
            catch { /* 单个文件失败不影响整包 */ }
        }

        return written;
    }

    /// <summary>
    /// 把配置里的凭据换成占位符。
    ///
    /// <para>
    /// JSON 结构不做解析（配置可以手改坏，诊断包不能因此失败）：
    /// 直接按行替换两个键的值。宁可多替换一行，也不能漏出去一串真令牌。
    /// </para>
    /// </summary>
    internal static string RedactSecrets(string json)
    {
        var sb = new StringBuilder(json.Length);
        foreach (var line in json.Split('\n'))
        {
            var t = line.TrimStart();
            if (t.StartsWith("\"gitHubToken\"", StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith("\"gitHubTokenProtected\"", StringComparison.OrdinalIgnoreCase))
            {
                var colon = line.IndexOf(':');
                var comma = line.TrimEnd().EndsWith(",") ? "," : "";
                sb.Append(colon >= 0 ? line[..(colon + 1)] : line).Append(" \"（已省略）\"").Append(comma);
            }
            else sb.Append(line);
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>默认落盘位置：桌面（取不到就用数据目录）。</summary>
    public static string DefaultPath()
    {
        var dir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!Directory.Exists(dir)) dir = AppPaths.DataDir;
        return Path.Combine(dir, $"鼠标滚轮增强-诊断-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
    }
}
