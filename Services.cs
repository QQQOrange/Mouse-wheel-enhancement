using System.Windows;
using MouseWheelEnhancement.Core;
using MouseWheelEnhancement.Update;

namespace MouseWheelEnhancement;

/// <summary>
/// 组装根：进程内只有一份配置、一个引擎、一个更新器。
/// 所有模块都从这里拿，不各自 new，否则会出现"界面改的是这份配置、
/// 引擎读的是另一份"这种最难查的分裂。
/// </summary>
internal static class Services
{
    public const string AppTitle = "鼠标滚轮增强";
    public const string AppTitleEn = "Mouse wheel enhancement";

    public static ConfigStore Store { get; private set; } = null!;
    public static WheelEngine Engine { get; private set; } = null!;
    public static UpdateService Updater { get; private set; } = null!;
    public static TrayController? Tray { get; private set; }
    public static MainWindow? Window { get; internal set; }

    /// <summary>当前进程是不是管理员 —— 决定能不能接管"以管理员运行的窗口"。</summary>
    public static bool IsElevated { get; private set; }

    /// <summary>后台检查发现的新版本，界面打开时直接显示。</summary>
    public static UpdateInfo? PendingUpdate { get; set; }

    /// <summary>配置或运行状态变了（界面据此刷新，不用轮询）。</summary>
    public static event Action? StateChanged;

    public static AppConfig Config => Store.Config;
    public static string Version => Log.Version;

    /// <summary>
    /// 完整初始化（配置 + 引擎 + 托盘）。探针、冒烟测试这些"一条龙跑完就退出"的
    /// 场景用它；正常启动走 <see cref="InitConfig"/> →（可能需要提权重启）→ <see cref="InitRuntime"/>。
    /// </summary>
    public static void Init()
    {
        InitConfig();
        InitRuntime();
    }

    /// <summary>
    /// 第一阶段：目录、日志、配置、主题、刷新率。
    ///
    /// <para>
    /// 和 <see cref="InitRuntime"/> 分开是有原因的：程序启动时可能要先判断
    /// "配置里开了「以管理员身份运行」，但现在不是管理员，得提权重启"。
    /// 如果配置文件还没读就先装了钩子、建了托盘图标，提权重启的那一刻
    /// 用户会看到托盘图标闪一下又消失；那一步只需要配置。
    /// </para>
    /// </summary>
    public static void InitConfig()
    {
        // 先把数据目录解析出来（可能要判可写性），日志才知道往哪写
        _ = AppPaths.DataDir;
        Log.Init();

        if (AppPaths.IsFallback)
            Log.Warn($"程序目录不可写，数据改存到：{AppPaths.DataDir}");
        Log.Info($"数据目录：{AppPaths.DataDir}");

        IsElevated = Native.IsElevated();

        Store = new ConfigStore();
        Store.Changed += OnConfigChanged;

        /* 主题要在**建窗口之前**应用：App.xaml 默认合并的是深色调色板，
         * 如果等窗口建完再换，浅色系统上会先闪一下深色。
         * 这里只是替换一份资源字典，窗口都还没建，所以是零成本的。 */
        _themeMode = Theme.Normalize(Store.Config.Appearance.Theme);
        Theme.Apply(_themeMode);
        Theme.Start(_themeMode);

        /* 帧率要跟着显示器走：先把当前刷新率读出来，再让引擎按它烘焙第一份快照。
         * 之后刷新率一变（换分辨率、插拔电源、动态刷新率），重建快照即可 —— 
         * 动画线程每帧都会读最新的那份。 */
        RefreshRateMonitor.Start();
    }

    /// <summary>
    /// 第二阶段：装钩子、起引擎、建托盘图标。
    /// <para>调用前 <see cref="InitConfig"/> 必须已经跑过（引擎要用配置里的参数）。</para>
    /// </summary>
    public static void InitRuntime()
    {
        Engine = new WheelEngine();
        Engine.Apply(Store.Config);

        /* 订阅要放在 Engine 建好之后：这个回调会用到 Engine，
         * 早订一毫秒就多一次 NullReference 的机会（刷新率变化正好撞在初始化窗口上）。 */
        RefreshRateMonitor.Changed += _ =>
        {
            Engine.Apply(Config);
            Notify();
        };
        Engine.Faulted += msg =>
        {
            Log.Error("引擎异常：" + msg);
            Notify();
        };

        Updater = new UpdateService(Store);
        UpdateService.SweepLeftovers(); // 清上次更新留下的 .old / .new

        var ok = Engine.Start();
        Log.Info(ok ? "引擎已启动" : "引擎启动失败（钩子没装上）");

        try
        {
            Tray = new TrayController();
        }
        catch (Exception ex)
        {
            // 托盘没了很不方便，但还不至于让增强本身不可用
            Log.Error("托盘图标创建失败（界面仍可正常打开）", ex);
        }
    }

