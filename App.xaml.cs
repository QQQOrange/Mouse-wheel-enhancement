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

        // 诊断模式：只查一次更新并打印结果（不装钩子、不建窗口）
        if (e.Args.Any(a => a.Equals("--checkupdate", StringComparison.OrdinalIgnoreCase)))
        {
            var code = CheckUpdateCli(e.Args);
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

        /* 提权重启后回来：把设置里的「开机自启 / 以管理员身份启动」真正落到系统里。
         * 放在 Services.Init 之后（配置要等它加载），ShowMainWindow 之前
         * （用户点完开关重启回来，第一眼就该看到开关已经是"开"的状态）。 */
        if (e.Args.Any(a => a.Equals("--apply-startup", StringComparison.OrdinalIgnoreCase)))
            ApplyStartupFromConfig();
        else
            RepairStartupIfNeeded();

        var startMinimized = e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        if (!startMinimized) ShowMainWindow();

        if (Services.Config.Update.AutoCheck) _ = AutoCheckAsync();
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

        sb.AppendLine($"程序：{UpdateService.ExePath}");
        sb.AppendLine($"当前权限：{(Core.Native.IsElevated() ? "管理员" : "普通用户（创建/删除会失败）")}");
        sb.AppendLine($"任务名：{Core.AdminStart.TaskName}");
        sb.AppendLine(new string('-', 56));

        try
        {
            var exists = Core.AdminStart.IsEnabled();
            sb.AppendLine($"任务是否存在：{(exists ? "是" : "否")}");

            if (exists)
            {
                sb.AppendLine($"指向当前程序：{(Core.AdminStart.PointsToCurrentExe() ? "是" : "否 —— 开机不会启动，需要重建")}");
            }

            if (doCreate || doDelete)
            {
                sb.AppendLine();
                sb.AppendLine(doCreate ? "正在创建任务…" : "正在删除任务…");
                var ok = Core.AdminStart.Set(doCreate, out var err);
                sb.AppendLine(ok ? "结果：成功" : "结果：失败 —— " + err);
                if (doCreate) sb.AppendLine($"现在是否存在：{(Core.AdminStart.IsEnabled() ? "是" : "否")}");
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
    /// 提权重启之后的落地动作：按配置把两种开机自启方式同步到系统。
    ///
    /// <para>
    /// 之所以要「重启自己再回来做」而不是当场做：写计划任务必须管理员权限，
    /// 而一个已经在运行的普通权限进程**没法给自己提权**，只能重启。
    /// </para>
    /// </summary>
    private static void ApplyStartupFromConfig()
    {
        var b = Services.Config.Behavior;
        if (AutoStart.Apply(b.RunAtLogin, b.RunAtLoginAsAdmin, out var error))
        {
            Log.Info($"已按设置应用开机自启：开机启动={b.RunAtLogin}，管理员={b.RunAtLoginAsAdmin}");
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
    ///         或者任务还在却指向旧路径（程序被挪了目录）→ 重建。</item>
    ///   <item>配置说不用管理员自启，任务却还留着（上次删除失败）→ 清掉。</item>
    /// </list>
    /// 只在**已经是管理员**时动作 —— 普通权限下这一步注定失败，白刷日志。
    /// </summary>
    private static void RepairStartupIfNeeded()
    {
        try
        {
            var b = Services.Config.Behavior;
            if (!b.RunAtLogin || !Services.IsElevated) return;

            if (b.RunAtLoginAsAdmin)
            {
                if (!AdminStart.IsEnabled() || !AdminStart.PointsToCurrentExe())
                {
                    if (AdminStart.Set(true, out var err)) Log.Info("已同步管理员自启任务（存在性或路径修复）");
                    else Log.Warn("同步管理员自启任务失败：" + err);
                }
            }
            else if (AdminStart.IsEnabled())
            {
                AdminStart.Set(false, out _);
                Log.Info("已清理残留的管理员自启任务");
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
