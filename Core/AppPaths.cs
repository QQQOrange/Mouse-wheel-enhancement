namespace MouseWheelEnhancement.Core;

/// <summary>
/// 程序的数据目录。
///
/// # 位置
///
/// 按用户要求，数据就放在**软件自己的文件夹**里，目录名固定为
/// <c>Mouse wheel enhancement data</c>：
/// <code>
/// &lt;程序目录&gt;\Mouse wheel enhancement data\
///     config.json     全部设置（令牌以 DPAPI 加密后存在里面）
///     logs\app.log    运行日志
/// </code>
/// 这样整个程序连同设置可以整体拷走，不散落在 %APPDATA%。
///
/// # 什么时候会退到 %APPDATA%
///
/// 程序放在 <c>C:\Program Files</c> 且当前不是管理员时，程序目录不可写。
/// 这种情况下硬写会每次启动都失败、设置也存不下来，所以退到
/// <c>%APPDATA%\Mouse wheel enhancement data</c>，并在日志和「关于」页里
/// 明确标出"数据实际存在哪里" —— 用户不该猜自己的设置跑到哪去了。
///
/// 环境变量 <c>MWE_DATA_DIR</c> 可以强制指定（自检与调试用）。
/// </summary>
public static class AppPaths
{
    public const string DataFolderName = "Mouse wheel enhancement data";

    private static string? _dataDir;

    /// <summary>实际使用的数据目录（进程内只解析一次）。</summary>
    public static string DataDir => _dataDir ??= Resolve();

    /// <summary>是不是退到了 %APPDATA%（程序目录不可写）。</summary>
    public static bool IsFallback { get; private set; }

    public static string LogDir => Path.Combine(DataDir, "logs");
    public static string LogPath => Path.Combine(LogDir, "app.log");

    public static string ExeDir => AppContext.BaseDirectory;

    private static string Resolve()
    {
        var env = Environment.GetEnvironmentVariable("MWE_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(env))
        {
            var d = env.Trim();
            TryEnsure(d);
            return d;
        }

        /* 主程序被放在版本目录 <安装目录>\app\<版本>\ 里时，数据目录必须在
         * **安装目录**下（也就是往上**两级**），理由见本类开头。
         *
         * 这里曾经写错过：判据只看父目录 = 安装目录\app\，
         * 而启动器根本不在 app\ 里 —— 于是这条永远不会命中，
         * 用户直接双击 app\<版本>\ 里的 exe 时，设置就落在版本目录里，
         * 下次升级被 CleanOldVersions 连目录一起删掉。 */
        var self = ExeDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Directory.GetParent(self)?.FullName;

        var installRoot = InstallRootOf(ExeDir);
        if (installRoot != null)
        {
            var installData = Path.Combine(installRoot, DataFolderName);
            if (Directory.Exists(installData) ||
                File.Exists(Path.Combine(installRoot, "MouseWheelEnhancement.exe")))
            {
                if (TryEnsure(installData))
                {
                    IsFallback = false;
                    return installData;
                }
            }
        }

        /* 退一级：exe 放在安装目录的某个子文件夹里（老布局），
         * 父目录有数据文件夹或启动器就认它。 */
        if (!string.IsNullOrEmpty(parent))
        {
            var upData = Path.Combine(parent, DataFolderName);
            if (Directory.Exists(upData) || File.Exists(Path.Combine(parent, "MouseWheelEnhancement.exe")))
            {
                if (TryEnsure(upData))
                {
                    IsFallback = false;
                    return upData;
                }
            }
        }

        var local = Path.Combine(ExeDir, DataFolderName);
        if (TryEnsure(local))
        {
            IsFallback = false;
            return local;
        }

        var roaming = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), DataFolderName);
        if (TryEnsure(roaming))
        {
            IsFallback = true;
            return roaming;
        }

        // 两个都写不了（极端受限环境）：仍然返回本地路径，让后续写入自己报错
        IsFallback = false;
        return local;
    }

    /// <summary>
    /// 若 <paramref name="exeDir"/> 形如 <c>&lt;安装目录&gt;\app\&lt;版本&gt;</c>，
    /// 返回 <c>&lt;安装目录&gt;</c>；否则返回 null。
    ///
    /// <para>
    /// 抽成纯函数是为了能在自检里断言 —— 这条判据错位过一次：原来的写法只看
    /// 父目录（<c>&lt;安装目录&gt;\app\</c>）里有没有启动器，而启动器根本不在 app\ 里，
    /// 于是条件永远不成立，用户直接双击版本目录里的 exe 时设置就落进了那个
    /// 下次升级会被删掉的目录。
    /// </para>
    /// </summary>
    internal static string? InstallRootOf(string? exeDir)
    {
        if (string.IsNullOrWhiteSpace(exeDir)) return null;
        try
        {
            var self = exeDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var parent = Directory.GetParent(self)?.FullName;
            if (string.IsNullOrEmpty(parent)) return null;

            if (!string.Equals(Path.GetFileName(parent), "app", StringComparison.OrdinalIgnoreCase)) return null;
            if (!Version.TryParse(Path.GetFileName(self), out _)) return null;

            return Directory.GetParent(parent)?.FullName;
        }
        catch { return null; }
    }

    /// <summary>目录能建、能写，才算可用。只判 Exists 不够 —— Program Files 下能建目录不等于能写文件。</summary>
    private static bool TryEnsure(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".write-probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }
}