    private static void OnConfigChanged(AppConfig cfg)
    {
        Engine.Apply(cfg);

        /* 主题单独处理：只有真的换了模式才动资源字典。
         * 拖个滑块也走这里，每次重建字典会让整棵视觉树重新解析一遍颜色，白闪一下。 */
        var mode = Theme.Normalize(cfg.Appearance.Theme);
        if (mode != _themeMode)
        {
            _themeMode = mode;
            Theme.SetMode(mode);
        }

        Notify();
    }

    private static string _themeMode = Theme.Normalize(new AppConfig().Appearance.Theme);

    public static void Notify() => StateChanged?.Invoke();

    public static void SetEnabled(bool on)
    {
        if (Config.Enabled == on) return;
        Config.Enabled = on;
        Store.Touch(); // → OnConfigChanged → 引擎 + 界面
        Log.Info(on ? "功能已启用" : "功能已暂停");
    }

    public static void ToggleEnabled() => SetEnabled(!Config.Enabled);

    /// <summary>把某个 exe 加入/移出当前名单（托盘里的"在当前应用中禁用"）。</summary>
    public static string ToggleAppInList(string exe)
    {
        var name = AppRuleName.Normalize(exe);
        if (name.Length == 0) return "没有识别出当前应用";

        var apps = Config.Filter.Apps;
        var hit = apps.FirstOrDefault(a => string.Equals(a.Exe, name, StringComparison.OrdinalIgnoreCase));
        if (hit != null)
        {
            apps.Remove(hit);
            Store.Touch();
            return $"已从名单移除 {name}";
        }

        apps.Add(new AppRule { Exe = name, Note = "手动添加" });
        Store.Touch();
        return $"已加入名单 {name}";
    }

    public static bool IsAppListed(string exe)
    {
        var name = AppRuleName.Normalize(exe);
        return Config.Filter.Apps.Any(a => string.Equals(a.Exe, name, StringComparison.OrdinalIgnoreCase));
    }

    public static void ShowWindow(int tabIndex = -1)
    {
        if (Application.Current is not App app) return;

        var dispatcher = app.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted) return;

        /* 必须封送到 UI 线程再动窗口。
         *
         * 托盘那条链路不是从 UI 线程过来的：TrayController.ScheduleShow 用的是
         * `Task.Delay(...).ContinueWith(..., TaskScheduler.Default)` ——
         * 显式指定了线程池，回调跑在 MTA 线程上。
         * 在 MTA 线程上 `new MainWindow()` 会抛"调用线程必须为 STA"，
         * 异常被 ShowMainWindow 的 catch 吃掉 → 用户看到的是
         * **"点托盘图标没有任何反应"**，日志里只有一行 WARN。
         *
         * 所有调用方（托盘单击/双击、托盘菜单、单实例唤起的外部进程）都从这里进，
         * 所以把封送放在这一层，而不是要求每个调用方自己记得。 */
        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(() =>
            {
                try { app.ShowMainWindow(tabIndex); }
                catch (Exception ex) { Log.Warn("托盘打开窗口失败：" + ex.Message); }
            }));
            return;
        }

        app.ShowMainWindow(tabIndex);
    }

    public static void Shutdown()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        void Mark(string label) => Log.Info($"退出·{label}：{sw.ElapsedMilliseconds} ms");

        try { Engine?.Dispose(); } catch { }
        Mark("引擎");

        try { Store?.SaveNow(); } catch { }
        Mark("保存配置");
        try { Store?.Dispose(); } catch { }
        try { Tray?.Dispose(); } catch { }
        Mark("托盘");
        try { RefreshRateMonitor.Stop(); } catch { }
        try { Theme.Stop(); } catch { }
        Mark("监听/主题");

        Log.Info("==== 退出 ====");
        Log.Flush();
        Mark("刷日志");
    }
}
