using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using MouseWheelEnhancement.Core;

namespace MouseWheelEnhancement;

/// <summary>
/// 第四代探针：驱动**真实的** <c>NotifyIcon</c>。
///
/// <para>
/// 前三代探针都没能复现异常本身，但已经把"异常从哪条通道逃出去"查清了
/// （WinForms 控件的 WndProc → <c>Application.ThreadException</c>，
/// 而我们从没订阅过它 → 框架默认弹窗 → 日志无 ERROR）。
/// </para>
///
/// <para>
/// 这一代不再模拟，而是**用真的 NotifyIcon**：反射拿到它内部那个窗口的句柄，
/// 往里投真鼠标消息（WM_LBUTTONDOWN / WM_LBUTTONUP），
/// 于是走的是和用户手点托盘图标**完全同一条**代码路径。
/// </para>
/// </summary>
internal static class TrayProbe
{
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDBLCLK = 0x0203;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private static readonly List<string> Report = new();
    private static void Note(string s) => Report.Add(s);
    private static void Note(string fmt, params object?[] args) => Report.Add(string.Format(fmt, args));

    private static void Pump(DispatcherPriority p = DispatcherPriority.Background)
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(p, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    /// <summary>在 NotifyIcon 内部找到那个承载鼠标消息的窗口。</summary>
    private static IntPtr FindTrayWindow(object notifyIcon)
    {
        for (var t = notifyIcon.GetType(); t != null; t = t.BaseType)
        {
            foreach (var f in t.GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public))
            {
                if (!typeof(System.Windows.Forms.NativeWindow).IsAssignableFrom(f.FieldType)) continue;
                try
                {
                    if (f.GetValue(notifyIcon) is System.Windows.Forms.NativeWindow nw && nw.Handle != IntPtr.Zero)
                        return nw.Handle;
                }
                catch { }
            }
        }
        return IntPtr.Zero;
    }

