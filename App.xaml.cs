using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MouseWheelEnhancement.Core;
using MouseWheelEnhancement.Update;

namespace MouseWheelEnhancement;

public partial class App : System.Windows.Application
{
    /// <summary>
    /// 这个参数是不是"跑完就退"的诊断命令。
    ///
    /// <para>
    /// 必须**大小写不敏感**、并且认 <c>--wheeltest lines=3</c> 这种带值的写法 ——
    /// 曾经这里用 <c>DiagnosticArgs.Contains</c>（区分大小写、只认整串），
    /// 结果 <c>--WHEELTEST</c> 或带参数时看门狗不会启动，又回到了会挂死的状态。
    /// </para>
    /// </summary>
    private static bool IsDiagnosticArg(string arg)
    {
        foreach (var d in DiagnosticArgs)
        {
            if (arg.Equals(d, StringComparison.OrdinalIgnoreCase)) return true;
            if (arg.StartsWith(d + " ", StringComparison.OrdinalIgnoreCase)) return true;
            if (arg.StartsWith(d + "=", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private const string MutexName = @"Local\MouseWheelEnhancement.SingleInstance";

    /// <summary>
    /// "跑完就退"的诊断参数。只有它们才需要看门狗 —— 注意
    /// <c>--minimized</c> 是开机自启用的正常参数，绝不能被当成诊断命令。
    /// </summary>
    private static readonly string[] DiagnosticArgs =
    {
        "--selftest", "--measure", "--featurecheck", "--taskcheck", "--frametrace",
        "--dialogtest", "--runtimecheck", "--wheeltest", "--scrollsmooth", "--probe",
        "--osdshot", "--uishot", "--uibase", "--uicheck", "--smoke",
    };
    private const string ActivateEventName = @"Local\MouseWheelEnhancement.Activate";

    /// <summary>
    /// 一行折合多少 DIP。WPF 自己按"行"滚时大约是 16~20 DIP 一行，
    /// 取 20 让"一格"的位移和系统原生滚动基本一致。
    /// </summary>
    internal const double PixelsPerLine = 20.0;

    /// <summary>
    /// <c>--wheeltest</c> 用：把每次像素换算的判据原样记下来。
    ///
    /// <para>
    /// 用户报"一格滚了一页"这类问题时，光看代码分不出是哪一层在滚
    /// （钩子放行后 WPF 自己滚 / 我们换算的像素 / 内层容器抢走），
    /// 所以让这条路径自己留痕。
    /// </para>
    /// </summary>
    internal static readonly System.Collections.Concurrent.ConcurrentQueue<string> WheelDiagLog = new();

    /// <summary>打开上面的留痕。默认关 —— 正常滚动时每帧都会过这里，不能白记。</summary>
    internal static bool WheelDiagOn;

    /// <summary>全局热键的隐藏消息窗口（0×0 的 HwndSource）。null = 没注册成功。</summary>
    private static System.Windows.Interop.HwndSource? _hotkeySource;

    /// <summary>
    /// 「显示主窗口」的重入标记。
    /// 托盘双击会连发 MouseClick×2 + DoubleClick，三次调用可能落在同一次
    /// 消息处理里 —— 没有这道闸，窗口会在自己还没显示完的时候被再次 Show()。
    /// </summary>
    private bool _showingWindow;

    /// <summary>
    /// 启动。没有 <c>OnExit</c> 覆写 —— 曾经那里负责把「每次滚动几行」还原，
    /// 那件事随接管方案一起撤掉了（现在软件启动、退出都不碰这个系统设置）。
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 自检模式：不建窗口、不装钩子，只跑纯逻辑断言然后退出
        /* CLI 诊断命令的统一看门狗。
         *
         * 这些命令都是"跑完就退"的，但它们会拉引擎、装钩子、注入事件、渲染窗口 ——
         * 任何一环卡住（比如另一个实例占着钩子），进程就会**无声地挂着**：
         * 没有输出、没有退出码、自动化脚本只能一直等。实测踩过一次 10 分钟的挂死。
         *
         * 所以：只要带诊断参数，就起一个后台看门狗，120 秒还没结束就写日志并强制退出 90。
         * 正常路径 120 秒足够（最慢的 frametrace 也就几秒）。 */
        if (e.Args.Any(IsDiagnosticArg))
        {
            var watchdog = new System.Threading.Thread(() =>
            {
                System.Threading.Thread.Sleep(TimeSpan.FromSeconds(120));
                try { Log.Error("诊断命令超过 120 秒仍未结束，强制退出（可能是另一个实例占着钩子）"); } catch { }
                try { Log.Flush(500); } catch { }
                Environment.Exit(90);
            })
            { IsBackground = true, Name = "cli-watchdog" };
            watchdog.Start();
        }

        if (e.Args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            var code = SelfTest.Run();
            Shutdown(code);
            Environment.Exit(code);
            return;
        }

        // 测量模式：正常跑起来，等界面稳定后记录资源占用再退出。
        //   --measure        只装托盘（≈ 常驻时的真实占用）
        //   --measure --show 连设置窗口一起（七页全部布局完）
        if (e.Args.Any(a => a.Equals("--measure", StringComparison.OrdinalIgnoreCase)))
        {
            // ⚠ 这里**不能**在设置完定时器后立刻 Shutdown：那会在测量之前就把进程结束掉。
            // 交给 WPF 的正常消息循环跑，定时器到点自己记录并退出。
            StartMeasureMode(e.Args.Any(a => a.Equals("--show", StringComparison.OrdinalIgnoreCase)));
            return;
        }

        // 诊断模式：计划任务（「以管理员身份开机启动」）状态检查
        /* 功能链路检查：验证 v1.8.x 那些"逻辑对、接线可能错"的地方。
         *
         * 放在抢单实例互斥体**之前**：它自己 new 引擎、自己装一次钩子再拆掉，
         * 不建托盘、不碰用户真实配置，所以即使已经有一个实例在跑也能单独跑。
         * 这一点是故意的 —— 排查"覆盖在真实实例里到底生效没有"时，
         * 恰恰替不掉正在运行的那个实例。 */
        if (e.Args.Any(a => a.Equals("--featurecheck", StringComparison.OrdinalIgnoreCase)))
        {
            var code = FeatureCheck.Run(e.Args);
            Shutdown(code);
            Environment.Exit(code);
            return;
        }

        if (e.Args.Any(a => a.Equals("--taskcheck", StringComparison.OrdinalIgnoreCase)))
        {
            var code = TaskCheckCli(e.Args);
            Shutdown(code);
            Environment.Exit(code);
            return;
        }

        /* 诊断模式：把动画线程真跑起来，量"空闲后第一帧发了多少"。
         *
         * 放在抢单实例互斥体**之前**：它不装钩子、不建窗口、不注入，
         * 所以即使已经有一个实例在跑（甚至那个实例正握着互斥体），也能单独跑。
         * 这一点很重要 —— 排查"首帧突发"时，恰恰是替不掉正在运行的实例的场景。 */
        if (e.Args.Any(a => a.Equals("--frametrace", StringComparison.OrdinalIgnoreCase)))
        {
            var code = FrameTrace.Run(e.Args);
            Shutdown(code);
            Environment.Exit(code);
            return;
        }

        // 诊断模式：只查一次更新并打印结果（不装钩子、不建窗口）
        if (e.Args.Any(a => a.Equals("--checkupdate", StringComparison.OrdinalIgnoreCase)))
        {
            var code = CheckUpdateCli(e.Args);
            Shutdown(code);
            Environment.Exit(code);
            return;
        }

        /* 诊断模式：系统「每次滚动几行」的现状。
         *
         * 存在的理由：这一项是**全局设置**，一旦被某个旧版本/别的软件写成 1 行，
         * 表现就是"整个系统都滚得特别慢"，而用户完全看不出是谁干的。
         * 加 --fix 会按 v1.6.x 留下的 wheellines.backup 还原一次。
         *
         * 放在抢单实例互斥体**之前**：这一项恰恰最需要在"另一个实例正跑着"时能查。 */
        if (e.Args.Any(a => a.Equals("--wheellines", StringComparison.OrdinalIgnoreCase)))
        {
            var code = WheelLinesCli(e.Args);
            Shutdown(code);
            Environment.Exit(code);
            return;
        }

        // 诊断模式：驱动真实 NotifyIcon，走完整的托盘点击路径
        if (e.Args.Any(a => a.Equals("--trayprobe", StringComparison.OrdinalIgnoreCase)))
        {
            var code = TrayProbe.Run();
            Shutdown(code);
            Environment.Exit(code);
            return;
        }

        // 诊断模式：用真实鼠标悬停复现 ToolTip/Popup 相关崩溃
        if (e.Args.Any(a => a.Equals("--hover", StringComparison.OrdinalIgnoreCase)))
        {
            var code = HoverProbe.Run();
            Shutdown(code);
            Environment.Exit(code);
            return;
        }

        // 诊断模式：验证"异常从原生 WndProc 逃出去"时走的是哪条通道
        if (e.Args.Any(a => a.Equals("--wndprobe", StringComparison.OrdinalIgnoreCase)))
        {
            var code = WndProcProbe.Run();
            Shutdown(code);
            Environment.Exit(code);
            return;
        }

        // 诊断模式：崩溃取证探针 —— 把"托盘点击 → 打开窗口"这条链路上
        //   每个可能出事的动作单独跑一遍，定位 VisualTarget 异常的确切触发点。
        if (e.Args.Any(a => a.Equals("--probe", StringComparison.OrdinalIgnoreCase)))
        {
            var code = CrashProbe.Run();
            Shutdown(code);
            Environment.Exit(code);
            return;
        }

        /* 倍率浮层截图：`--osdshot`。两种主题各拍一张，用来核对颜色和圆角/投影。 */
        if (e.Args.Any(a => a.Equals("--osdshot", StringComparison.OrdinalIgnoreCase)))
        {
            Services.Init();
            var osdCode = ShotOsd();
            Shutdown(osdCode);
            Environment.Exit(osdCode);
            return;
        }

        /* 界面截图：`--uishot [dark|light]`。逐页渲染成 PNG，用来肉眼/自动核对
         * "深色主题下有没有黑字落在深底上"。截图落在数据目录 uishot-*.png。 */
        if (e.Args.Any(a => a.Equals("--uishot", StringComparison.OrdinalIgnoreCase)))
        {
            var mode = e.Args.Any(a => a.Equals("light", StringComparison.OrdinalIgnoreCase)) ? Theme.Light : Theme.Dark;
            var only = -1;
            foreach (var a in e.Args)
                if (int.TryParse(a, out var page) && page >= 0 && page <= 7) only = page;

            Services.Init();
            var shotWin = new MainWindow();
            shotWin.Width = 1120;
            shotWin.Height = 846;
            shotWin.Show();
            var code = ShotAllPages(shotWin, mode, only);
            shotWin.Close();
            Shutdown(code);
            Environment.Exit(code);
            return;
        }

        /* UI 像素基线：`--uibase` 把 light/dark × 页面 0..7 的截图复制到
         * tools/ui-baseline/page{n}-{mode}.png，作为回归基线。 */
        if (e.Args.Any(a => a.Equals("--uibase", StringComparison.OrdinalIgnoreCase)))
        {
            Services.Init();
            var baseWin = new MainWindow();
            baseWin.Width = 1120;
            baseWin.Height = 846;
            baseWin.Show();
            var code = CaptureUiBaseline(baseWin);
            baseWin.Close();
            Shutdown(code);
            Environment.Exit(code);
            return;
        }

        /* UI 像素回归：`--uicheck` 重新渲染同样 16 张图，与 tools/ui-baseline
         * 逐像素比较；没有基线时退出码 2。 */
        if (e.Args.Any(a => a.Equals("--uicheck", StringComparison.OrdinalIgnoreCase)))
        {
            Services.Init();
            var checkWin = new MainWindow();
            checkWin.Width = 1120;
            checkWin.Height = 846;
            checkWin.Show();
            var code = CheckUiBaseline(checkWin);
            checkWin.Close();
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

        /* 自身窗口滚动实测：`--wheeltest [lines=N]`。
         *
         * 用户报"设置页里一格滚了一页 / 加速失效"时，光读代码分不出是哪一层在滚：
         * 可能是钩子放行后 WPF 自己滚、可能是我们的像素换算、也可能是内层容器抢走。
         * 这条命令把三条路径分别量出来（位移读自 ScrollViewer.VerticalOffset），
         * 并让 OnPreviewMouseWheelScroll 把每一条判据写进 wheeltest.txt。 */
        /* 逐帧平滑度实测：真注入 + 真渲染，逐帧读 VerticalOffset。
         * 与 --frametrace 互补（那条走 FrameSink，不注入）。 */
        if (e.Args.Any(a => a.Equals("--scrollsmooth", StringComparison.OrdinalIgnoreCase)))
        {
            Services.Init();
            var ssWin = new MainWindow();
            ssWin.Show();
            ssWin.UpdateLayout();
            var ssCode = ScrollSmooth.Run(ssWin);
            Shutdown(ssCode);
            Environment.Exit(ssCode);
            return;
        }

        /* --fineguard <pid> <lines>：细腻模式的守护进程。
         * 等主进程退出（正常或被杀）后把系统行数写回去，然后自己结束。
         * 必须**在单实例检查之前**处理：它不能弹窗口、不能抢互斥量。 */
        var guardIndex = Array.FindIndex(e.Args, a => a.Equals("--fineguard", StringComparison.OrdinalIgnoreCase));
        if (guardIndex >= 0 && e.Args.Length > guardIndex + 2
            && int.TryParse(e.Args[guardIndex + 1], out var guardPid)
            && int.TryParse(e.Args[guardIndex + 2], out var guardLines))
        {
            try
            {
                /* ⚠ 这里**不能**先还原：主进程此刻正在用细腻模式（系统=1 行）。
                 * 之前误写成"启动就还原"，会让细腻模式刚开启就被打回去。
                 * 只等它退出，退出后再还原。 */
                using var main = System.Diagnostics.Process.GetProcessById(guardPid);
                main.WaitForExit();          // 主进程活着就一直等（不占 CPU）
            }
            catch { /* 进程已经没了：直接还原 */ }

            SystemWheelLines.RestoreLines(guardLines);

            /* 还原完把备份清掉：否则下次启动还会"发现上次没退干净"再还原一遍
             * （虽无害，但日志会误报，也会让用户以为又出问题了）。 */
            try { SystemWheelLines.ClearBackupAfterGuard(); } catch { }

            Shutdown(0);
            Environment.Exit(0);
            return;
        }

        if (e.Args.Any(a => a.Equals("--safe", StringComparison.OrdinalIgnoreCase)))
        {
            SafeMode = true;
        }

        if (e.Args.Any(a => a.Equals("--targettrace", StringComparison.OrdinalIgnoreCase)))
        {
            Services.Init();
            var traceCode = TargetTrace();
            Shutdown(traceCode);
            Environment.Exit(traceCode);
            return;
        }

        if (e.Args.Any(a => a.Equals("--keyprobe", StringComparison.OrdinalIgnoreCase)))
        {
            Services.Init();
            var keyCode = KeyProbe();
            Shutdown(keyCode);
            Environment.Exit(keyCode);
            return;
        }

        if (e.Args.Any(a => a.Equals("--wpfprobe", StringComparison.OrdinalIgnoreCase)))
        {
            Services.Init();
            var probeCode = WpfProbe();
            Shutdown(probeCode);
            Environment.Exit(probeCode);
            return;
        }

        if (e.Args.Any(a => a.StartsWith("--wheeltest", StringComparison.OrdinalIgnoreCase)))
        {
            var lines = 0;
            foreach (var a in e.Args)
            {
                var k = a.IndexOf("lines=", StringComparison.OrdinalIgnoreCase);
                if (k >= 0 && int.TryParse(a.AsSpan(k + 6), out var v)) lines = v;
            }

            Services.Init();
            var wtWin = new MainWindow();
            wtWin.Show();
            wtWin.UpdateLayout();
            var wtCode = WheelTest.Run(wtWin, lines);
            Shutdown(wtCode);
            Environment.Exit(wtCode);
            return;
        }

        /* 这次启动是不是"上一轮提权重启的接班人"。
         * 带这个标记有两层意思：① 别再提权了（防死循环）；② 抢互斥体时要肯等 ——
         * 上一代进程正在退出，它手里的互斥体还要过几百毫秒才放开。 */
        _elevateTried = e.Args.Any(a =>
            a.Equals(Core.AdminStart.ElevateAttemptArg, StringComparison.OrdinalIgnoreCase));

        // 单实例：第二个进程只负责"把已有窗口叫出来"，然后自己退出
        _mutex = AcquireSingleInstance(_elevateTried ? TimeSpan.FromSeconds(6) : TimeSpan.Zero);
        if (_mutex == null)
        {
            var signaled = false;
            try
            {
                if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var other))
                {
                    other.Set();
                    signaled = true;
                }
            }
            catch { /* 叫不动 —— 下面给用户一句人话，而不是"双击没反应" */ }

            /* 叫不动已有实例，最常见的原因是**权限不对等**：常驻的那份是管理员
             * （计划任务开机启动就会这样），而这次是普通权限双击。
             * 两个命名对象的写权限都被 UIPI 拒了，于是老版本这里的表现是
             * "双击图标毫无反应" —— 没有窗口、没有提示、日志里也没有（Log 还没初始化）。
             * 至少要说清楚"为什么没反应、该怎么办"。 */
            if (!signaled)
            {
                try
                {
                    MessageBox.Show(
                        "「鼠标滚轮增强」已经在运行了（它可能是以管理员身份启动的）。\r\n\r\n" +
                        "请在任务栏右下角的托盘里点它的图标打开设置；\r\n" +
                        "如果托盘里看不到，打开任务管理器结束 MouseWheelEnhancement.App.exe 再试。",
                        "鼠标滚轮增强", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch { /* 连弹窗都不行（无交互桌面）：安静退出 */ }
            }

            Shutdown();
            return;
        }
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        StartActivateListener();

        DispatcherUnhandledException += OnUnhandled;

        /* UI 线程之外（后台线程 / 线程池 / 原生回调）的兜底。
         *
         * 这类异常**不会**走 DispatcherUnhandledException，默认行为是直接结束进程：
         * 用户看到的是"程序突然没了"，日志里一条都没有；如果当时细腻模式正开着，
         * 系统行数也会停在 1 行（靠 RunOnce 兜底要等下次登录）。
         * 这里至少做到三件事：留日志、尽量还原系统设置、把日志刷盘。 */
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            try { Log.Error("后台线程未处理异常", args.ExceptionObject as Exception); } catch { }
            try { SystemWheelLines.LeaveFine(); } catch { }
            try { Log.Flush(1000); } catch { }
        };

        /* ══════════════════════════════════════════════════════════════
         * 接管 **WinForms** 的未处理异常通道。
         *
         * 这一段是修"VisualTarget 的根 Visual 不能具有父级"那个崩溃的关键。
         *
         * 事情是这样的：本程序托盘图标用的是 WinForms 的 NotifyIcon，
         * 它的鼠标消息走的是**原生 Win32 窗口过程**。异常从那里抛出来时：
         *
         *   · 不走 WPF 的 DispatcherUnhandledException（那不是 DispatcherOperation）
         *   · 不走 AppDomain.UnhandledException（WinForms 先一步拦下了）
         *   · 而是被 WinForms 的 Application.ThreadException 接住
         *
         * 我们从前没订阅 ThreadException，于是走的是 WinForms 的**默认**处理：
         * 弹出框架自带的那个"应用程序的组件中发生了未经处理的异常。
         * 如果单击"继续"……"对话框 —— 用户点「继续」程序就能继续跑，
         * 所以看起来"没事"，但**日志里一条记录都没有**，
         * 事后完全无从查起。
         *
         * 实测（见 WndProcProbe 的对照实验）：同一个异常，
         *   · 抛在 WinForms Control 的 WndProc 里 → ThreadException 接得住
         *   · 抛在裸 NativeWindow 的 WndProc 里 → 三条通道全都不接，直接崩
         *
         * 所以这里必须显式订阅，并且用与 Dispatcher 相同的处理：
         * 记日志 → 落盘取证 → 弹我们自己的框。**用户永远不该看到框架默认弹窗。**
         * ══════════════════════════════════════════════════════════════ */
        try
        {
            System.Windows.Forms.Application.SetUnhandledExceptionMode(
                System.Windows.Forms.UnhandledExceptionMode.CatchException);
            System.Windows.Forms.Application.ThreadException += OnWinFormsThreadException;
        }
        catch (Exception ex)
        {
            Log.Warn("接管 WinForms 异常通道失败：" + ex.Message);
        }

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Error("未处理的域异常", args.ExceptionObject as Exception);
            Log.Flush();
            DumpCrash(args.ExceptionObject as Exception, "appdomain");   // 后台线程异常会带走进程
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("未观察的任务异常", args.Exception);
            args.SetObserved();
        };

        /* 第一阶段初始化：只读配置、应用主题。
         * 钩子和托盘留到确认"不需要提权重启"之后 —— 否则提权那一刻
         * 用户会看到托盘图标闪一下又消失。 */
        try
        {
            Services.InitConfig();
        }
        catch (Exception ex)
        {
            Log.Error("初始化失败", ex);
            MessageBox.Show($"初始化失败：{ex.Message}\n\n日志：{Log.FilePath}",
                Services.AppTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
            return;
        }

        /* 把"稳定入口"（启动器 exe 的路径）落实下来。
         *
         * 开机自启任务必须指向它，不能指向 %LOCALAPPDATA%\...\app\<版本>\ 里的主程序本体 ——
         * 那个目录每次升级都会被删掉，任务会当场静默失效。
         *
         * 启动器启动主程序时用环境变量把路径传过来；这里再落到配置里，
         * 这样"提权重启自己"之后（新进程没有那个环境变量）仍然找得到同一个入口。 */
        ResolveStableEntry();

        /* 「以管理员身份运行」：手动启动也要算。
         *
         * 已经是普通权限的进程没法给自己提权，只能另起一个带 runas 的进程再退出。
         * 判断放在这里是因为：再早读不到配置，再晚就已经装好钩子、建好托盘了。
         *
         * 这个判断以前根本不存在 —— 开关只管开机那次的计划任务，用户关掉软件
         * 再双击打开就是普通权限，界面上还挂着"当前不是管理员身份"，
         * 正是"我明明开了管理员启动"那个报障的根因。 */
        if (TryRestartElevatedIfNeeded(e.Args)) return;

        /* 提权重启后回来：把设置里的「开机自启 / 以管理员身份运行」真正落到系统里。
         * 必须早于 InitRuntime —— 这一步可能又要提权重启（比如关掉管理员开关时要
         * 删计划任务），早做就不用先装一遍钩子再拆掉。 */
        if (e.Args.Any(a => a.Equals("--apply-startup", StringComparison.OrdinalIgnoreCase)))
            ApplyStartupFromConfig();
        else
            RepairStartupIfNeeded();

        /* 第二阶段：装钩子、起引擎、建托盘。到这一步基本不会再失败了，
         * 万一失败也不能让进程半死不活地留在后台。 */
        try
        {
            /* ⚠ 启动时**不写**系统滚动行数。
             *
             * v1.7.0 还会在这里把配置里的值写进系统（"同步"的一半），
             * 但那等于"软件一启动就把系统设置改成自己记的那个数"——
             * 用户在系统面板里改过、软件没开着的那段时间，改动会被悄悄覆盖掉。
             * 现在系统是唯一真相：这里只做两件事 ——
             *   ① 迁移：把 v1.6.x「接管」可能留下的 1 行按备份还原；
             *   ② 什么都不干，等用户拖滑块时再写（SystemWheelLines.Set）。
             * 界面上的数字靠 MainWindow.SyncWheelLinesFromSystem() 读回。 */
            SystemWheelLines.CleanupLegacy();

            Services.InitRuntime();
        }
        catch (Exception ex)
        {
            Log.Error("引擎/托盘初始化失败", ex);
            MessageBox.Show($"初始化失败：{ex.Message}\n\n日志：{Log.FilePath}",
                Services.AppTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
            return;
        }

        // 全局热键：Ctrl+Alt+W 开关增强，Ctrl+Alt+S 打开设置。失败只记日志，不影响主流程。
        RegisterGlobalHotkeys();
        StartRuntimeAssist();

        var startMinimized = e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        if (!startMinimized) ShowMainWindow();

        if (Services.Config.Update.AutoCheck) _ = AutoCheckAsync();
    }

    /* ══════════════ 全局热键 ══════════════ */

    private const int HotkeyToggleId = 0x4D57;
    private const int HotkeySettingsId = 0x4D58;

    /// <summary>
    /// 注册两个系统级热键：<c>Ctrl+Alt+W</c> 开关增强、<c>Ctrl+Alt+S</c> 打开设置。
    ///
    /// <para>
    /// 用一个 0×0 的隐藏 <see cref="System.Windows.Interop.HwndSource"/> 收 <c>WM_HOTKEY</c>。
    /// 注册失败（被别的程序占用）只记一条日志 —— 热键是锦上添花，不该影响主流程。
    /// </para>
    /// </summary>
    private static void RegisterGlobalHotkeys()
    {
        try
        {
            if (_hotkeySource != null) return;

            var p = new System.Windows.Interop.HwndSourceParameters("MWE-Hotkeys")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0,
            };
            _hotkeySource = new System.Windows.Interop.HwndSource(p);
            _hotkeySource.AddHook(HotkeyWndProc);

            var hwnd = _hotkeySource.Handle;
            if (!Native.RegisterHotKey(hwnd, HotkeyToggleId, Native.MOD_CONTROL | Native.MOD_ALT, 0x57))
            {
                // 被占了就多带一个 Shift —— Ctrl+Alt+W 常被输入法/截图工具占用
                if (Native.RegisterHotKey(hwnd, HotkeyToggleId,
                        Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_SHIFT, 0x57))
                    Log.Info("Ctrl+Alt+W 被占用，改用 Ctrl+Alt+Shift+W 开关增强");
                else
                    Log.Warn("全局热键 Ctrl+Alt+W / Ctrl+Alt+Shift+W 都注册失败");
            }
            if (!Native.RegisterHotKey(hwnd, HotkeySettingsId, Native.MOD_CONTROL | Native.MOD_ALT, 0x53))
                Log.Warn("全局热键 Ctrl+Alt+S 注册失败（可能被其它程序占用）");
        }
        catch (Exception ex)
        {
            Log.Warn("注册全局热键失败：" + ex.Message);
        }
    }

    private static IntPtr HotkeyWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != Native.WM_HOTKEY) return IntPtr.Zero;

        switch (wParam.ToInt32())
        {
            case HotkeyToggleId:
                Services.ToggleEnabled();
                handled = true;
                break;
            case HotkeySettingsId:
                Services.ShowWindow();
                handled = true;
                break;
        }
        return IntPtr.Zero;
    }

    private static void UnregisterGlobalHotkeys()
    {
        try
        {
            if (_hotkeySource == null) return;
            Native.UnregisterHotKey(_hotkeySource.Handle, HotkeyToggleId);
            Native.UnregisterHotKey(_hotkeySource.Handle, HotkeySettingsId);
            _hotkeySource.RemoveHook(HotkeyWndProc);
            _hotkeySource.Dispose();
            _hotkeySource = null;
        }
        catch { /* 退出阶段不纠缠 */ }
    }

    /// <summary>
    /// 抢单实例互斥体，抢不到就返回 <c>null</c>。
    ///
    /// <para>
    /// 为什么不直接用 <c>new Mutex(true, name, out createdNew)</c>：那个写法只能回答
    /// "这个名字是不是我刚创建的"，回答不了"上一代正在退出、我现在能不能接手"。
    /// 提权重启的接班进程恰好就是后一种情况 —— 上一代还握着互斥体，几百毫秒后才放开。
    /// 用 <c>createdNew</c> 判断的话，接班进程会把自己当成"第二个实例"直接退出，
    /// 用户看到的现象是：点了开关，软件没了。
    /// </para>
    /// </summary>
    /// <param name="wait">最多等多久（接班进程才需要等，普通启动传 <see cref="TimeSpan.Zero"/>）。</param>
    private static Mutex? AcquireSingleInstance(TimeSpan wait)
    {
        var deadline = Environment.TickCount64 + (long)wait.TotalMilliseconds;
        while (true)
        {
            Mutex m;
            try { m = new Mutex(false, MutexName); }
            catch { return null; }

            try
            {
                if (m.WaitOne(0)) return m;          // 抢到了
            }
            catch (AbandonedMutexException)
            {
                /* 上一代是被强杀的（任务管理器结束进程 / 崩溃），互斥体没人释放。
                 * 系统把它判成"已放弃"并交给我们 —— 这时我们就是持有者了。 */
                return m;
            }

            m.Dispose();
            if (Environment.TickCount64 >= deadline) return null;
            Thread.Sleep(100);
        }
    }

    /// <summary>
    /// <c>--taskcheck</c>：检查「以管理员身份开机启动」的计划任务。
    ///
    /// <para>
    /// 默认**只读**（查存在性、查路径是否指向当前 exe）；加 <c>--create</c> 或
    /// <c>--delete</c> 才真的改，且必须管理员权限。
    /// </para>
    /// <para>
    /// 存在的意义：这条链路（写计划任务）平时只在用户拨一下开关时走一次，
    /// 出问题时的表现是"开关拨了、下次开机却没启动"——只能靠人过一夜才发现。
    /// 有了这个入口，至少能当场把状态和真正的原因打出来。
    /// </para>
    /// </summary>
    private static int TaskCheckCli(string[] args)
    {
        var sb = new System.Text.StringBuilder();
        var doCreate = args.Any(a => a.Equals("--create", StringComparison.OrdinalIgnoreCase));
        var doDelete = args.Any(a => a.Equals("--delete", StringComparison.OrdinalIgnoreCase));

        var elevated = Core.Native.IsElevated();
        sb.AppendLine($"主程序：{UpdateService.ExePath}");
        sb.AppendLine($"稳定入口：{Core.AdminStart.StableEntryPath}");
        sb.AppendLine($"当前权限：{(elevated ? "管理员" : "普通用户（创建/删除会失败）")}");
        sb.AppendLine($"任务名：{Core.AdminStart.TaskName}");
        sb.AppendLine(new string('-', 56));

        try
        {
            var exists = Core.AdminStart.IsEnabled();
            sb.AppendLine($"任务是否存在：{(exists ? "是" : "否")}");

            if (exists)
            {
                var target = Core.AdminStart.TaskTargetPath();
                sb.AppendLine($"任务指向：{(target ?? "（取不到）")}");
                if (!string.IsNullOrWhiteSpace(target))
                    sb.AppendLine($"目标文件存在：{(File.Exists(target) ? "是" : "否 —— 目标已被删除，开机必然起不来")}");

                if (Core.AdminStart.HasKnownEntry)
                    sb.AppendLine($"指向稳定入口：{(Core.AdminStart.PointsToStableEntry() ? "是" : "否 —— 需要重建")}");
                else
                    sb.AppendLine("指向稳定入口：（当前不是从启动器启动，无法判断稳定入口是谁）");

                sb.AppendLine($"是否已失效：{(Core.AdminStart.IsStale() ? "是" : "否")}");
            }

            if (doCreate || doDelete)
            {
                // 写计划任务必须管理员权限。当前不是的话，自己提权重来一次。
                if (!elevated)
                {
                    sb.AppendLine();
                    sb.AppendLine("当前不是管理员权限，正在提权重启后继续…");
                    WriteCliResult("taskcheck.txt", sb.ToString(), 0);

                    if (RelaunchElevated(args))
                    {
                        Application.Current.Shutdown(0);
                        Environment.Exit(0);
                    }
                    sb.AppendLine("提权失败：请手动以管理员身份运行本程序。");
                    return WriteCliResult("taskcheck.txt", sb.ToString(), 2);
                }

                sb.AppendLine();
                sb.AppendLine(doCreate ? "正在创建任务…" : "正在删除任务…");
                var ok = Core.AdminStart.Set(doCreate, out var err);
                sb.AppendLine(ok ? "结果：成功" : "结果：失败 —— " + err);
                if (doCreate)
                {
                    sb.AppendLine($"现在是否存在：{(Core.AdminStart.IsEnabled() ? "是" : "否")}");
                    sb.AppendLine($"现在指向：{Core.AdminStart.TaskTargetPath() ?? "（取不到）"}");
                }
                return WriteCliResult("taskcheck.txt", sb.ToString(), ok ? 0 : 2);
            }

            sb.AppendLine();
            sb.AppendLine("（只读检查。加 --create 或 --delete 才会真的改动。）");
        }
        catch (Exception ex)
        {
            sb.AppendLine("异常：" + ex);
            return WriteCliResult("taskcheck.txt", sb.ToString(), 2);
        }

        return WriteCliResult("taskcheck.txt", sb.ToString(), 0);
    }

    /// <summary>
    /// 解析"稳定入口"（开机自启任务该指向的那个 exe），并把它记进配置。
    ///
    /// <para>
    /// 顺序：环境变量（启动器这次拉起我们时给的）→ 配置里上次记的 → 都没有就用当前 exe。
    /// 走启动器来的话，环境变量和配置是一致的，第二次以后靠配置就够了。
    /// </para>
    /// </summary>
    private static void ResolveStableEntry()
    {
        try
        {
            var env = Environment.GetEnvironmentVariable(Core.AdminStart.LauncherPathEnv);
            var envPath = string.IsNullOrWhiteSpace(env) ? null : env.Trim();

            var cfgPath = Services.Config.Behavior.LauncherPath;
            if (string.IsNullOrWhiteSpace(cfgPath)) cfgPath = null;

            /* 按优先级挑第一个**真实存在**的。
             *
             * 这里不能写成 `envPath ?? cfgPath` 再判 Exists：环境变量指向的文件
             * 万一没了（启动器被删/改名），就会连带把配置里那个仍然有效的路径也清掉，
             * 于是 StableEntryPath 退化成版本目录，自愈反而把任务写坏。 */
            string? chosen = null;
            foreach (var candidate in new[] { envPath, cfgPath })
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                if (!File.Exists(candidate)) continue;
                chosen = candidate;
                break;
            }

            if (chosen != null)
            {
                Core.AdminStart.LauncherPath = chosen;

                // 配置里没有、或和实际用的不一致 → 更新它
                if (!string.Equals(cfgPath, chosen, StringComparison.OrdinalIgnoreCase))
                {
                    Services.Config.Behavior.LauncherPath = chosen;
                    Services.Store.SaveNow();
                    Log.Info($"已记住启动器路径：{chosen}");
                }
            }
            else if (cfgPath != null)
            {
                // 配置里的启动器已经不在了（用户挪了或删了）→ 忘掉，让它退回当前 exe
                Core.AdminStart.LauncherPath = null;
                Services.Config.Behavior.LauncherPath = "";
                Services.Store.SaveNow();
                Log.Warn($"配置里记的启动器已不存在：{cfgPath}");
            }
        }
        catch (Exception ex) { Log.Warn("解析稳定入口失败：" + ex.Message); }
    }

