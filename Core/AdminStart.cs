using System.Runtime.InteropServices;

namespace MouseWheelEnhancement.Core;

/// <summary>
/// 「以管理员身份开机启动」——用 Windows 计划任务实现。
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
    /// 任务里的启动路径是否就是**当前这个 exe**。
    /// 程序被挪了目录之后任务还在、但指向旧路径 —— 那是"看起来开着、其实开机不启动"，
    /// 比彻底关掉更难发现，所以要能查出来并覆盖重写。
    /// </summary>
    public static bool PointsToCurrentExe()
    {
        try
        {
            dynamic folder = RootFolder();
            dynamic task = folder.GetTask(TaskName);
            dynamic actions = task.Definition.Actions;
            var exe = Update.UpdateService.ExePath;

            // COM 集合是 1-based
            for (var i = 1; i <= actions.Count; i++)
            {
                try
                {
                    dynamic a = actions[i];
                    if (string.Equals((string)a.Path, exe, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch { /* 非 Exec 动作没有 Path，跳过 */ }
            }
            return false;
        }
        catch { return false; }
    }

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
            action.Path = Update.UpdateService.ExePath;
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