    public static int Run()
    {
        Report.Clear();

        var caught = new List<(string, Exception)>();
        Dispatcher.CurrentDispatcher.UnhandledException += (s, e) =>
        { caught.Add(("dispatcher", e.Exception)); e.Handled = true; };
        System.Windows.Forms.Application.ThreadException += (s, e) =>
            caught.Add(("winforms", e.Exception));
        System.Windows.Forms.Application.SetUnhandledExceptionMode(
            System.Windows.Forms.UnhandledExceptionMode.CatchException);
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        { if (e.ExceptionObject is Exception ex) caught.Add(("appdomain", ex)); };

        Services.Init();

        var w = new MainWindow();
        w.Show();
        w.UpdateLayout();
        Services.Window = w;
        Pump(DispatcherPriority.Background);

        var icon = Services.Tray;
        if (icon == null) { Note("没有托盘图标"); return 1; }

        // 反射拿到 NotifyIcon 实例
        var field = typeof(TrayController).GetField("_icon", BindingFlags.NonPublic | BindingFlags.Instance);
        var notifyIcon = field?.GetValue(icon);
        if (notifyIcon == null) { Note("拿不到 NotifyIcon 实例"); return 1; }

        var hwnd = FindTrayWindow(notifyIcon);
        Note("NotifyIcon 内部窗口句柄：0x{0:X}", hwnd.ToInt64());
        if (hwnd == IntPtr.Zero)
        {
            // 退一步：把 NotifyIcon 的私有字段都列出来，便于后续调整
            for (var t = notifyIcon.GetType(); t != null; t = t.BaseType)
            {
                foreach (var f in t.GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
                    Note("   字段 {0}.{1} : {2}", t.Name, f.Name, f.FieldType.Name);
            }
            return 1;
        }

        const int lParam = 0x000A000A;   // 客户区坐标 (10,10)

        void Click()
        {
            SendMessage(hwnd, WM_MOUSEMOVE, IntPtr.Zero, (IntPtr)lParam);
            SendMessage(hwnd, WM_LBUTTONDOWN, (IntPtr)1, (IntPtr)lParam);
            SendMessage(hwnd, WM_LBUTTONUP, IntPtr.Zero, (IntPtr)lParam);
            Pump(DispatcherPriority.Background);
        }

        /* ── 场景 1：单击一次 ── */
        caught.Clear();
        Click();
        Note("场景1（单击托盘）异常：{0}", caught.Count);
        foreach (var (ch, ex) in caught)
        {
            Note("   [{0}] {1}: {2}", ch, ex.GetType().Name, ex.Message);
            Note("   堆栈：{0}", string.Join(" | ",
                (ex.StackTrace ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(6)));
        }

        /* ── 场景 2：双击（MouseClick×2 + DoubleClick ⇒ ShowWindow 三连发）── */
        caught.Clear();
        SendMessage(hwnd, WM_LBUTTONDOWN, (IntPtr)1, (IntPtr)lParam);
        SendMessage(hwnd, WM_LBUTTONUP, IntPtr.Zero, (IntPtr)lParam);
        SendMessage(hwnd, WM_LBUTTONDBLCLK, (IntPtr)1, (IntPtr)lParam);
        SendMessage(hwnd, WM_LBUTTONUP, IntPtr.Zero, (IntPtr)lParam);
        Pump(DispatcherPriority.Background);
        Note("场景2（双击托盘）异常：{0}", caught.Count);
        foreach (var (ch, ex) in caught)
        {
            Note("   [{0}] {1}: {2}", ch, ex.GetType().Name, ex.Message);
            Note("   堆栈：{0}", string.Join(" | ",
                (ex.StackTrace ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(6)));
        }

        /* ── 场景 3：窗口开着时再点托盘 ── */
        caught.Clear();
        Click();
        Note("场景3（窗口开着时点托盘）异常：{0}", caught.Count);
        foreach (var (ch, ex) in caught)
        {
            Note("   [{0}] {1}: {2}", ch, ex.GetType().Name, ex.Message);
            Note("   堆栈：{0}", string.Join(" | ",
                (ex.StackTrace ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(6)));
        }

        /* ── 场景 4：窗口开着 + ContextMenu 开着时点托盘 ── */
        caught.Clear();
        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = w.ThemeBtn };
        menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "探针项" });
        menu.IsOpen = true;
        Pump(DispatcherPriority.Background);
        Click();
        Note("场景4（菜单开着时点托盘）异常：{0}", caught.Count);
        foreach (var (ch, ex) in caught)
        {
            Note("   [{0}] {1}: {2}", ch, ex.GetType().Name, ex.Message);
            Note("   堆栈：{0}", string.Join(" | ",
                (ex.StackTrace ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(6)));
        }
        try { menu.IsOpen = false; } catch { }

        /* ── 场景 5：菜单开着 + 窗口隐藏时点托盘 ── */
        caught.Clear();
        var menu2 = new System.Windows.Controls.ContextMenu { PlacementTarget = w.ThemeBtn };
        menu2.Items.Add(new System.Windows.Controls.MenuItem { Header = "探针项" });
        menu2.IsOpen = true;
        Pump(DispatcherPriority.Background);
        w.Hide();
        Pump(DispatcherPriority.Background);
        Click();
        Note("场景5（菜单开着+窗口隐藏时点托盘）异常：{0}", caught.Count);
        foreach (var (ch, ex) in caught)
        {
            Note("   [{0}] {1}: {2}", ch, ex.GetType().Name, ex.Message);
            Note("   堆栈：{0}", string.Join(" | ",
                (ex.StackTrace ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(6)));
        }
        try { menu2.IsOpen = false; } catch { }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("──── 真实托盘点击探针 ────");
        foreach (var line in Report) sb.AppendLine(line);
        var text = sb.ToString();
        Log.Info(text);
        try { File.WriteAllText(Path.Combine(AppPaths.DataDir, "tray-probe.txt"), text); }
        catch { }
        Console.WriteLine(text);
        return 0;
    }
}