    /// <summary>
    /// 「以管理员身份运行」开着、但这次是普通权限 → 提权重启自己。
    ///
    /// <para>
    /// 这就是"我明明在设置里开了管理员启动，打开还是提示不是管理员"的修法。
    /// 以前这个开关**只管开机自启**（计划任务），手动双击、更新完自动重启、
    /// 从托盘退出再打开，全都是普通权限 —— 而界面上的字写的是"以管理员身份启动"，
    /// 用户当然会认为它管所有启动。
    /// </para>
    ///
    /// <returns>已经发起提权重启并退出（调用方应立刻返回）时为 true。</returns>
    /// </summary>
    private static bool TryRestartElevatedIfNeeded(string[] args)
    {
        if (!Core.AdminStart.ShouldElevate(Services.IsElevated, Services.Config.Behavior.RunAsAdmin, _elevateTried))
            return false;

        if (!RelaunchElevated(args))
        {
            /* 提权没成功：用户点了 UAC 的「否」、或者组策略禁止 runas。
             * 不重试（会变成弹窗风暴）—— 把标记置上，本进程后面再遇到
             * "需要管理员"的分支（--apply-startup 那条）也不会再弹一次。
             * 以普通权限继续跑；界面上的「权限提示」卡片会说清楚原因。 */
            _elevateTried = true;
            Log.Warn("「以管理员身份运行」已开启，但本次提权没有成功，以普通权限继续");
            return false;
        }

        Log.Info("「以管理员身份运行」已开启，正在以管理员权限重启");
        ExitApp(0);
        return true;
    }

