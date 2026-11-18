using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace MouseWheelEnhancement.Core;

/// <summary>
/// 细腻模式的守护进程（命令行形态：<c>--fineguard &lt;pid&gt; &lt;lines&gt; [startTicks]</c>）。
///
/// <para>
/// # 它要解决什么
/// </para>
/// <para>
/// 细腻模式会把系统「每次滚几行」临时改成 1，退出时还回去。正常退出有
/// <c>ProcessExit</c> 兜底、异常退出有 RunOnce 兜底，但**被强杀 / 崩溃 / 关机**
/// 这几条路上主进程什么都来不及做 —— 唯一可靠的办法是另起一个进程等它死。
/// </para>
/// <para>
/// # 为什么不再走 App.OnStartup
/// </para>
/// <para>
/// 原来的实现是在 <c>OnStartup</c> 里分流的，代价是**为了一个 WaitForExit 把整套
/// WPF 拉起来一份**：app.InitializeComponent() 已经把 App.xaml 和两本资源字典
/// 解析完，WPF/WinForms 程序集全部载入 —— 实测工作集 47.7 MB、私有 11.9 MB、
/// 5 个线程。它整条命只做一件事：阻塞。
/// </para>
/// <para>
/// 现在改在 <b>模块初始化器</b>里分流（见文件末尾的 <c>FineGuardModule</c>）。
/// CLR 在**进入 Main 之前**就会跑模块初始化器，那时 <c>Application</c> 还没有被
/// 实例化，WPF 程序集一个都没加载 —— 等待进程因此只剩 CLR + 一个等待句柄。
/// 等之前再调一次 <c>EmptyWorkingSet</c>，把启动时摸过的页也还回去。
/// </para>
/// <para>
/// # 为什么不用 <c>DISABLE_XAML_GENERATED_MAIN</c>
/// </para>
/// <para>
/// 那是"自己写 Main"的老办法，.NET 10 的 WPF 代码生成器已经不再输出
/// <c>#if !DISABLE_XAML_GENERATED_MAIN</c>（实测 PresentationBuildTasks 10.0.402
/// 里连这个字符串都没有了），写了自己的 Main 会得到 CS0017 多个入口点。
/// 模块初始化器能达到同样的效果，还不用碰 csproj 和 XAML 生成物。
/// </para>
/// </summary>
public static class FineGuard
{
    /// <summary>命令行开关。大小写不敏感。</summary>
    public const string Switch = "--fineguard";

    /// <summary>
    /// 解析命令行。不是守护进程、或参数不完整 → <c>false</c>（照常启动 GUI）。
    ///
    /// <para>
    /// 第 4 个参数（主进程启动时间，UTC ticks）可选：老版本守护进程没有它，
    /// 按 0 处理表示"只按 pid 认人"。
    /// </para>
    /// </summary>
    public static bool TryParse(string[] args, out int pid, out int lines, out long startTicks)
    {
        pid = 0;
        lines = 0;
        startTicks = 0;
        if (args is null || args.Length == 0) return false;

        var index = Array.FindIndex(args, a => a.Equals(Switch, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || args.Length <= index + 2) return false;
        if (!int.TryParse(args[index + 1], out pid)) return false;
        if (!int.TryParse(args[index + 2], out lines)) return false;
        if (args.Length > index + 3) long.TryParse(args[index + 3], out startTicks);
        return true;
    }

    /// <summary>
    /// 是守护进程就**等完、还原完，然后结束进程**（返回 <c>true</c> 只是为了让调用点
    /// 读起来完整；正常路径上不会再回到调用方）；不是就立刻返回 <c>false</c>。
    /// </summary>
    public static bool TryRun(string[] args)
    {
        if (!TryParse(args, out var pid, out var lines, out var startTicks)) return false;

        try
        {
            /* ⚠ 这里**不能**先还原：主进程此刻正在用细腻模式（系统 = 1 行）。
             * 只等它退出，退出后再还原。 */
            if (WaitOwnerExit(pid, startTicks))
            {
                SystemWheelLines.RestoreLines(lines);

                /* 还原完把备份清掉：否则下次启动还会"发现上次没退干净"再还原一遍
                 * （虽无害，但日志会误报，也会让用户以为又出问题了）。 */
                try { SystemWheelLines.ClearBackupAfterGuard(); } catch { }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("细腻模式守护进程异常（系统行数保持现状，下次启动自愈）：" + ex.Message);
        }

        return true;
    }

    /// <summary>
    /// 等主进程退出。<c>false</c> = 这个 pid 已经被别人复用了，**不要还原**
    /// （原主早就不在了，行数交给下次启动的自愈逻辑）。
    /// </summary>
    private static bool WaitOwnerExit(int pid, long startTicks)
    {
        try
        {
            using var owner = Process.GetProcessById(pid);
            if (owner.HasExited) return true;

            if (startTicks > 0 &&
                owner.StartTime.ToUniversalTime().Ticks != startTicks)
            {
                Log.Warn($"细腻模式守护：pid {pid} 已被复用，退出（不还原）");
                return false;
            }

            /* 等之前先把工作集还回去：CLR 启动时摸过的那些页后面一次都不会再碰，
             * 留着只是让任务管理器上的数字难看。 */
            MemoryTrim.TrimNow();

            owner.WaitForExit();   // 主进程活着就一直等（不占 CPU）
            return true;
        }
        catch
        {
            /* 进程已经没了（或者根本没起来）：直接还原。 */
            return true;
        }
    }
}

/// <summary>
/// 模块初始化器：CLR **在进入 Main 之前**跑这里。
///
/// <para>
/// 守护进程在这里分流就能避开 WPF：<c>Application</c> 还没被 new 出来，
/// App.xaml / 资源字典 / PresentationFramework 一个都不会加载。
/// 其余参数（<c>--selftest</c>、<c>--frametrace</c>、正常启动……）原样落到
/// 生成的 Main → <c>App.OnStartup</c>，行为一字未改。
/// </para>
/// </summary>
internal static class FineGuardModule
{
    [ModuleInitializer]
    internal static void Init()
    {
        /* 初始化器里抛异常会让进程以 TypeInitializationException 死掉、
         * 用户看到"双击没反应"。这里的任何意外都必须退回正常启动。 */
        try
        {
            var args = Environment.GetCommandLineArgs();
            if (!FineGuard.TryParse(args, out _, out _, out _)) return;

            FineGuard.TryRun(args);
            Environment.Exit(0);
        }
        catch { /* 解析/等待失败 → 让它照常进 Main */ }
    }
}
