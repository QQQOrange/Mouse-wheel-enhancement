using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using MouseWheelEnhancement.Core;

namespace MouseWheelEnhancement;

/// <summary>
/// 第二代探针：把托盘点击**真正发生的地方**搬进测试里。
///
/// <para>
/// 第一代探针（<see cref="CrashProbe"/>）在 WPF Dispatcher 的干净帧里跑，
/// 8 个用例全过 —— 说明触发点不在"WPF 上下文"。
/// 而真实世界里，托盘图标是 WinForms 的 <c>NotifyIcon</c>：它的鼠标消息
/// 走的是**原生 Win32 窗口过程**，异常从那里抛出去之后：
/// </para>
/// <list type="bullet">
///   <item>不走 WPF 的 <c>DispatcherUnhandledException</c>（那不是 DispatcherOperation）；</item>
///   <item>不走 <c>AppDomain.UnhandledException</c>（WinForms 先把它吃掉）；</item>
///   <item>于是直接撞上框架的默认异常对话框 —— 用户看到的就是"应用程序的组件中
///         发生了未经处理的异常。如果单击"继续"……"，而日志里一条 ERROR 都没有。</item>
/// </list>
///
/// <para>
/// 所以这一代的重点是**验证异常通道**，并把"在原生 WndProc 里调
/// <c>Services.ShowWindow()</c>"这条真实路径跑一遍。
/// </para>
/// </summary>
internal static class WndProcProbe
{
    private const int WM_APP_TEST = 0x8001;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr RegisterWindowMessage(string name);

    /// <summary>
    /// 一个用 WinForms <see cref="NativeWindow"/> 实现的裸窗口。
    /// 它的 <c>WndProc</c> 与 <c>NotifyIcon</c> 内部那个窗口**完全同构**：
    /// 消息从原生消息泵进来，异常也从这里抛出去。
    /// </summary>
    private sealed class TestWindow : System.Windows.Forms.NativeWindow
    {
        public Action OnMessage = () => { };
        public int Hits;

        protected override void WndProc(ref System.Windows.Forms.Message m)
        {
            if (m.Msg == WM_APP_TEST)
            {
                Hits++;
                // 异常故意不 catch —— 让它按真实路径逃出去
                OnMessage();
                return;
            }
            base.WndProc(ref m);
        }
    }

    /// <summary>
    /// 用 WinForms <see cref="System.Windows.Forms.Control"/> 派生的探针窗体。
    /// <c>NotifyIcon</c> 内部正是这么干的（Control + 自己的消息循环），
    /// 异常在 <c>Control.WndProc</c> 里抛出时走的是 WinForms 的
    /// <c>Application.ThreadException</c> 通道 —— 和裸 NativeWindow 不一样。
    /// </summary>
    private sealed class ControlWindow : System.Windows.Forms.Control
    {
        public Action OnMessage = () => { };
        public int Hits;