    /// <summary>
    /// 以管理员权限重新拉起自己。
    ///
    /// <para>
    /// 拉起的目标是**稳定入口**（启动器）而不是主程序本体：主程序本体住在版本目录里，
    /// 换个版本就找不到"启动器在哪"这个信息了；而走启动器还能顺带把
    /// <c>MWE_LAUNCHER_PATH</c> 重新传一遍。
    /// </para>
    /// <para>
    /// 自动附带 <see cref="Core.AdminStart.ElevateAttemptArg"/>：新进程据此知道
    /// "这一轮已经试过了"，提权没生效时不会再提一次。
    /// </para>
    /// </summary>
    /// <param name="args">要转发的开关（只收 <c>--xxx</c>，其余当噪音丢掉）。</param>
    internal static bool RelaunchElevated(params string[] args)
    {
        try
        {
            var exe = Core.AdminStart.StableEntryPath;
            if (!File.Exists(exe)) exe = UpdateService.ExePath;
            if (!File.Exists(exe)) return false;

            var forwarded = args
                .Where(a => !string.IsNullOrWhiteSpace(a) &&
                            a.StartsWith("--", StringComparison.Ordinal) &&
                            !a.Equals(Core.AdminStart.ElevateAttemptArg, StringComparison.OrdinalIgnoreCase))
                .Append(Core.AdminStart.ElevateAttemptArg)
                .Select(a => "\"" + a.Replace("\"", "\\\"") + "\"");

            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = string.Join(" ", forwarded),
            };
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            // 最常见的是 ERROR_CANCELLED(1223)：用户在 UAC 上点了「否」。
            // 这不是程序错误，记一笔就够，别弹框吓人。
            Log.Warn("提权重启失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 提权重启后回来：按配置把两种开机自启方式同步到系统。
    ///
    /// <para>
    /// 之所以要「重启自己再回来做」而不是当场做：写计划任务必须管理员权限，
    /// 而一个已经在运行的普通权限进程**没法给自己提权**，只能重启。
    /// </para>
    /// </summary>
    private static void ApplyStartupFromConfig()
    {
        var b = Services.Config.Behavior;

        /* 要动计划任务就必须是管理员：建任务、删任务都要。
         *
         * 多数时候这一条已经被 TryRestartElevatedIfNeeded 解决了（开关开着时它先把进程提上去）。
         * 这里兜的是它不管的那条路：用户在设置里**关掉**「以管理员身份运行」——
         * 此时 RunAsAdmin 已经是 false、不会触发自动提权，但旧任务还等着删。 */
        var needAdmin = Core.AdminStart.IsEnabled() || (b.RunAtLogin && b.RunAsAdmin);
        if (needAdmin && !Services.IsElevated && !_elevateTried)
        {
            if (RelaunchElevated("--apply-startup"))
            {
                Log.Info("应用开机自启需要管理员权限，已发起提权重启");
                ExitApp(0);
                return;
            }
        }

        if (AutoStart.Apply(b.RunAtLogin, b.RunAsAdmin, out var error))
        {
            Log.Info($"已按设置应用开机自启：开机启动={b.RunAtLogin}，管理员={b.RunAsAdmin}");
        }
        else
        {
            Log.Warn("应用开机自启失败：" + error);
            MessageBox.Show(error ?? "设置开机自启失败。", Services.AppTitle,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// 启动自愈。两件事：
    /// <list type="number">
    ///   <item>配置说要以管理员身份自启，但计划任务不见了（用户手动删过），
    ///         或者任务还在却指向了失效路径（版本目录被清理 / 程序被挪了目录）→ 重建。</item>
    ///   <item>配置说不用管理员自启，任务却还留着（上次删除失败）→ 清掉。</item>
    /// </list>
    /// 只在**已经是管理员**时动作 —— 普通权限下重建计划任务注定失败（E_ACCESSDENIED）。
    /// 普通权限下发现任务失效时不做任何写操作，改为在设置页显示红色提示 + 一键修复。
    /// </summary>
    private static void RepairStartupIfNeeded()
    {
        try
        {
            var b = Services.Config.Behavior;

            /* 配置说"不要管理员自启"（包括整个开机自启都关了）时，
             * 计划任务必须清掉 —— 否则开关显示着关，开机却还是弹一次提权启动。
             *
             * 这里曾经写成 `if (!b.RunAtLogin) return;` 之后再判断，
             * 结果"自启整体关闭 + 任务没删成功"这条最需要清理的路径被第一行挡住了：
             * 之后每次以管理员启动都不会再自愈，残留任务永远留着。 */
            if (!b.RunAtLogin || !b.RunAsAdmin)
            {
                if (Services.IsElevated && AdminStart.IsEnabled())
                {
                    AdminStart.Set(false, out _);
                    Log.Info("已清理残留的管理员自启任务");
                }
                else if (AdminStart.IsEnabled())
                {
                    // 没权限删：设置页的提示条会显示"任务多余"，让用户点「立即修复」
                    Log.Warn("计划任务还在，但设置里已经关掉了管理员自启 —— 当前没有管理员权限，" +
                             "无法自动清理，请在设置页点「立即修复」。");
                }
                return;
            }

            if (!Services.IsElevated)
            {
                // 非管理员：只能看，不能改。把"需要修复"这件事记下来，设置页会显示。
                if (AdminStart.IsStale())
                    Log.Warn("管理员自启任务已失效（目标文件不存在或指向旧版本），" +
                             "当前不是管理员权限，无法自动重建 —— 请在设置页点「立即修复」。");
                return;
            }

            if (!AdminStart.IsEnabled() || AdminStart.IsStale())
            {
                var why = AdminStart.TaskTargetPath();
                if (AdminStart.Set(true, out var err))
                    Log.Info($"已重建管理员自启任务（原先指向：{why ?? "（无）"}）");
                else
                    Log.Warn("重建管理员自启任务失败：" + err);
            }
        }
        catch (Exception ex) { Log.Warn("开机自启自愈失败：" + ex.Message); }
    }

    private static readonly IntPtr ForeignSignature = (IntPtr)0x5EE0;

    /// <summary>
    /// `--measure`：让程序按真实方式跑起来（装钩子、建托盘、可选建窗口），
    /// 等 6 秒让 JIT / GC / 首帧渲染都稳定，再把占用写进日志然后退出。
    ///
    /// 加这个模式是因为"内存占用"只能实测：WPF 的数字取决于有没有渲染过、
    /// 有没有走过 D3D、有没有把七个页面都布局出来。拍脑袋说"大概几十兆"没有意义。
    /// </summary>
    private static void StartMeasureMode(bool showWindow)
    {
        try
        {
            Services.Init();

            MainWindow? w = null;
            if (showWindow)
            {
                w = new MainWindow();
                w.Show();
                w.UpdateLayout();
                for (var i = 0; i < 7; i++) { w.SelectTab(i); w.UpdateLayout(); }
            }

            // 等 6 秒：JIT、GC、首帧渲染、D3D 设备创建都稳定下来，数字才有可比性
            var timer = new DispatcherTimer(TimeSpan.FromSeconds(6), DispatcherPriority.Background,
                (_, _) =>
                {
                    ReportResources(showWindow ? "measure(带窗口)" : "measure(仅托盘)");

                    if (w == null) { ExitApp(); return; }

                    /* 再走一遍"用户点 × 退到托盘"的真实路径：窗口应当被真正销毁，
                     * 那 100 多兆要还给系统。3 秒后回收一次再测，这是"关掉设置后
                     * 内存会不会回落"的唯一硬证据。 */
                    w.CloseAndRelease();
                    var after = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background,
                        (_, _) =>
                        {
                            GC.Collect();
                            GC.WaitForPendingFinalizers();
                            GC.Collect();
                            ReportResources("measure(关闭设置后)");

                            /* 再走一次"从托盘点开设置"：窗口是刚销毁的，
                             * 这条重建路径必须验证 —— 否则为了省内存把用户
                             * 卡在"点了打不开"上，比多占两百兆严重得多。 */
                            Services.ShowWindow();
                            var reopened = Services.Window;
                            reopened?.UpdateLayout();
                            ReportResources($"measure(重新打开，窗口={(reopened != null ? "已重建" : "失败")})");
                            ExitApp();
                        }, Dispatcher.CurrentDispatcher);
                    _ = after;
                }, Dispatcher.CurrentDispatcher);
            _ = timer; // DispatcherTimer 由 dispatcher 持有，不需要额外引用
        }
        catch (Exception ex)
        {
            Log.Error("measure 模式异常", ex);
            Log.Flush();
            Environment.Exit(3);
        }
    }

    /// <summary>把启动耗时与资源占用写进日志（优化前后对比要用的原始数字）。</summary>
    private static void ReportResources(System.Diagnostics.Stopwatch sw) => ReportResources($"启动+七页布局 {sw.ElapsedMilliseconds} ms");

    private static void ReportResources(string label)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetCurrentProcess();
            p.Refresh();
            var gdi = Native.GetGuiResources(p.Handle, 0);
            var user = Native.GetGuiResources(p.Handle, 1);
            Log.Info($"{label}：" +
                     $"工作集 {p.WorkingSet64 / 1048576.0:0.0} MB，" +
                     $"私有 {p.PrivateMemorySize64 / 1048576.0:0.0} MB，" +
                     $"托管堆 {GC.GetTotalMemory(false) / 1048576.0:0.0} MB，" +
                     $"线程 {p.Threads.Count}，GDI {gdi}，USER {user}");
        }
        catch (Exception ex) { Log.Warn("统计资源占用失败：" + ex.Message); }
    }

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

    /// <summary>显示主窗口。托盘图标点一下就该把它叫出来 —— 这条路必须万无一失。</summary>
    ///
    /// <remarks>
    /// <para>
    /// 这里曾是崩溃的出口：托盘图标是 WinForms 组件，它的鼠标回调在**原生 WndProc**
    /// 里执行，异常会绕过 WPF 的 Dispatcher 直奔 WinForms 的 ThreadException。
    /// 所以这段代码的任何异常都意味着"用户看到框架弹窗 + 日志空白"。
    /// 现在有两层保险：① 外面包了 try/catch，异常走统一出口（记日志 + 落盘 + 弹我们的框）；
    /// ② 里面每一步都做**状态自检**，宁可重建窗口也不在半死不活的窗口上继续操作。
    /// </para>
    /// </remarks>
    public void ShowMainWindow(int tabIndex = -1)
    {
        // 重入保护：托盘双击会连发 MouseClick×2 + DoubleClick，
        // 三次调用可能重叠在一次消息处理里。重入时直接忽略 —— 窗口已经在显示了。
        if (_showingWindow) return;
        _showingWindow = true;
        try
        {
            var w = Services.Window;

            // 窗口可能"看起来还在，实际已经死了"：
            // 关闭到托盘时 OnClosing 会先 Hide 再在 ApplicationIdle 里 Close()，
            // 而 Closed 事件把 Services.Window 置空 —— 但这中间有窗口期。
            // 这时候对已销毁的窗口调 Show() 会抛
            // "Cannot set Visibility or call Show ... after a Window has closed"。
            if (w != null && PresentationSource.FromVisual(w) == null && !w.IsVisible)
            {
                Log.Warn("主窗口已失效（PresentationSource 丢失），重建");
                try { w.Close(); } catch { /* 关不掉就算了，下面直接丢弃 */ }
                w = null;
                Services.Window = null;
            }

            if (w == null)
            {
                var fresh = new MainWindow();
                // ⚠ 身份校验：旧窗口的 Closed 事件不能把**新**窗口的引用也清掉。
                //   之前是无条件置 null，一旦"旧窗口的 Closed 晚于新窗口创建"，
                //   Services.Window 就指向 null 而真正的窗口还开着 —— 下次点托盘
                //   会再造一个，用户看到"开出了两个窗口"。
                fresh.Closed += (_, _) =>
                {
                    if (ReferenceEquals(Services.Window, fresh)) Services.Window = null;
                };
                Services.Window = fresh;
                w = fresh;
                w.Show();
            }
            else
            {
                if (w.WindowState == WindowState.Minimized)
                    w.WindowState = WindowState.Normal;
                w.Show();
                w.Activate();
            }

            if (tabIndex >= 0) w.SelectTab(tabIndex);

            // 置顶再取消 —— 用来把窗口抢到前台。
            // 这一步会触发窗口的 z 序与布局变化，是最容易和"开着的弹层"打架的地方，
            // 所以单独包一层：真出了岔子也不该让整个"显示窗口"失败。
            TryRaiseToFront(w);
        }
        catch (Exception ex)
        {
            /* 走到这里说明窗口状态确实坏了。
             * 与其把异常抛到 WinForms 那条通道上去弹框架框，
             * 不如自己收拾干净：丢掉这个窗口实例，下次重新建一个。 */
            Log.Error("显示主窗口失败，将重建窗口", ex);
            Log.Flush();
            DumpCrash(ex, "showmainwindow");

            try
            {
                var broken = Services.Window;
                Services.Window = null;
                if (broken != null)
                {
                    /* 这里只需要把它关掉：它的 Closed 处理器带身份校验
                     * （ReferenceEquals(Services.Window, window)），此时 Window 已经是 null，
                     * 不会再误清别人的引用。
                     * 以前这里写的是 `broken.Closed -= (_, _) => { }` —— 退订一个刚创建的
                     * lambda，什么也退不掉，纯属噪音。 */
                    try { broken.Close(); } catch { }
                }
            }
            catch { /* 收拾残局失败就算了 */ }
        }
        finally
        {
            _showingWindow = false;
        }
    }

    /// <summary>
    /// 把窗口抢到前台。失败就失败 —— 这只是个体验优化，不值得为它抛异常。
    /// </summary>
    private static void TryRaiseToFront(Window w)
    {
        try
        {
            w.Topmost = true;
            w.Topmost = false;
            w.Activate();
        }
        catch (Exception ex)
        {
            Log.Warn("窗口置顶失败（不影响使用）：" + ex.Message);
        }
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
        // 界面层的小毛病不该把整个工具带走 —— 它还在替用户接管滚轮
        e.Handled = true;
        ReportCrash(e.Exception, "dispatcher");
    }

    /// <summary>
    /// WinForms 通道的未处理异常（托盘图标的 WndProc 走的就是这条）。
    ///
    /// <para>
    /// 这是"VisualTarget 的根 Visual 不能具有父级"那次崩溃的出口 ——
    /// 在接上这个订阅之前，异常被 WinForms 的默认处理吃掉，
    /// 用户看到的是框架自带的弹窗，而 app.log 里连一条 ERROR 都没有。
    /// </para>
    /// </summary>
    private void OnWinFormsThreadException(object sender, System.Threading.ThreadExceptionEventArgs e)
    {
        ReportCrash(e.Exception, "winforms");
    }

    /// <summary>
    /// 崩溃的统一出口：记日志 → 同步落盘取证 → 弹我们自己的框。
    ///
    /// <para>
    /// 顺序不能改：先 <c>Log.Flush()</c> 把异步日志队列落盘，
    /// 再用 <see cref="DumpCrash"/> 同步写一份 crash-*.txt
    /// （进程可能紧接着就没了，异步队列扛不住）。
    /// </para>
    /// </summary>
    private void ReportCrash(Exception ex, string channel)
    {
        try
        {
            // 连堆栈一起记 + 落盘后再弹框：异步日志在进程随后退出时会整段丢掉，
            // 而"到底是哪个弹出层炸的"全在堆栈里
            Log.Error($"未处理异常（{channel}）", ex);
            Log.Flush();
            DumpCrash(ex, channel);
        }
        catch { /* 取证本身失败也不能二次爆炸 */ }

        // InvalidOperationException 多半是"对已关闭窗口再 Show()"这类良性情况，
        // 不打扰用户；其余都要明确告知 —— 用户看到框架默认弹窗只会更懵。
        if (ex is InvalidOperationException) return;

        try
        {
            MessageBox.Show(
                $"出了个小问题（已记录日志，不会影响滚轮增强）：\n{ex.Message}\n\n" +
                $"日志：{Log.FilePath}",
                Services.AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch { /* 弹框失败就算了，不能因此崩 */ }
    }

    /// <summary>
    /// 把未处理异常**同步**写到数据目录（不经过异步日志队列）。
    ///
    /// <para>
    /// 异步日志在"异常之后进程立刻没了"的场景下整段丢失 —— 实测就出现过：
    /// 用户看到框架的默认异常弹窗，而 app.log 里连 ERROR 都没有。
    /// 堆栈是定位的唯一线索（"VisualTarget 的根 Visual 不能具有父级"光看消息
    /// 定位不到是哪个弹出层），所以这里用最笨但最可靠的方式：直接写文件。
    /// </para>
    /// </summary>
    private static void DumpCrash(Exception? ex, string channel)
    {
        try
        {
            var dir = AppPaths.DataDir;
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"渠道：{channel}");
            sb.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
            sb.AppendLine($"版本：v{Log.Version}");
            sb.AppendLine();
            sb.AppendLine(ex?.ToString() ?? "(异常对象为空)");
            sb.AppendLine();
            sb.AppendLine("---- 全部线程 ----");
            foreach (System.Diagnostics.ProcessThread t in System.Diagnostics.Process.GetCurrentProcess().Threads)
            {
                try
                {
                    sb.AppendLine($"  线程 {t.Id} 状态={t.ThreadState} 起始={t.StartTime:HH:mm:ss.fff}");
                }
                catch { /* 线程可能已退出 */ }
            }
            File.WriteAllText(path, sb.ToString(), new System.Text.UTF8Encoding(false));
        }
        catch { /* 取证失败也不能二次爆炸 */ }
    }

    /// <summary>真正的退出入口（托盘菜单、安装完更新重启都用它）。</summary>
    /* ══════════════ 运行时守护：OSD + 看门狗（v1.8.0） ══════════════ */

    private static OsdWindow? _osd;
    private static System.Threading.Timer? _watchdog;

    /// <summary>上一次看门狗重启钩子的时刻（TickCount64）。失败要退避，不能每轮硬重启。</summary>
    private static long _lastHookRestartMs;

    private void StartRuntimeAssist()
    {
        try
        {
            Services.Engine.Boosted += OnBoosted;
            _watchdog = new System.Threading.Timer(_ => WatchdogTick(), null,
                TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            Log.Warn("运行时守护启动失败（不影响滚动）：" + ex.Message);
        }
    }

    private static void StopRuntimeAssist()
    {
        try { _watchdog?.Dispose(); } catch { }
        _watchdog = null;

        /* 先退订再关窗：退出过程中动画线程可能还在跑，退订后就不会再往
         * 已经要销毁的 UI 线程封送 OSD 更新。 */
        try { if (Services.Engine != null) Services.Engine.Boosted -= OnBoosted; } catch { }

        try
        {
            if (_osd != null)
            {
                _osd.Hide();
                _osd.Close();
            }
        }
        catch { }
        _osd = null;
    }

    /// <summary>钩子线程回调：只做封送，真正的窗口操作留给 UI 线程。</summary>
    private static void OnBoosted(double multiplier)
    {
        var d = Application.Current?.Dispatcher;
        if (d == null || d.HasShutdownStarted) return;

        _ = d.BeginInvoke(new Action(() =>
        {
            try
            {
                _osd ??= new OsdWindow();
                _osd.Flash(multiplier);
            }
            catch (Exception ex)
            {
                // OSD 是锦上添花，坏掉就永久关掉它，别每次滚动都抛
                Log.Warn("倍率浮层失败，本次运行不再显示：" + ex.Message);
                Services.Engine.Boosted -= OnBoosted;
            }
        }));
    }

    /// <summary>
    /// 每 5 秒一次：钩子还活着吗？该不该换一个新的？
    ///
    /// <para>
    /// 低层钩子被系统静默摘掉时没有任何通知，句柄仍然非零 ——
    /// 唯一能自愈的方式就是**定期换一个新的**。为了不吞掉正在进行的滚动，
    /// 只在"至少 3 秒没有滚轮事件"的空闲窗口里换。
    /// </para>
    /// </summary>
    private static void WatchdogTick()
    {
        try
        {
            var engine = Services.Engine;
            if (engine == null) return;

            if (!Services.Config.Behavior.HookWatchdog)
            {
                _ = engine.TakeHookStats();  // 关掉也要把计数器清零，避免下次开时数字虚高
                return;
            }

            var now = Environment.TickCount64;

            /* 动画线程死了必须单独重启 —— 光看钩子看不出来。
             * 它一死，被接管的事件会"只吞不发"，整个系统的滚轮就不动了。
             * 钩子那边已经改成"动画器不在了就原样放行"，所以这里慢一点也不会卡住滚动。 */
            if (!engine.AnimatorAlive)
            {
                Log.Warn("看门狗：平滑动画线程不在了，正在重新拉起");
                engine.EnsureAnimator();
            }

            if (!engine.HookThreadAlive || !engine.IsRunning)
            {
                /* 退避 30 秒。装钩子失败（比如被杀软挡着）时，每 5 秒一次 Restart
                 * 会连着跑 `Join(2000)`，日志也会刷满 —— 反而更难排查。 */
                if (now - Interlocked.Read(ref _lastHookRestartMs) < 30_000) return;
                Interlocked.Exchange(ref _lastHookRestartMs, now);

                Log.Warn("看门狗：钩子线程已不在，正在重启钩子");
                if (!engine.Restart()) Log.Warn("看门狗：重启钩子失败，30 秒后重试");
                return;
            }

            var idle = now - engine.LastEventTicks;
            var age = now - engine.HookBornTicks;

            /* 10 分钟换一次，且必须空闲。轮换会有一瞬间（几十微秒）没有钩子在位，
             * 正在滚的时候换会丢事件 —— 所以宁可等。 */
            if (age > 10 * 60_000 && idle > 3000)
                engine.RequestHookRotation();

            var (samples, maxMs, rotations) = engine.TakeHookStats();

            /* 只在明显偏慢时记一条：正常回调是几十微秒量级，
             * 超过 40ms 说明这台机器在输入链路上被拖住了，值得留证据。 */
            /* 有输入就记一条（10 秒一次、只在真的有滚动时），
             * 让"回调到底多快"变成可查的事实，而不是只在出问题时才留证据。 */
            if (samples > 0 && maxMs <= 40)
            {
                Log.Info($"钩子回调：{samples} 次，最大 {maxMs:0.00} ms");
            }

            if (samples > 0 && maxMs > 40)
                Log.Warn($"钩子回调耗时偏高：最大 {maxMs:0.0} ms / {samples} 次（已轮换 {rotations} 次）");

                /* 超过 250ms 就不是"偶发抖动"，而是真的在拖整机输入：
                 * 主动把钩子摘掉再装上 —— 摘掉的那一瞬间系统输入立刻恢复。 */
                if (maxMs > 250) engine.RotateHookForSafety();
        }
        catch
        {
            // 守护线程绝不能把进程带走
        }
    }

    public static void ExitApp(int exitCode = 0)
    {
        StopRuntimeAssist();
        UnregisterGlobalHotkeys();
        Services.Shutdown();
        if (Current is App app)
        {
            try { app._mutex?.ReleaseMutex(); } catch { /* 不是持有者（冒烟/自检路径） */ }
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
        /* ⚠ 必须先放开单实例互斥体，再拉起新进程。
         *
         * 新进程抢的是同一个命名互斥体（超时 0），而旧进程要跑完
         * Services.Shutdown（引擎、托盘、配置落盘、日志刷盘最多 500ms）才轮到
         * ExitApp 里那句 ReleaseMutex —— 两边是几百毫秒的竞态。
         * 输的那次：新进程看到"已有实例"，激活一下旧窗口就自己退出，
         * 用户看到的是**图标和窗口一起消失、新版本根本没起来**。
         *
         * 这里先把互斥体交出去（并置空，避免 ExitApp 二次释放抛异常），
         * 再启动新进程：新进程一定能拿到，之后旧进程安安静静退出。 */
        if (Current is App app)
        {
            try { app._mutex?.ReleaseMutex(); } catch { /* 不是持有者 */ }
            try { app._mutex?.Dispose(); } catch { }
            app._mutex = null;
        }

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
