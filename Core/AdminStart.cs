using System.Runtime.InteropServices;

namespace MouseWheelEnhancement.Core;

/// <summary>
/// 「以管理员身份运行」的**开机自启那一半** —— 用 Windows 计划任务实现。
///
/// <para>
/// 另一半（手动双击启动）不走计划任务：进程启动时发现自己是普通权限，
/// 就自己用 <c>runas</c> 提权重启一次。两条路合起来才是设置里那一个开关的完整含义，
/// 见 <see cref="ShouldElevate"/> 与 DESIGN 第 27 节。
/// </para>
///
/// <para>
/// 为什么不能用注册表：<c>HKCU\...\Run</c> 里的程序**一律以普通权限启动**，
/// 注册表没有"以管理员运行"这个字段。想让它在登录时就是管理员，只有两条路：
/// </para>
/// <list type="number">
///   <item>计划任务 + <c>RunLevel=HighestAvailable</c>：登录时由任务计划程序直接以
///         高完整性级别拉起，**不弹 UAC**（提权授权在创建任务时就完成了）。</item>
///   <item>往 Run 项里放一个自己 <c>ShellExecute(runas)</c> 的壳：每次开机弹一次 UAC。</item>
/// </list>
/// <para>这里用第一条。第二条对"开机自启"这种用户根本没在看屏幕的时刻不可接受。</para>
///
/// <para>
/// 实现走 <b>COM（Schedule.Service）</b>而不是 <c>schtasks.exe</c>：
/// </para>
/// <list type="bullet">
///   <item>少一个外部进程。安全软件/组策略把 <c>schtasks.exe</c> 拉黑是很常见的
///         （企业环境尤其如此），而那时功能会整体失效且原因晦涩。</item>
///   <item>不用解析本地化的命令行输出 —— 中文 Windows 和英文 Windows 的提示文案完全不同。</item>
///   <item>能直接读到任务的动作路径，用来发现"程序被挪了目录、任务还指着旧路径"。</item>
/// </list>
/// </summary>
public static class AdminStart
{
    /// <summary>计划任务名（在「任务计划程序」里显示为这个，用户能自己找到并删掉）。</summary>
    public const string TaskName = "鼠标滚轮增强（管理员）";

    /// <summary>
    /// 启动命令行的"这次启动已经试过提权"标记。
    ///
    /// <para>
    /// 提权重启的接班人带上它，用来断掉死循环：万一提权没生效（组策略把
    /// <c>runas</c> 拦了、UAC 被点了取消、镜像里根本没有提权能力），
    /// 新进程会发现自己仍然不是管理员 —— 没有这个标记的话它会再提权一次，
    /// 于是用户面前会无限循环地弹确认框。带标记的这次不再尝试，直接以普通权限跑。
    /// </para>
    /// </summary>
    public const string ElevateAttemptArg = "--elevate-attempt";

    /// <summary>
    /// 启动器把自己的路径通过这个环境变量传给主程序。
    /// <para>见 <see cref="StableEntryPath"/>。</para>
    /// </summary>
    public const string LauncherPathEnv = "MWE_LAUNCHER_PATH";

    /* COM 常量（taskschd.h） */
    private const int TaskCreateOrUpdate = 6;
    private const int LogonInteractiveToken = 3;
    private const int RunLevelHighest = 1;
    private const int TriggerLogon = 9;
    private const int ActionExec = 0;
    private const int MultipleInstancesIgnoreNew = 0;

