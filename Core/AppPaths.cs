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

    public static string ConfigPath => Path.Combine(DataDir, "config.json");
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
