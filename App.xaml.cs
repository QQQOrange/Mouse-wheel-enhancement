using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using MouseWheelEnhancement.Core;
using MouseWheelEnhancement.Update;

namespace MouseWheelEnhancement;

public partial class App : System.Windows.Application
{
    private const string MutexName = @"Local\MouseWheelEnhancement.SingleInstance";
    private const string ActivateEventName = @"Local\MouseWheelEnhancement.Activate";

    private Mutex? _mutex;
    private EventWaitHandle? _activateEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 自检模式：不建窗口、不装钩子，只跑纯逻辑断言然后退出
        if (e.Args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            var code = SelfTest.Run();
            Shutdown(code);
            Environment.Exit(code);
            return;
        }

        // 冒烟模式：真的把这套东西拉起来（装钩子、建托盘、实例化窗口、逐页布局），
        // 然后立刻退出。给"改完 XAML 到底能不能跑"一个不用肉眼盯的答案。
        if (e.Args.Any(a => a.Equals("--smoke", StringComparison.OrdinalIgnoreCase)))
        {
            var code = SmokeTest();
            Shutdown(code);
            Environment.Exit(code);
            return;
        }

        // 单实例：第二个进程只负责"把已有窗口叫出来"，然后自己退出
        _mutex = new Mutex(true, MutexName, out var isFirst);
        if (!isFirst)
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var other))
                    other.Set();
            }
            catch { /* 叫不动就算了，至少不重复起一份 */ }
            Shutdown();
            return;
        }
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        StartActivateListener();

        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("未处理的域异常", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("未观察的任务异常", args.Exception);
            args.SetObserved();
        };

        try
        {
            Services.Init();
        }
        catch (Exception ex)
        {
            Log.Error("初始化失败", ex);
            MessageBox.Show($"初始化失败：{ex.Message}\n\n日志：{Log.FilePath}",
                Services.AppTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
            return;
        }

        var startMinimized = e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        if (!startMinimized) ShowMainWindow();

        if (Services.Config.Update.AutoCheck) _ = AutoCheckAsync();
    }

    /// <summary>
    /// 冒烟测试：把引擎、托盘、窗口、七个页面全部真的建一遍，
    /// 再用注入的"外来"滚轮事件把 钩子 → 判定 → 平滑 → 注入 整条链路打通一次，
    /// 最后退出。返回 0 表示这条链路没有异常。
    ///
    /// 存在的意义：XAML 的错误、钩子装不上、注入被拦，这些都不会在编译期暴露，
    /// 但都能在这里变成一句明确的失败，而不是等用户装上之后说"没反应"。
    /// </summary>
    private static int SmokeTest()
    {
        var code = 0;
        try
        {
            Services.Init();

            var w = new MainWindow();
            w.Show();
            w.UpdateLayout();

            for (var i = 0; i < 7; i++)
            {
                w.SelectTab(i);
                w.UpdateLayout();
                Log.Info($"smoke: tab{i} → {w.LayoutReport()}");
            }

            w.SelectTab(1);
            w.UpdateLayout();
            RedrawCurveNow(w);

            if (!Services.Engine.IsRunning)
            {
                Log.Error("smoke: 钩子没装上");
                code = 4;
            }

            /* 端到端驱动：用一个**不属于本程序**的标记注入滚轮，让它像真滚轮一样
             * 先经过我们的钩子。命中判定后 BoostedCount 会涨，注入链路也一并验证。 */
            var beforeBoost = Services.Engine.BoostedCount;
            var beforePass = Services.Engine.PassedCount;
            var sent = 0;
            for (var i = 0; i < 16; i++)
            {
                if (InputInjector.SendWheelWith(Native.WHEEL_DELTA, ForeignSignature)) sent++;
                Thread.Sleep(18);
            }
            Thread.Sleep(400);

            var boosted = Services.Engine.BoostedCount - beforeBoost;
            var passed = Services.Engine.PassedCount - beforePass;
            Log.Info($"smoke: 注入 {sent}/16 次，钩子接管 {boosted} 次、放行 {passed} 次，" +
                     $"倍率 {Services.Engine.LastMultiplier:0.00}×");

            if (sent == 0)
            {
                Log.Warn("smoke: 注入全部失败（多半是桌面被锁或没有交互会话），跳过链路判定");
            }
            else if (boosted == 0)
            {
                Log.Error("smoke: 注入成功但钩子没有接管 —— 链路没通");
                code = 5;
            }

            w.Hide();
            Services.Shutdown();
        }
        catch (Exception ex)
        {
            code = 3;
            Log.Error("smoke: 启动链路抛异常", ex);
            try { File.WriteAllText(Path.Combine(AppPaths.DataDir, "smoke-error.txt"), ex.ToString()); }
            catch { }
            Log.Flush();
        }
        return code;
    }

    /// <summary>冒烟测试专用的"外来"标记：不能等于 <c>InputInjector.Signature</c>。</summary>
    private static readonly IntPtr ForeignSignature = (IntPtr)0x5EE0;

    private static void RedrawCurveNow(MainWindow w)
    {
        // 触发一次曲线重绘（切到「加速」页时正常会自己画，这里确保画过）
        w.SelectTab(0);
        w.UpdateLayout();
        w.SelectTab(1);
        w.UpdateLayout();
    }

    private void StartActivateListener()
    {
        var th = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    if (_activateEvent == null || !_activateEvent.WaitOne()) return;
                    Dispatcher.BeginInvoke(new Action(() => ShowMainWindow()));
                }
                catch { return; }
            }
        })
        { IsBackground = true, Name = "MouseWheelEnhancement-Activate" };
        th.Start();
    }

    public void ShowMainWindow(int tabIndex = -1)
    {
        if (Services.Window == null)
        {
            Services.Window = new MainWindow();
            Services.Window.Closed += (_, _) => Services.Window = null;
            Services.Window.Show();
        }
        else
        {
            if (Services.Window.WindowState == WindowState.Minimized)
                Services.Window.WindowState = WindowState.Normal;
            Services.Window.Show();
            Services.Window.Activate();
        }

        if (tabIndex >= 0) Services.Window.SelectTab(tabIndex);
        Services.Window.Topmost = true;
        Services.Window.Topmost = false;
    }

    /// <summary>启动后静默查一次更新：没新版**什么都不弹**（用户明确要求）。</summary>
    private async Task AutoCheckAsync()
    {
        try
        {
            await Task.Delay(2500); // 别和启动抢资源
            var outcome = await Services.Updater.CheckAsync(Services.Version);
            if (!outcome.Ok)
            {
                Log.Warn("自动检查更新失败：" + outcome.Error);
                return;
            }
            var info = outcome.Info!;
            Services.Config.Update.LastCheckUtc = DateTime.UtcNow.ToString("o");
            Services.Store.Touch(notify: false);

            if (!info.Newer) { Log.Info($"自动检查：已是最新（{Services.Version}）"); return; }

            Services.PendingUpdate = info;
            Log.Info($"自动检查：发现新版 v{info.Version}");
            Services.Tray?.NotifyUpdateAvailable(info.Version);
            Services.Window?.ShowUpdateInfo(info, fromAutoCheck: true);
        }
        catch (Exception ex)
        {
            Log.Warn("自动检查更新异常：" + ex.Message);
        }
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("界面线程未处理异常", e.Exception);
        // 界面层的小毛病不该把整个工具带走 —— 它还在替用户接管滚轮
        e.Handled = true;
        if (e.Exception is not InvalidOperationException)
        {
            MessageBox.Show($"出了个小问题（已记录日志）：{e.Exception.Message}\n\n日志：{Log.FilePath}",
                Services.AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>真正的退出入口（托盘菜单、安装完更新重启都用它）。</summary>
    public static void ExitApp(int exitCode = 0)
    {
        Services.Shutdown();
        if (Current is App app)
        {
            app._mutex?.ReleaseMutex();
            app._mutex?.Dispose();
            app._mutex = null;
            app._activateEvent?.Dispose();
            app._activateEvent = null;
            app.Shutdown(exitCode);
        }
        Environment.Exit(exitCode);
    }

    /// <summary>下载完更新后：先启动新版本，再退出自己。</summary>
    public static void RestartForUpdate()
    {
        if (!Services.Updater.RestartApp())
        {
            MessageBox.Show("新版本已经就位，但自动启动失败 —— 请手动重新打开程序。",
                Services.AppTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ExitApp();
    }

    /// <summary>打开外部链接（关于页的 GitHub 地址等）。</summary>
    public static void OpenExternal(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"打开链接失败 {url}：{ex.Message}");
        }
    }
}