    /// <summary>查询计划任务是否已注册。不需要管理员权限。</summary>
    public static bool IsEnabled()
    {
        try
        {
            dynamic folder = RootFolder();
            try
            {
                folder.GetTask(TaskName);
                return true;
            }
            catch { return false; }
        }
        catch (Exception ex)
        {
            // 任务计划服务本身不可用（极其少见），当作"没开"而不是"开着"
            Log.Warn("查询计划任务失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 启动器 exe 的路径。由 <see cref="App"/> 在启动时从环境变量和配置里解析后赋值。
    /// <para>放在 Core 里是为了让 <see cref="StableEntryPath"/> 不必反向依赖 <c>Services</c>。</para>
    /// </summary>
    public static string? LauncherPath { get; set; }

    /// <summary>
    /// 开机自启任务应该指向的那个 exe —— **稳定入口**。
    ///
    /// <para>
    /// 这里踩过一个很隐蔽的坑：主程序本体并不住在用户放软件的那个文件夹里，
    /// 它是启动器释放到 <c>%LOCALAPPDATA%\MouseWheelEnhancement\app\&lt;版本&gt;\</c>
    /// 的。版本一升级，启动器就会把旧版本目录**删掉**。
    /// </para>
    /// <para>
    /// 如果计划任务指向的是那个版本目录里的主程序，那么：
    /// 任务还在（开关显示"开"）、配置也没错，但目标文件已经没了 →
    /// 登录时任务启动失败（0x80070002），用户只会看到"开机没起来"。
    /// 实测就是这样：任务指向 <c>app\1.4.0\...</c>，而那个目录早被清理了。
    /// </para>
    /// <para>
    /// 正确的目标是**启动器 exe**：它永远待在用户放软件的那个文件夹里，升级不换位置；
    /// 而且由它来启动，开机那次也能走"释放最新版本"的流程。
    /// </para>
    /// </summary>
    public static string StableEntryPath
    {
        get
        {
            try
            {
                var env = Environment.GetEnvironmentVariable(LauncherPathEnv);
                if (!string.IsNullOrWhiteSpace(env))
                {
                    var p = env.Trim();
                    if (File.Exists(p)) return p;
                }
            }
            catch { /* 环境变量读不到就往下走 */ }

            try
            {
                var known = LauncherPath;
                if (!string.IsNullOrWhiteSpace(known) && File.Exists(known)) return known;
            }
            catch { }

            return Update.UpdateService.ExePath;
        }
    }

    /// <summary>任务当前实际指向的路径（诊断用；取不到返回 null）。</summary>
    public static string? TaskTargetPath()
    {
        try
        {
            dynamic folder = RootFolder();
            dynamic task = folder.GetTask(TaskName);
            dynamic actions = task.Definition.Actions;
            for (var i = 1; i <= actions.Count; i++)
            {
                try
                {
                    dynamic a = actions[i];
                    var p = a.Path as string;
                    if (!string.IsNullOrWhiteSpace(p)) return p;
                }
                catch { /* 非 Exec 动作没有 Path */ }
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 我们**是否真的知道**稳定入口是谁。
    ///
    /// <para>
    /// 从启动器启动时有 <c>MWE_LAUNCHER_PATH</c>；以后有配置里记下的路径。
    /// 两者都没有（用户直接双击版本目录里的主程序、或者从编译输出目录跑诊断）时，
    /// <see cref="StableEntryPath"/> 只能退化成"当前 exe" —— 那是个**猜测**，
    /// 拿它去判"任务指错了没有"会误报（明明任务指着正确的启动器）。
    /// </para>
    /// </summary>
    public static bool HasKnownEntry
    {
        get
        {
            try
            {
                var env = Environment.GetEnvironmentVariable(LauncherPathEnv);
                if (!string.IsNullOrWhiteSpace(env) && File.Exists(env.Trim())) return true;
            }
            catch { }

            var known = LauncherPath;
            return !string.IsNullOrWhiteSpace(known) && File.Exists(known);
        }
    }

    /// <summary>
    /// 任务是不是**已经失效**了 —— 开关显示"开"，但开机其实起不来。
    ///
    /// <para>
    /// 两种失效：① 指向的文件已经不存在（版本目录被清理）；
    /// ② 指向的不是稳定入口（升级后会被清理的位置）。
    /// </para>
    /// <para>
    /// 第 ② 条只在**我们知道稳定入口是谁**时才判 —— 否则
    /// <see cref="StableEntryPath"/> 只是"当前 exe"这个猜测，会误报。
    /// </para>
    /// </summary>
    public static bool IsStale()
    {
        if (!IsEnabled()) return false;

        var target = TaskTargetPath();
        if (string.IsNullOrWhiteSpace(target)) return true;      // 任务没有可执行动作
        if (!File.Exists(target)) return true;                    // 目标文件没了（铁证）

        if (!HasKnownEntry) return false;                         // 不知道正确目标，不下判定
        return !string.Equals(target, StableEntryPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 任务里的启动路径是否就是**稳定入口**。
    /// 程序被挪了目录、或者升级换了版本目录之后任务还在、但指向旧路径 ——
    /// 那是"看起来开着、其实开机不启动"，比彻底关掉更难发现，所以要能查出来并覆盖重写。
    /// <para>
    /// 名字特意从 <c>PointsToCurrentExe</c> 改过来：本轮那个 bug 的根源就是
    /// "任务指向当前 exe"这个错误概念 —— 当前 exe 住在版本目录里，升级即失效。
    /// </para>
    /// </summary>
    public static bool PointsToStableEntry()
    {
        var target = TaskTargetPath();
        if (string.IsNullOrWhiteSpace(target)) return false;
        return string.Equals(target, StableEntryPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 这个路径是不是"版本目录里的主程序本体" —— 也就是
    /// <c>%LOCALAPPDATA%\MouseWheelEnhancement\app\&lt;版本&gt;\MouseWheelEnhancement.App.exe</c>。
    ///
    /// <para>
    /// 这类路径**绝对不能**写进开机自启：启动器升级时会
    /// <c>CleanOldVersions</c> 把整个版本目录删掉，任务当场静默失效
    /// （实测就是这样丢掉过一次用户的自启）。
    /// </para>
    /// </summary>
    public static bool IsVersionedBody(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var verDir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(verDir)) return false;
            var appDir = Path.GetDirectoryName(verDir);
            if (string.IsNullOrEmpty(appDir)) return false;

            // …\app\<版本号>\xxx.exe —— 两个条件都要满足，避免误伤 D:\app\1.0\ 这种命名
            return string.Equals(Path.GetFileName(appDir), "app", StringComparison.OrdinalIgnoreCase)
                   && Version.TryParse(Path.GetFileName(verDir), out _);
        }
        catch { return false; }
    }

    /// <summary>
    /// 这次启动要不要自己提权。
    ///
    /// <para>
    /// 抽成纯函数是为了能在自检里把四种组合都断言一遍 —— 这个判断错了的表现是
    /// "用户明明开了开关，界面还说不是管理员"，肉眼很难验证。
    /// </para>
    /// </summary>
    /// <param name="isElevated">当前进程是不是已经是管理员。</param>
    /// <param name="runAsAdmin">配置里的「以管理员身份运行」。</param>
    /// <param name="alreadyTried">命令行里已经带了 <see cref="ElevateAttemptArg"/>（上一轮试过了）。</param>
    public static bool ShouldElevate(bool isElevated, bool runAsAdmin, bool alreadyTried)
        => runAsAdmin && !isElevated && !alreadyTried;

    /// <summary>
    /// 创建（<paramref name="on"/> = true）或删除计划任务。
    /// <para>**创建和删除都需要管理员权限**，调用方必须先确认 <see cref="Native.IsElevated"/>。</para>
    /// </summary>
    /// <param name="error">失败原因（已翻成人话，可以直接显示给用户）。</param>
    public static bool Set(bool on, out string? error)
    {
        error = null;
        try
        {
            /* 写任务之前先自检目标路径。
             *
             * 这个判断是为了堵一条**反向**破坏路径：万一稳定入口解析不到
             * （启动器被挪走、环境变量和配置都失效），StableEntryPath 会退化成
             * 版本目录里的主程序本体 —— 那正是"升级即失效"的那个路径。
             * 与其把任务写坏、让用户下次开机又静默失败，不如当场拒绝并说清楚。 */
            var target = StableEntryPath;
            if (on && IsVersionedBody(target))
            {
                error = "找不到启动器（MouseWheelEnhancement.exe）—— 现在只能定位到版本目录里的主程序，" +
                        "把它设成开机自启的话，下次升级就会失效。请从安装目录的 " +
                        "MouseWheelEnhancement.exe 启动一次再设置。";
                Log.Warn($"拒绝把开机自启指向版本目录：{target}");
                return false;
            }

            dynamic svc = Service();
            dynamic folder = svc.GetFolder("\\");

            if (!on)
            {
                try { folder.DeleteTask(TaskName, 0); }
                catch (Exception del) when ((uint)del.HResult == 0x80070002) { /* 本来就没有，算成功 */ }
                return true;
            }

            var user = Environment.UserDomainName + "\\" + Environment.UserName;

            dynamic def = svc.NewTask(0);

            def.RegistrationInfo.Description =
                "以管理员身份启动「鼠标滚轮增强」，让它对管理员程序的窗口也生效。" +
                "此任务由程序在设置里创建，关掉那个开关即可移除。";
            def.RegistrationInfo.Author = user;

            def.Principal.UserId = user;
            def.Principal.LogonType = LogonInteractiveToken;
            def.Principal.RunLevel = RunLevelHighest;   // ← 这就是"以管理员运行"

            /* 这几项默认值都会坑人，必须显式关掉：
             *   DisallowStartIfOnBatteries —— 默认 true，笔记本拔电源就不启动
             *   ExecutionTimeLimit          —— 默认 3 天，任务计划程序会主动结束进程
             *   StopIfGoingOnBatteries      —— 切到电池时结束进程
             *   MultipleInstances           —— 程序自己还有单实例互斥，这里是第二道保险 */
            def.Settings.MultipleInstances = MultipleInstancesIgnoreNew;
            def.Settings.DisallowStartIfOnBatteries = false;
            def.Settings.StopIfGoingOnBatteries = false;
            def.Settings.ExecutionTimeLimit = "PT0S";   // 0 = 不限时
            def.Settings.Enabled = true;
            def.Settings.RunOnlyIfIdle = false;
            def.Settings.StartWhenAvailable = false;
            def.Settings.WakeToRun = false;
            def.Settings.IdleSettings.StopOnIdleEnd = false;

            def.Triggers.Clear();
            dynamic trigger = def.Triggers.Create(TriggerLogon);
            trigger.UserId = user;
            // 登录后等 8 秒再起：开机瞬间抢 IO 会拖慢桌面加载，而托盘图标晚几秒没人注意
            trigger.Delay = "PT8S";
            trigger.Enabled = true;

            def.Actions.Clear();
            dynamic action = def.Actions.Create(ActionExec);
            action.Path = target;            // ← 稳定入口，不是版本目录里的主程序
            action.Arguments = "--minimized";

            folder.RegisterTaskDefinition(TaskName, def, TaskCreateOrUpdate,
                user, null, LogonInteractiveToken, null);
            return true;
        }
        catch (Exception ex)
        {
            error = Explain(ex);
            Log.Warn($"设置计划任务失败：{ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            return false;
        }
    }

    /// <summary>连上任务计划服务，返回根目录（<c>\</c>）。</summary>
    private static dynamic RootFolder()
    {
        dynamic svc = Service();
        return svc.GetFolder("\\");
    }

    /// <summary>
    /// 连上任务计划服务。
    /// <para>
    /// 刻意**不缓存**这个 COM 对象：它绑定在创建线程的 apartment 上，
    /// 缓存之后从别的线程用会抛 <c>RPC_E_WRONG_THREAD</c>。
    /// 重新创建一次只要十几毫秒，而它一天也调不了几次。
    /// </para>
    /// </summary>
    private static dynamic Service()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service")
                   ?? throw new InvalidOperationException("系统里找不到「任务计划程序」服务（Schedule.Service）");

        dynamic svc = Activator.CreateInstance(type)!;
        svc.Connect();   // 不带参数 = 连本机、当前用户
        return svc;
    }

    /// <summary>
    /// 把异常翻成人话。
    ///
    /// <para>
    /// 不能只认 <c>COMException</c>：.NET 的 COM 互操作会把某些 HRESULT 映射成对应的
    /// BCL 异常类型 —— <c>E_ACCESSDENIED</c> 会变成 <c>UnauthorizedAccessException</c>。
    /// 实测就是这样：只 catch COMException 的话，用户看到的是
    /// "拒绝访问。(0x80070005 (E_ACCESSDENIED))" 这种原始输出，
    /// 而不是"请以管理员身份运行"。
    /// </para>
    /// </summary>
    private static string Explain(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            var text = (uint)e.HResult switch
            {
                0x80070005 => "没有权限创建计划任务 —— 需要管理员权限。",
                0x80070002 => "系统里找不到「任务计划程序」服务。",
                0x80041315 => "任务计划程序服务没有在运行，请检查系统服务里的 Task Scheduler。",
                0x8004131F => "计划任务已经存在（同名），请先在「任务计划程序」里删掉它。",
                0x8007000E => "内存不足，创建计划任务失败。",
                _ => null,
            };
            if (text != null) return text;
        }
        return "设置计划任务失败：" + ex.Message;
    }
}
