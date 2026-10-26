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

    public static void Init()
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

        Engine = new WheelEngine();
        Engine.Apply(Store.Config);
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
        Notify();
    }

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
        app.ShowMainWindow(tabIndex);
    }

    public static void Shutdown()
    {
        try { Engine?.Dispose(); } catch { }
        try { Store?.SaveNow(); } catch { }
        try { Store?.Dispose(); } catch { }
        try { Tray?.Dispose(); } catch { }
        Log.Info("==== 退出 ====");
        Log.Flush();
    }
}