        protected override void WndProc(ref System.Windows.Forms.Message m)
        {
            if (m.Msg == WM_APP_TEST)
            {
                Hits++;
                OnMessage();
                return;
            }
            base.WndProc(ref m);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private static readonly List<string> Report = new();

    private static void Note(string s) => Report.Add(s);

    private static void Note(string fmt, params object?[] args) => Report.Add(string.Format(fmt, args));

    public static int Run()
    {
        Report.Clear();

        /* ── 1. 装上"探针版"的异常监听，看异常到底走哪条通道 ── */
        var dispatcherCaught = new List<Exception>();
        var appDomainCaught = new List<Exception>();
        var threadCaught = new List<Exception>();

        void OnDispatcher(object? s, DispatcherUnhandledExceptionEventArgs e)
        {
            dispatcherCaught.Add(e.Exception);
            e.Handled = true;
        }
        void OnAppDomain(object? s, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex) appDomainCaught.Add(ex);
        }
        void OnThreadException(object? s, System.Threading.ThreadExceptionEventArgs e)
        {
            threadCaught.Add(e.Exception);
        }

        Dispatcher.CurrentDispatcher.UnhandledException += OnDispatcher;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomain;
        System.Windows.Forms.Application.ThreadException += OnThreadException;
        System.Windows.Forms.Application.SetUnhandledExceptionMode(
            System.Windows.Forms.UnhandledExceptionMode.CatchException);

        Services.Init();

        var w = new MainWindow();
        w.Show();
        w.UpdateLayout();
        Services.Window = w;

        var test = new TestWindow();
        test.CreateHandle(new System.Windows.Forms.CreateParams
        {
            Caption = "MWE.Probe",
            Width = 0,
            Height = 0,
        });
        Note($"探针窗口已建：hwnd=0x{test.Handle.ToInt64():X}");

        /* ── 2. 在原生 WndProc 里调真实的那条路径 ── */
        void Post(Action a)
        {
            test.OnMessage = a;
            PostMessage(test.Handle, WM_APP_TEST, IntPtr.Zero, IntPtr.Zero);
            // 让消息泵把这条消息取走并派发
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }

        // 用例 1：WndProc 里调 ShowMainWindow（= 托盘左键点击）
        Post(() => Services.ShowWindow());
        Note("用例1（WndProc → ShowWindow）后：dispatcher={0} appdomain={1} thread={2}",
            dispatcherCaught.Count, appDomainCaught.Count, threadCaught.Count);
        foreach (var ex in dispatcherCaught.Concat(appDomainCaught).Concat(threadCaught))
            Note("   捕获：{0}: {1}", ex.GetType().Name, ex.Message);

        // 用例 2：WndProc 里连发三次（= 托盘双击：MouseClick×2 + DoubleClick）
        dispatcherCaught.Clear(); appDomainCaught.Clear(); threadCaught.Clear();
        Post(() => { Services.ShowWindow(); Services.ShowWindow(); Services.ShowWindow(); });
        Note("用例2（WndProc → ShowWindow×3）后：dispatcher={0} appdomain={1} thread={2}",
            dispatcherCaught.Count, appDomainCaught.Count, threadCaught.Count);
        foreach (var ex in dispatcherCaught.Concat(appDomainCaught).Concat(threadCaught))
            Note("   捕获：{0}: {1}", ex.GetType().Name, ex.Message);

        // 用例 3：WndProc 里开 ContextMenu 后再 ShowWindow
        dispatcherCaught.Clear(); appDomainCaught.Clear(); threadCaught.Clear();
        Post(() =>
        {
            var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = w.ThemeBtn };
            menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "探针项" });
            menu.IsOpen = true;
            Services.ShowWindow();
        });
        Note("用例3（WndProc → 开菜单 + ShowWindow）后：dispatcher={0} appdomain={1} thread={2}",
            dispatcherCaught.Count, appDomainCaught.Count, threadCaught.Count);
        foreach (var ex in dispatcherCaught.Concat(appDomainCaught).Concat(threadCaught))
            Note("   捕获：{0}: {1}", ex.GetType().Name, ex.Message);

        // 用例 4：WndProc 里 ShowWindow 后立刻 Hide（= 窗口正开着时点托盘）
        dispatcherCaught.Clear(); appDomainCaught.Clear(); threadCaught.Clear();
        Post(() => { Services.ShowWindow(); w.Hide(); });
        Note("用例4（WndProc → ShowWindow + Hide）后：dispatcher={0} appdomain={1} thread={2}",
            dispatcherCaught.Count, appDomainCaught.Count, threadCaught.Count);
        foreach (var ex in dispatcherCaught.Concat(appDomainCaught).Concat(threadCaught))
            Note("   捕获：{0}: {1}", ex.GetType().Name, ex.Message);

        /* ── 3. 验证"异常从 WndProc 逃出去"时，各通道到底谁接 ──
         *
         * 这一条是关键：故意在 WndProc 里抛一个和崩溃一模一样的异常，
         * 看它落到哪条通道 —— 这决定修复该挂在哪。 */
        dispatcherCaught.Clear(); appDomainCaught.Clear(); threadCaught.Clear();
        Post(() => throw new ArgumentException("VisualTarget 的根 Visual 不能具有父级。"));
        Note("用例5（裸 NativeWindow WndProc 里直接抛）后：dispatcher={0} appdomain={1} thread={2}，WndProc 命中 {3} 次",
            dispatcherCaught.Count, appDomainCaught.Count, threadCaught.Count, test.Hits);
        foreach (var ex in dispatcherCaught.Concat(appDomainCaught).Concat(threadCaught))
            Note("   捕获：{0}: {1}", ex.GetType().Name, ex.Message);

        /* ── 用例 6：WinForms Control 派生的窗口（与 NotifyIcon 同构）里抛异常 ── */
        dispatcherCaught.Clear(); appDomainCaught.Clear(); threadCaught.Clear();
        var ctrl = new ControlWindow();
        var ctrlHandle = ctrl.Handle;   // 强制建句柄
        ctrl.OnMessage = () => throw new ArgumentException("VisualTarget 的根 Visual 不能具有父级。");
        PostMessage(ctrlHandle, WM_APP_TEST, IntPtr.Zero, IntPtr.Zero);
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
        Note("用例6（WinForms Control WndProc 里直接抛）后：dispatcher={0} appdomain={1} thread={2}，WndProc 命中 {3} 次",
            dispatcherCaught.Count, appDomainCaught.Count, threadCaught.Count, ctrl.Hits);
        foreach (var ex in dispatcherCaught.Concat(appDomainCaught).Concat(threadCaught))
            Note("   捕获：{0}: {1}", ex.GetType().Name, ex.Message);

        /* ── 用例 7：Control WndProc 里调真实路径（ShowWindow）── */
        dispatcherCaught.Clear(); appDomainCaught.Clear(); threadCaught.Clear();
        ctrl.OnMessage = () => Services.ShowWindow();
        PostMessage(ctrlHandle, WM_APP_TEST, IntPtr.Zero, IntPtr.Zero);
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
        Note("用例7（WinForms Control WndProc → ShowWindow）后：dispatcher={0} appdomain={1} thread={2}，WndProc 命中 {3} 次",
            dispatcherCaught.Count, appDomainCaught.Count, threadCaught.Count, ctrl.Hits);
        foreach (var ex in dispatcherCaught.Concat(appDomainCaught).Concat(threadCaught))
            Note("   捕获：{0}: {1}", ex.GetType().Name, ex.Message);

        Dispatcher.CurrentDispatcher.UnhandledException -= OnDispatcher;
        AppDomain.CurrentDomain.UnhandledException -= OnAppDomain;
        System.Windows.Forms.Application.ThreadException -= OnThreadException;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("──── WndProc 通道探针 ────");
        foreach (var line in Report) sb.AppendLine(line);
        var text = sb.ToString();

        Log.Info(text);
        try { File.WriteAllText(Path.Combine(AppPaths.DataDir, "wndproc-probe.txt"), text); }
        catch { }
        Console.WriteLine(text);
        return 0;
    }
}
