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
    private const string MutexName = @"Local\MouseWheelEnhancement.SingleInstance";
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
    /// 页面按像素滚动的入口（挂在 App.xaml 里那条隐式 ScrollViewer 样式上）。
    ///
    /// <para>
    /// 为什么必须自己算：WPF 的 ScrollViewer **无视 delta 大小**，一个事件固定滚
    /// <see cref="SystemParameters.WheelScrollLines"/> 行（官方 issue dotnet/wpf#5936，
    /// 至今未改）。于是"一格"只能整块移动，我们的摊帧（把一格拆成 8 帧小 delta）
    /// 全被它当成"又滚了一格"，平滑自然无从谈起。
    /// </para>
    /// <para>
    /// 改成 <c>delta ÷ 120 × 行数 × 每行像素</c> 之后，滚动量就跟 delta 成正比了 ——
    /// 和浏览器同一套语义。这时 8 帧摊帧才真正生效：每帧 15 单位 ≈ 7.5 DIP，
    /// 八帧推完一格，肉眼看到的是连续推进而不是跳。
    /// </para>
    /// <para>
    /// 这里乘的"行数"**不是**什么补偿倍数，就是用户设的「每格滚动格数」本身：
    /// 一格 = N 行 = N × 20 DIP，和系统原生语义对齐（所以调 10 行时这一页也滚得远，
    /// 与其它程序一致）。值直接从注册表读，不用 <see cref="SystemParameters"/> ——
    /// 那是 WPF 自己缓存的一份副本，我们能少一份就少一份。
    /// </para>
    /// </summary>
    private void OnPreviewMouseWheelScroll(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        if (sender is not ScrollViewer sv) return;

        /* 诊断留痕（--wheeltest 专用）：每一条"为什么不换算 / 换算了多少"都记下来。
         * 正常使用时 WheelDiagOn 是 false，这里什么都不做。 */
        void Bypass(string why)
        {
            if (WheelDiagOn) WheelDiagLog.Enqueue($"bypass: {why}（delta={e.Delta}）");
        }

        /* 列表类容器（ListBox / ComboBox 下拉）是 CanContentScroll=true，按"项"滚。
         * 对它们换算像素没有意义，直接交回系统。 */
        if (sv.CanContentScroll) { Bypass("CanContentScroll（按项滚）"); return; }

        /* 滚不动就别拦：让事件继续隧道，外层（或系统）接。
         * 这一条同时解决了"页面到底了还吃掉滚轮"的老问题。 */
        if (sv.ScrollableHeight <= 0) { Bypass($"ScrollableHeight={sv.ScrollableHeight}"); return; }

        /* Ctrl+滚轮是缩放、Shift+滚轮是横向，都不是我们要管的。 */
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0)
        { Bypass("Ctrl/Shift 修饰键"); return; }

        /* 鼠标正悬在**内层可滚容器**上（比如设置页里的黑名单列表）时让给它。
         * Preview 是隧道、外层先收到，不判断就会把内层的滚动抢过来。 */
        if (HasInnerScrollable(e.OriginalSource as DependencyObject, sv)) { Bypass("有内层可滚容器"); return; }

        var lines = SystemWheelLines.Current();
        if (lines <= 0) { Bypass($"行数={lines}（≤0 不换算）"); return; }

        var px = e.Delta * PixelsPerLine * lines / (double)Native.WHEEL_DELTA;
        var before = sv.VerticalOffset;
        var planned = PlanScroll(before, px, sv.ScrollableHeight);

        /* 只有"夹过之后还是原地"才叫到边界 —— 那一侧真的没得滚了，
         * 不标 handled，让事件继续隧道（外层或系统接）。
         *
         * ⚠ 这里以前写的是 `Math.Abs(target - before) < 0.5`，把"位移不到半像素"
         * 也当成了边界。摊帧会把一格拆成 8 帧小 delta，v1.7.1 的零头累积又让尾帧
         * 出现 delta=1/2/3 这种值（px 只有 0.17~0.5），于是每一格的收尾帧
         * 全部被判成"在边界上"而放行 —— 而 WPF 的 ScrollViewer 拿到一个事件
         * 就滚**一整行**（16 px 起步；它缓存的行数若还是 3，就是 48 px）。
         * 我们辛辛苦苦摊出去 17 px，它一跳就是几十 px，一格直接顶到一页多。
         * 实测：慢滚一格 offset 跳 784.5 px（视口才 741 px），
         * 而绕过钩子时同样的一格只滚 20 px（正确值）。
         *
         * 现在按"夹过之后有没有动"判：0.33 px 也是位移，ScrollViewer 的
         * VerticalOffset 是 double，小数照样滚得动。见 <see cref="PlanScroll"/>。 */
        if (planned == null)
        {
            Bypass($"到边界了（offset={before:0.##}，delta={e.Delta}）");
            return;
        }

        /* 不直接应用：同一渲染帧内可能来好几个 delta（160 Hz 注入），
         * 每来一个就 ScrollToVerticalOffset 会白做一轮 Arrange（帮助页有上千个
         * TextBlock，一轮 Arrange 不便宜）。攒到下一个渲染帧再一次性应用 ——
         * 实际 Arrange 次数自动降到"渲染帧率"，而不是"注入帧率"。 */
        QueueScroll(sv, planned.Value - before);
        e.Handled = true;
        if (WheelDiagOn)
            WheelDiagLog.Enqueue($"px={px:0.##} lines={lines} delta={e.Delta} " +
                                 $"offset {before:0.##}→{planned.Value:0.##} " +
                                 $"Δ={planned.Value - before:0.##}");
    }

    /// <summary>
    /// 这一下该滚到哪。<c>null</c> = 这一侧真的没得滚了（该放行，让外层或系统接）。
    ///
    /// <para>
    /// 判据只有一条：<b>夹过之后有没有动</b>。到顶 / 到底 / 内容比视口还短时，
    /// <see cref="Math.Clamp(double,double,double)"/> 会把目标夹回当前值，
    /// 这时才放行。
    /// </para>
    /// <para>
    /// **不能把"位移小"当成边界。** 摊帧的尾帧只有 1~3 单位（0.17~0.5 px），
    /// 按位移大小判会把每一格的收尾全部放行给 WPF 原生，而它一个事件就滚一整行
    /// （16 px 起步）—— 一格直接顶到一页多。这是 v1.7.2 修掉的 bug，
    /// 判据单独抽成函数，自检可以直接钉住（见 SelfTest）。 </para>
    /// </summary>
    internal static double? PlanScroll(double offset, double px, double scrollableHeight)
    {
        /* 先把上界夹成非负：ScrollableHeight 理论上是 ExtentHeight - ViewportHeight
         * （不会为负），但 Math.Clamp 在 min > max 时是抛异常而不是返回，
         * 而这里正处在滚轮事件的处理链上 —— 抛一次就等于这一次滚动彻底丢掉。 */
        var max = Math.Max(0, scrollableHeight);
        var target = Math.Clamp(offset - px, 0, max);
        return target == offset ? null : target;
    }

    /// <summary>
    /// <paramref name="source"/> 到 <paramref name="outer"/> 之间有没有别的、**还能滚**的
    /// ScrollViewer。有就说明滚轮该归它。
    /// </summary>
    private static bool HasInnerScrollable(DependencyObject? source, ScrollViewer outer)
    {
        var node = source;
        while (node != null && !ReferenceEquals(node, outer))
        {
            if (node is ScrollViewer inner && inner.ScrollableHeight > 0) return true;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    private Mutex? _mutex;
    private EventWaitHandle? _activateEvent;

    /* ── 本程序窗口滚动的"帧合并" ──
     * 注入是 160 帧/秒，但 WPF 只在渲染帧真正画一次。把同一帧内的多次位移
     * 累加起来、到渲染帧一次应用，Arrange 次数就从"注入帧率"降到"渲染帧率"。 */
    private ScrollViewer? _pendingScrollViewer;
    private double _pendingScrollDelta;
    private bool _scrollFlushHooked;

    private void QueueScroll(ScrollViewer sv, double delta)
    {
        if (delta == 0) return;

        _pendingScrollViewer = sv;
        _pendingScrollDelta += delta;

        if (_scrollFlushHooked) return;
        _scrollFlushHooked = true;
        System.Windows.Media.CompositionTarget.Rendering += FlushScroll;
    }

    private void FlushScroll(object? sender, EventArgs e)
    {
        System.Windows.Media.CompositionTarget.Rendering -= FlushScroll;
        _scrollFlushHooked = false;

        var sv = _pendingScrollViewer;
        var delta = _pendingScrollDelta;
        _pendingScrollViewer = null;
        _pendingScrollDelta = 0;

        if (sv == null || delta == 0) return;
        // 窗口已经藏起来了：这一帧的尾巴丢掉，别留着下次打开时跳一下
        if (!sv.IsVisible) return;

        var max = Math.Max(0, sv.ScrollableHeight);
        sv.ScrollToVerticalOffset(Math.Clamp(sv.VerticalOffset + delta, 0, max));
    }

    /// <summary>
    /// 本次启动已经尝试过提权（命令行带 <see cref="Core.AdminStart.ElevateAttemptArg"/>）。
    ///
    /// <para>
    /// 用来断掉"提权没生效 → 再提权 → 还没生效"的死循环。提权失败的原因通常是
    /// 用户点了取消、组策略禁用 runas、或者凭据不对 —— 这些都不是重试能解决的。
    /// </para>
    /// </summary>
    private static bool _elevateTried;

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
        var sw = System.Diagnostics.Stopwatch.StartNew();
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
            /* 用 Stopwatch 定绝对时间点、Sleep(0) 让出，而不是 Thread.Sleep(18)：
             * 后者受系统计时器粒度影响（默认 15.6ms），实际间隔会在 18~31ms 之间乱跳，
             * 于是"倍率"读数每次都不一样，这条检查就没有回归价值了。 */
            var injectClock = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < 16; i++)
            {
                if (InputInjector.SendWheelWith(Native.WHEEL_DELTA, ForeignSignature)) sent++;
                var due = 18 * (i + 1);
                while (injectClock.ElapsedMilliseconds < due) Thread.Sleep(0);
            }
            Thread.Sleep(400);

            var boosted = Services.Engine.BoostedCount - beforeBoost;
            var passed = Services.Engine.PassedCount - beforePass;

            /* ── 自我增强：把光标挪到自己窗口里，再滚一次 ──
             *
             * 这一条是专门为"功能对自己不生效"补的。判定依据有两个，缺一不可：
             *   ① 目标的 exe 就是本程序（说明目标解析认得出自己）；
             *   ② 这次滚动被接管了（说明 Skipped 逻辑没有把它放行）。
             *
             * 只靠读代码看不出问题 —— 以前的默认配置就是把 ② 卡住的。 */
            var selfCode = CheckSelfBoost(w);
            if (selfCode != 0) code = selfCode;

            var themeCode = CheckTheme(w);
            if (themeCode != 0) code = themeCode;

            var wlCode = CheckWheelLinesUi(w);
            if (wlCode != 0) code = wlCode;

            var uiCode = CheckUpdatePageUi(w);
            if (uiCode != 0) code = uiCode;

            ShotUpdatePage(w);

            /* 最后再验一次托盘那条链路（会换掉当前窗口，所以放在所有取窗口的检查之后） */
            var trayCode = CheckTrayShowPath();
            if (trayCode != 0) code = trayCode;

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

            /* 托盘图标是 GDI+ 现画的，编译期查不出错；日志里放一份字符画，
             * 这样"图标画成了什么"有据可查，而不是只能盯着托盘猜。 */
            Log.Info("smoke: 托盘图标（启用态）\n" + TrayController.AsciiDump(true));

            Log.Info($"smoke: 刷新率 {RefreshRateMonitor.Hz} Hz（{RefreshRateMonitor.FrameIntervalMs:0.00} ms/帧），" +
                     $"高精度定时器={(Services.Engine.Animator.UsingHighResolutionTimer ? "可用" : "不可用（已退回毫秒等待）")}");

            ReportResources(sw);

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

    /// <summary>
    /// 更新页两处 UI 的硬检查 —— "改完 XAML 到底对不对"不用肉眼盯的那类：
    ///
    /// <para>
    /// ① **令牌框必须是暗文**。曾经是普通 TextBox，用户一粘贴整串令牌明文摆在那里；
    /// 必须是 <see cref="System.Windows.Controls.PasswordBox"/>。
    /// </para>
    /// <para>
    /// ② **主按钮禁用态必须换专用配色**。曾经的禁用 Trigger 是整块 Opacity 0.45，
    /// 蓝底白字一起冲淡，浅色主题下对比度直接塌到看不清（用户原话："蓝底黑字，有时候看不清"）。
    /// 现在改成 PrimaryBtnDisabledBg/Fg 两份调色板各一套，跟随主题。
    /// </para>
    ///
    /// 判定方式：读控件**模板里那个 Border 的真实 Background**，再和调色板里的值对。
    /// 这样换主题也成立 —— 两份调色板各有一份禁用色，谁生效就和谁比。
    /// </summary>
    /// <summary>
    /// 验证「托盘单击能打开窗口」这条链路。
    ///
    /// <para>
    /// 托盘单击不是从 UI 线程过来的：<see cref="TrayController"/> 里用的是
    /// <c>Task.Delay(...).ContinueWith(..., TaskScheduler.Default)</c> —— 线程池、MTA。
    /// 而 WPF 窗口只能在 UI 线程创建。以前 <c>ShowWindow</c> 直接
    /// <c>new MainWindow()</c>，在 MTA 上必抛"调用线程必须为 STA"，
    /// 异常被 <c>ShowMainWindow</c> 的 catch 吞成一条 WARN ——
    /// **用户看到的是"点托盘没反应"，日志里也只有一行 WARN**。
    /// </para>
    /// <para>
    /// 原有的 `--trayprobe` 只统计三条异常通道，这条被吞掉的路径它抓不到，
    /// 所以必须在冒烟里用**同一套线程环境**真的喊一次，再看窗口有没有出来。
    /// </para>
    /// </summary>
    private static int CheckTrayShowPath()
    {
        try
        {
            // 丢掉当前窗口引用，强制让 ShowWindow 走"新建窗口"那条（原来会崩的就是它）
            var old = Services.Window;
            Services.Window = null;
            try { old?.Hide(); } catch { }

            // 与托盘单击完全一样的调用环境：线程池 → Services.ShowWindow()
            Task.Run(() => Services.ShowWindow()).GetAwaiter().GetResult();

            // BeginInvoke 排的活要泵一次消息队列才会执行
            Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

            var w = Services.Window;
            if (w != null && w.IsVisible)
            {
                Log.Info("smoke: 托盘（线程池）路径能正常打开窗口");
                return 0;
            }

            Log.Error($"smoke: 托盘路径没能打开窗口（Window={(w == null ? "null" : "有但不可见")}）");
            return 6;
        }
        catch (Exception ex)
        {
            Log.Error("smoke: 托盘路径抛异常", ex);
            return 6;
        }
    }

    private static int CheckUpdatePageUi(MainWindow w)
    {
        var code = 0;

        // WCAG 对比度：禁用态不是"装饰性文字"，也得能读
        static double Lin(System.Windows.Media.Color c)
        {
            static double Ch(double v)
            {
                var s = v / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
        }
        static double Contrast(System.Windows.Media.Color a, System.Windows.Media.Color b)
        {
            var l1 = Lin(a);
            var l2 = Lin(b);
            return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
        }
        static string Hex(System.Windows.Media.Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        try
        {
            w.SelectTab(4);          // 更新页
            w.UpdateLayout();

            /* ① 暗文 ── */
            if (w.TokenBox is not System.Windows.Controls.PasswordBox)
            {
                Log.Error("smoke: 令牌输入框不是 PasswordBox —— 令牌会以明文显示在屏幕上");
                code = 20;
            }
            else
            {
                Log.Info("smoke: 令牌输入框是 PasswordBox（暗文）");
            }

            /* ② 主按钮禁用态配色 ── */
            var wantBg = (System.Windows.Media.SolidColorBrush)w.FindResource("PrimaryBtnDisabledBg");
            var wantFg = (System.Windows.Media.SolidColorBrush)w.FindResource("PrimaryBtnDisabledFg");

            var btn = w.SaveTokenBtn;
            var bd = (System.Windows.Controls.Border)btn.Template.FindName("Bd", btn);

            btn.IsEnabled = false;
            w.UpdateLayout();
            var disBg = (bd.Background as System.Windows.Media.SolidColorBrush)?.Color;
            var disFg = (btn.Foreground as System.Windows.Media.SolidColorBrush)?.Color;
            var disOpacity = bd.Opacity;

            btn.IsEnabled = true;
            w.UpdateLayout();
            var enBg = (bd.Background as System.Windows.Media.SolidColorBrush)?.Color;

            Log.Info($"smoke: 主按钮禁用态 底色={Hex(disBg ?? default)} 字色={Hex(disFg ?? default)} " +
                     $"整体不透明度={disOpacity:0.00}；常态底色={Hex(enBg ?? default)}");

            if (disBg != wantBg.Color)
            {
                Log.Error($"smoke: 主按钮禁用态底色不是专用配色（期望 {Hex(wantBg.Color)}，" +
                          $"实际 {(disBg.HasValue ? Hex(disBg.Value) : "取不到")}）");
                code = 21;
            }
            if (disFg != wantFg.Color)
            {
                Log.Error($"smoke: 主按钮禁用态字色不是专用配色（期望 {Hex(wantFg.Color)}，" +
                          $"实际 {(disFg.HasValue ? Hex(disFg.Value) : "取不到")}）");
                code = 22;
            }
            // 整块褪色正是"蓝底黑字看不清"的成因，不允许回来
            if (disOpacity < 0.99)
            {
                Log.Error($"smoke: 主按钮禁用态仍在整块褪色（不透明度 {disOpacity:0.00}）—— " +
                          "蓝底白字会被一起冲淡，这就是『蓝底黑字看不清』的根因");
                code = 23;
            }
            if (disBg.HasValue && disFg.HasValue && Contrast(disBg.Value, disFg.Value) < 4.0)
            {
                Log.Error($"smoke: 主按钮禁用态对比度只有 {Contrast(disBg.Value, disFg.Value):0.00}:1，低于 4:1");
                code = 24;
            }
            if (disBg.HasValue && enBg.HasValue && disBg.Value == enBg.Value)
            {
                Log.Error("smoke: 主按钮禁用态和常态底色一模一样，用户看不出按钮禁用了");
                code = 25;
            }
        }
        catch (Exception ex)
        {
            Log.Error("smoke: 更新页 UI 检查抛异常", ex);
            code = 26;
        }
        return code;
    }

    /// <summary>
    /// smoke 附赠：把更新页按浅色/深色各截一张图，存进数据目录。
    ///
    /// <para>
    /// 圆角、令牌暗文、禁用态配色这类"看一眼就知道对不对"的东西，断言只能覆盖到
    /// 颜色值和控件类型；像素层面的观感（圆角是不是真圆了、暗文是不是一串圆点、
    /// 浅色下按钮还清不清楚）得有张图才说得清。
    /// </para>
    /// <para>
    /// 用 <see cref="System.Windows.Media.Imaging.RenderTargetBitmap"/> 而不是 BitBlt：
    /// 它渲染的是视觉树，不依赖屏幕 DC、窗口可见性和置顶，所以窗口被别的进程挡着
    /// （甚至桌面被锁）也照样出图 —— 实测里那些 UIPI / 遮挡坑一个都碰不到。
    /// </para>
    /// <para>
    /// 截图前会写一个假令牌把界面推到"已保存"态（PasswordBox 显示圆点、
    /// 「保存令牌」禁用），这正是本轮两处改动的对照面；用完把原令牌还回去。
    /// </para>
    /// </summary>
    private static void ShotUpdatePage(MainWindow w)
    {
        var cfgMode = Theme.Normalize(Services.Config.Appearance.Theme);
        var origToken = Services.Store.CurrentToken();
        try
        {
            Services.Store.SetToken("ghp_smoketest0000000000000000000000");
            w.RefreshTokenUi();
            w.SelectTab(4);          // 更新页
            w.UpdateLayout();

            foreach (var (mode, name) in new[] { (Theme.Light, "light"), (Theme.Dark, "dark") })
            {
                Theme.SetMode(mode);
                w.UpdateLayout();
                // DynamicResource 换字典要过一次渲染优先级才能落到新画刷上
                w.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

                var path = Path.Combine(AppPaths.DataDir, $"smoke-update-{name}.png");
                SaveWindowShot(w, path);
                Log.Info($"smoke: 更新页截图（{name}）→ {path}");
            }
        }
        catch (Exception ex)
        {
            // 截图只是留证，失败不影响 smoke 判定
            Log.Warn("smoke: 截图失败（不影响判定）：" + ex.Message);
        }
        finally
        {
            Services.Store.SetToken(origToken ?? "");
            w.RefreshTokenUi();
            Theme.SetMode(cfgMode);
            w.UpdateLayout();
        }
    }

    private static void SaveWindowShot(MainWindow w, string path)
    {
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
            Math.Max(1, (int)w.ActualWidth),
            Math.Max(1, (int)w.ActualHeight),
            96, 96,
            System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(w);

        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    /// <summary>
    /// `--wheellines`：报告系统「每次滚动几行」的现状；带 `--fix` 时按旧版备份还原。
    ///
    /// <para>
    /// 这一项是 HKCU 里的**全局设置**，被写成 1 行之后所有程序都会变慢，
    /// 而界面只显示一个数字，看不出"是谁改的、原来是多少"。
    /// 这个入口把三样东西摆在一起：现在的值、旧版留下的备份、谁在维护它。
    /// </para>
    /// <para>
    /// 只读运行（不加 --fix）不会写任何东西 —— 和程序正常启动的行为一致。
    /// </para>
    /// </summary>
    private static int WheelLinesCli(string[] args)
    {
        static string Describe(int v) =>
            v == Native.WHEELSCROLLLINES_PAGE ? "-1（一次滚一页）"
            : v == 0 ? "0（读取失败）"
            : v + " 行";

        var sb = new System.Text.StringBuilder();
        try
        {
            var cur = Core.SystemWheelLines.Current();
            var backup = Core.SystemWheelLines.BackupValue();

            sb.AppendLine($"软件版本：v{Log.Version}");
            sb.AppendLine($"数据目录：{AppPaths.DataDir}");
            sb.AppendLine(new string('-', 56));
            sb.AppendLine($"系统「每次滚动几行」：{Describe(cur)}");
            sb.AppendLine($"旧版接管备份：{(backup > 0 ? Describe(backup) : "（没有）")}");
            sb.AppendLine();
            sb.AppendLine("说明：这个值是系统的全局设置，软件启动、退出都不会改它；");
            sb.AppendLine("      只有你在设置页拖「每格滚动格数」时才会写。");

            if (args.Any(a => a.Equals("--fix", StringComparison.OrdinalIgnoreCase)))
            {
                sb.AppendLine();
                if (backup <= 0)
                {
                    sb.AppendLine("--fix：没有备份文件，无法自动还原。");
                    sb.AppendLine("      请手动到 设置 → 蓝牙和其他设备 → 鼠标 → 「每次滚动以下行数」改回来。");
                    return WriteCliResult("wheellines.txt", sb.ToString(), 0);
                }

                var wrote = Core.SystemWheelLines.RestoreFromBackup(force: true);
                sb.AppendLine(wrote
                    ? $"--fix：已按备份写回 {Describe(Core.SystemWheelLines.Current())}"
                    : $"--fix：当前已经等于备份值（{Describe(Core.SystemWheelLines.Current())}），没有改动");
            }

            return WriteCliResult("wheellines.txt", sb.ToString(), 0);
        }
        catch (Exception ex)
        {
            sb.AppendLine("失败：" + ex);
            return WriteCliResult("wheellines.txt", sb.ToString(), 2);
        }
    }

    /// <summary>
    /// `--checkupdate`：只走一次「检查更新」并把结论写出来。
    /// 用于排查"为什么点检查更新没反应"—— 界面只给一句人话，这里给完整判据
    /// （仓库、用没用令牌、有没有新版、包多大、有没有校验值）。
    /// </summary>
    private static int CheckUpdateCli(string[] args)
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            /* 两个可选参数（都只为"验证发版流程"存在）：
             *   --as 1.0.0   假装当前是这个版本 → 用来测"发现新版"这条分支
             *   --download   真的把包下下来并校验 SHA256（**不替换程序**） */
            var assumeIndex = Array.FindIndex(args, a => a.Equals("--as", StringComparison.OrdinalIgnoreCase));
            var assume = assumeIndex >= 0 && assumeIndex + 1 < args.Length ? args[assumeIndex + 1] : null;
            var doDownload = args.Any(a => a.Equals("--download", StringComparison.OrdinalIgnoreCase));
            var current = string.IsNullOrWhiteSpace(assume) ? Log.Version : assume.Trim();
            var store = new ConfigStore();
            var svc = new UpdateService(store);

            sb.AppendLine($"当前版本：{current}" + (assume != null ? $"（--as 指定，真实版本 {Log.Version}）" : ""));
            sb.AppendLine($"仓库：{svc.Repo}");
            sb.AppendLine($"最新版接口：{svc.LatestApi}");
            sb.AppendLine($"网页备用：{svc.LatestWeb}");
            sb.AppendLine($"令牌：{(store.CurrentToken() == null ? "未填写（匿名，60 次/小时）" : "已填写（5000 次/小时）")}");
            sb.AppendLine(new string('-', 56));

            /* ⚠ 不能用 CheckAsync(...).GetAwaiter().GetResult()：OnStartup 跑在 WPF 的
             * UI 线程上，那里装了 DispatcherSynchronizationContext，await 之后的续接
             * 会被排回同一个线程，而这个线程正卡在 GetResult() 上 —— 直接死锁。
             * Task.Run 让它跑在线程池（无同步上下文），再阻塞等待就安全了；
             * 另外加个超时兜底，命令行诊断绝不能挂死。 */
            var outcome = Task.Run(() => svc.CheckAsync(current))
                .WaitAsync(TimeSpan.FromSeconds(45))
                .GetAwaiter().GetResult();
            if (!outcome.Ok)
            {
                sb.AppendLine("结果：失败");
                sb.AppendLine("原因：" + outcome.Error);
            }
            else
            {
                var i = outcome.Info!;
                sb.AppendLine($"最新版本：v{i.Version}（tag {i.Tag}）");
                sb.AppendLine($"有没有新版：{(i.Newer ? "有" : "没有，已是最新")}");
                sb.AppendLine($"这次用上令牌：{(i.Authed ? "是" : "否")}");
                if (i.Size > 0) sb.AppendLine($"更新包：{Text.HumanSize(i.Size)}");
                if (!string.IsNullOrEmpty(i.Url)) sb.AppendLine($"下载地址：{i.Url}");
                // "已是最新"时走的是省额度的 302 路径，根本没拉版本说明 —— 别误报成"没写 SHA"
                sb.AppendLine("校验值：" + (string.IsNullOrEmpty(i.Sha256)
                    ? (i.Newer ? "（版本说明里没写 SHA256）" : "（已是最新，未拉取版本说明）")
                    : i.Sha256));
                if (i.AuthWarning != null) sb.AppendLine("提示：" + i.AuthWarning);
                if (!i.Newer)
                {
                    sb.AppendLine();
                    sb.AppendLine("（已是最新，所以没有可下载的包 —— 这是正常结果，不是错误）");
                }
                else if (doDownload)
                {
                    sb.AppendLine();
                    sb.AppendLine("开始下载并校验（不替换程序）…");
                    var staged = Task.Run(() => svc.DownloadAsync(i, null))
                        .WaitAsync(TimeSpan.FromSeconds(180))
                        .GetAwaiter().GetResult();
                    var len = new FileInfo(staged).Length;
                    sb.AppendLine($"下载完成：{staged}");
                    sb.AppendLine($"体积 {len} 字节，SHA256 与版本说明" +
                                  (string.IsNullOrEmpty(i.Sha256) ? "（说明里没写，只比了体积）" : "一致 ✓"));
                    try { File.Delete(staged); } catch { }
                    sb.AppendLine("已删除临时文件，未改动正在运行的程序");
                }
            }
        }
        catch (Exception ex)
        {
            // 把整条异常链打出来：真正的原因十有八九在最里面那层
            sb.AppendLine("失败：");
            for (var e2 = ex; e2 != null; e2 = e2.InnerException)
                sb.AppendLine($"  {e2.GetType().Name}: {e2.Message}");
            return WriteCliResult("checkupdate.txt", sb.ToString(), 2);
        }
        return WriteCliResult("checkupdate.txt", sb.ToString(), 0);
    }

    private static int WriteCliResult(string fileName, string text, int code)
    {
        try
        {
            var path = Path.Combine(AppPaths.DataDir, fileName);
            File.WriteAllText(path, text, new System.Text.UTF8Encoding(false));
            try
            {
                Native.AttachConsole(Native.ATTACH_PARENT_PROCESS);
                Console.Out.Write(text);
            }
            catch { }
        }
        catch { }
        return code;
    }

    /// <summary>
    /// 「每格滚动格数」的界面必须和系统里的值**完全一致**。
    ///
    /// <para>
    /// 这一项最容易出的错不是"读错值"，而是"两处各说一套"：
    /// 标签写着 3 行、滑块停在 1、系统里其实是 5 —— 用户看到的就是"这软件在瞎报"。
    /// 根源往往是两个入口（载入配置 / 窗口激活）各写一套同步逻辑。
    /// 现在只有 <c>MainWindow.SyncWheelLinesFromSystem()</c> 一个入口，
    /// 这条断言就是钉住它的：标签、滑块、可用状态三者都得跟着系统走。
    /// </para>
    /// <para>
    /// 只读，不改任何设置 —— 冒烟不该顺手改用户的系统行数。
    /// </para>
    /// </summary>
    private static int CheckWheelLinesUi(MainWindow w)
    {
        try
        {
            var sys = Core.SystemWheelLines.Current();
            var usable = Core.SystemWheelLines.IsUsable(sys);

            var label = w.WheelLinesValue.Text;
            var sliderValue = w.WheelLinesSlider.Value;
            var sliderEnabled = w.WheelLinesSlider.IsEnabled;

            var expectLabel =
                sys == Core.SystemWheelLines.PageMode ? "一次一页"
                : usable ? sys + " 行"
                : "读取不到";

            Log.Info($"smoke: 每格滚动格数 → 系统 {sys} 行，标签「{label}」，" +
                     $"滑块 {sliderValue:0}（可用={sliderEnabled}）");

            if (label != expectLabel)
            {
                Log.Error($"smoke: 标签和系统不一致（期望「{expectLabel}」，实际「{label}」）");
                return 27;
            }

            if (usable && Math.Abs(sliderValue - sys) > 0.01)
            {
                Log.Error($"smoke: 滑块位置和系统不一致（系统 {sys}，滑块 {sliderValue:0}）");
                return 28;
            }

            if (sliderEnabled != usable)
            {
                Log.Error($"smoke: 滑块可用状态不对（系统值 {(usable ? "可调" : "不可调")}，滑块 {(sliderEnabled ? "可调" : "不可调")}）");
                return 29;
            }

            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("smoke: 每格滚动格数 UI 检查抛异常", ex);
            return 30;
        }
    }

    /// <summary>
    /// 验证主题能**热切换**：不重建窗口，只换调色板字典，已有的控件应当立刻变色。
    ///
    /// <para>
    /// 这是本功能唯一真正容易做错的地方：如果样式里用的是 StaticResource，
    /// 换字典对已建好的元素毫无作用 —— 界面看起来"没反应"，重开一次才对。
    /// 所以这里读的是**一个真实控件的 Background**（侧栏，绑的是 PanelBg），
    /// 而不是资源字典里的值。后者换了只能证明"字典换了"，证明不了"界面跟着变了"。
    /// </para>
    /// <para>
    /// 曾经这里读的是 <c>w.Background</c>，而窗口底是渐变画笔 ——
    /// <c>as SolidColorBrush</c>永远失败，落到 <see cref="Colors.Transparent"/>，
    /// 而 Transparent 的 RGB 是 #FFFFFF（alpha 为 0），于是无论切到哪个主题
    /// 都打印"底色 #FFFFFF、亮度 1.00"，这条检查从 UI 改版起就一直在误报。
    /// </para>
    /// </summary>
    private static int CheckTheme(MainWindow w)
    {
        static double Lum(System.Windows.Media.Color c) =>
            (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;

        static System.Windows.Media.Color BgOf(System.Windows.Controls.Border e) =>
            (e.Background as System.Windows.Media.SolidColorBrush)?.Color
            ?? System.Windows.Media.Colors.Transparent;

        var cfgMode = Theme.Normalize(Services.Config.Appearance.Theme);
        var code = 0;

        try
        {
            Theme.SetMode(Theme.Light);
            w.UpdateLayout();
            var light = BgOf(w.Sidebar);

            Theme.SetMode(Theme.Dark);
            w.UpdateLayout();
            var dark = BgOf(w.Sidebar);

            Log.Info($"smoke: 主题热切换 → 浅色侧栏 #{light.R:X2}{light.G:X2}{light.B:X2}" +
                     $"（亮度 {Lum(light):0.00}），深色侧栏 #{dark.R:X2}{dark.G:X2}{dark.B:X2}" +
                     $"（亮度 {Lum(dark):0.00}）");

            if (Lum(light) < 0.7)
            {
                Log.Error("smoke: 切到浅色后窗口底色仍然很暗 —— 静态资源没跟着换");
                code = 10;
            }
            if (Lum(dark) > 0.3)
            {
                Log.Error("smoke: 切回深色后窗口底色仍然很亮");
                code = 11;
            }
            if (Math.Abs(Lum(light) - Lum(dark)) < 0.4)
            {
                Log.Error("smoke: 两种主题的底色几乎一样，切换没有真的生效");
                code = 12;
            }
        }
        catch (Exception ex)
        {
            Log.Error("smoke: 主题切换抛异常", ex);
            code = 13;
        }
        finally
        {
            // 回到配置里的模式（可能是"跟随系统"）
            Theme.SetMode(cfgMode);
            w.UpdateLayout();
            Log.Info($"smoke: 主题已复原为「{cfgMode}」→ 实际 {Theme.Effective}");
        }

        return code;
    }

    /// <summary>
    /// 验证「增强对自己也生效」：光标移进设置窗口，注入滚轮，检查是否被接管。
    ///
    /// <para>
    /// 顺带反向验证一次开关：把「在本窗口暂停增强」临时打开，同样注入，
    /// 应当一次都不接管（那就是以前的默认行为）。测完立刻改回去，
    /// 免得把测试状态落到用户配置里。
    /// </para>
    /// </summary>
    private static int CheckSelfBoost(MainWindow w)
    {
        var own = AppRuleName.Normalize(Environment.ProcessPath);
        if (own.Length == 0)
        {
            Log.Warn("smoke: 取不到自己的 exe 名，跳过自我增强检查");
            return 0;
        }

        var settings = RuntimeSettings.From(Services.Config);
        if (settings.ShouldSkipSelf(own))
        {
            Log.Error("smoke: 配置里仍然把自己排除了 —— 在设置窗口里滚不出效果");
            return 6;
        }

        if (!Native.GetCursorPos(out var saved)) saved = default;

        var p = w.PointToScreen(new System.Windows.Point(w.ActualWidth / 2, w.ActualHeight / 2));
        if (!Native.SetCursorPos((int)p.X, (int)p.Y))
        {
            Log.Warn("smoke: 光标挪不过去（会话可能被锁），跳过自我增强检查");
            return 0;
        }

        /* 先把窗口提到最前，再确认那个坐标底下**真的是我们自己的窗口**。
         *
         * 冒烟经常在浏览器/编辑器开着的时候跑：那个坐标上压着别人的窗口时，
         * WindowFromPoint 解析出来是别人，这条断言就变成了"别人的窗口能不能被增强"，
         * 结果必然是失败 —— 但代码其实没问题。所以这里做两件事：
         * ① 临时置顶并激活，把窗口抢到最上面；
         * ② 仍被别人压着（会话锁定、桌面切换中）就跳过，而不是误报失败。 */
        var myHwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
        try
        {
            w.Activate();
            w.Topmost = true;
            w.Topmost = false;
        }
        catch { /* 抢不到前台不影响后面的判定 */ }

        Thread.Sleep(180);                 // 让光标落位、窗口层级稳定，目标解析才认得出是自己

        var under = Native.WindowFromPoint(new Native.POINT { X = (int)p.X, Y = (int)p.Y });
        var root = under == IntPtr.Zero ? IntPtr.Zero : Native.GetAncestor(under, Native.GA_ROOT);
        if (root != myHwnd)
        {
            Log.Warn("smoke: 光标位置被别的窗口占着（桌面上还有其它窗口压在上面），跳过自我增强检查");
            return 0;
        }

        var before = Services.Engine.BoostedCount;
        var sent = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 12; i++)
        {
            if (InputInjector.SendWheelWith(Native.WHEEL_DELTA, ForeignSignature)) sent++;
            var due = 18 * (i + 1);
            while (clock.ElapsedMilliseconds < due) Thread.Sleep(0);
        }
        Thread.Sleep(350);

        var got = Services.Engine.BoostedCount - before;
        var target = Services.Engine.LastTarget.Exe;
        Log.Info($"smoke: 光标在自己窗口内 → 目标 exe={target}，注入 {sent}/12 次，接管 {got} 次");

        var result = 0;
        if (sent == 0)
        {
            Log.Warn("smoke: 注入失败（桌面可能被锁），跳过自我增强判定");
        }
        else if (!string.Equals(target, own, StringComparison.OrdinalIgnoreCase))
        {
            Log.Error($"smoke: 目标没解析成自己（自己的 exe={own}），无法判定");
            result = 7;
        }
        else if (got == 0)
        {
            Log.Error("smoke: 自己窗口里的滚动没有被接管 —— 自我增强不生效");
            result = 8;
        }

        /* 反向：打开「在本窗口暂停增强」，应当一次都不接管 */
        if (result == 0)
        {
            var cfg = Services.Config;
            var keep = cfg.Behavior.PauseWhileSettingsOpen;
            try
            {
                cfg.Behavior.PauseWhileSettingsOpen = true;
                Services.Engine.Apply(cfg);
                // 120 ms 在高负载时偶尔不够：注入会抢在 Volatile.Write 生效前跑完，
                // 表现为"暂停开关没起作用"的假失败。250 ms 留足余量。
                Thread.Sleep(250);

                var beforeOff = Services.Engine.BoostedCount;
                for (var i = 0; i < 10; i++) InputInjector.SendWheelWith(Native.WHEEL_DELTA, ForeignSignature);
                Thread.Sleep(300);
                var gotOff = Services.Engine.BoostedCount - beforeOff;

                Log.Info($"smoke: 打开「在本窗口暂停增强」后，接管 {gotOff} 次（期望 0）");
                if (gotOff != 0)
                {
                    Log.Error("smoke: 暂停开关没起作用");
                    result = 9;
                }
            }
            finally
            {
                // 一定要还原：冒烟退出时会保存配置，别把测试值写进用户文件
                cfg.Behavior.PauseWhileSettingsOpen = keep;
                Services.Engine.Apply(cfg);
                Native.SetCursorPos(saved.X, saved.Y);
            }
        }
        else
        {
            Native.SetCursorPos(saved.X, saved.Y);
        }

        return result;
    }

    /// <summary>冒烟测试专用的"外来"标记：不能等于 <c>InputInjector.Signature</c>。</summary>
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
    public static void ExitApp(int exitCode = 0)
    {
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
