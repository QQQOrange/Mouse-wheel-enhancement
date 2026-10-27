using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MouseWheelEnhancement.Core;

namespace MouseWheelEnhancement;

/// <summary>
/// 第三代探针：用**真实鼠标**复现用户的操作序列。
///
/// <para>
/// 前两代探针在 WPF 干净上下文里跑，8 个用例全过；WndProc 通道探针查明了
/// 异常的逃逸路径（WinForms ThreadException），但没能复现异常本身。
/// 剩下的差别是"真实用户动作"：鼠标**悬停**会让 WPF 的 ToolTipService
/// 弹出 ToolTip（它底层是 Popup，有自己的 HwndSource），
/// 而纯代码调用不会产生这个状态。
/// </para>
///
/// <para>
/// 所以这一代用 <c>SendInput</c> 把真鼠标挪到按钮上，等 ToolTip 真的弹出来，
/// 然后在 ToolTip 开着的时候执行各种扰动操作 —— 这才是用户实际会遇到的情况。
/// </para>
/// </summary>
internal static class HoverProbe
{
    private static readonly List<string> Report = new();
    private static void Note(string s) => Report.Add(s);
    private static void Note(string fmt, params object?[] args) => Report.Add(string.Format(fmt, args));

    private static void Pump(DispatcherPriority p = DispatcherPriority.Background)
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(p, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private const int INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct INPUTUNION
    {
        public MOUSEINPUT mi;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public INPUTUNION u;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint n, INPUT[] p, int size);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT p);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    /// <summary>
    /// 把真鼠标挪到某个元素中心（触发 ToolTipService 的悬停检测）。
    /// 绝对坐标必须按**虚拟桌面**归一化，否则会被按主显示器解释。
    /// </summary>
    private static bool Hover(FrameworkElement? target)
    {
        if (target == null) return false;
        var pt = target.PointToScreen(new Point(target.ActualWidth / 2, target.ActualHeight / 2));
        return MoveMouseTo((int)Math.Round(pt.X), (int)Math.Round(pt.Y));
    }

    private static bool MoveMouseTo(int x, int y)
    {
        // 分几步挪过去：ToolTipService 要的是**连续**的移动轨迹，
        // 一步跳过去有时不算"进入"控件。
        for (var step = 1; step <= 5; step++)
        {
            var sx = GetCursorX();
            var sy = GetCursorY();
            MoveMouseToRaw(sx + (x - sx) * step / 5, sy + (y - sy) * step / 5);
            System.Threading.Thread.Sleep(40);
        }
        return true;
    }

    private static int GetCursorX()
    {
        GetCursorPos(out var p);
        return p.X;
    }

    private static int GetCursorY()
    {
        GetCursorPos(out var p);
        return p.Y;
    }

    private static bool MoveMouseToRaw(int x, int y)
    {
        var vw = Native.GetSystemMetrics(78);   // SM_XVIRTUALSCREEN
        var vh = Native.GetSystemMetrics(79);   // SM_YVIRTUALSCREEN
        var vx = Native.GetSystemMetrics(76);   // SM_XVIRTUALSCREEN
        var vy = Native.GetSystemMetrics(77);   // SM_YVIRTUALSCREEN
        if (vw <= 0 || vh <= 0) return false;

        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new INPUTUNION
            {
                mi = new MOUSEINPUT
                {
                    dx = (int)(((double)(x - vx)) * 65535.0 / (vw - 1)),
                    dy = (int)(((double)(y - vy)) * 65535.0 / (vh - 1)),
                    dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                },
            },
        };
        return SendInput(1, new[] { input }, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>()) == 1;
    }

    private static void ClickCurrent()
    {
        var down = new INPUT
        {
            type = INPUT_MOUSE,
            u = new INPUTUNION { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTDOWN } },
        };
        var up = new INPUT
        {
            type = INPUT_MOUSE,
            u = new INPUTUNION { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTUP } },
        };
        var size = System.Runtime.InteropServices.Marshal.SizeOf<INPUT>();
        SendInput(1, new[] { down }, size);
        System.Threading.Thread.Sleep(30);
        SendInput(1, new[] { up }, size);
        Pump(DispatcherPriority.Background);
    }

    /// <summary>
    /// 把 ToolTipService 的"当前正在显示的提示"内部状态强行置位。
    ///
    /// <para>
    /// 合成鼠标移动不会被 ToolTipService 认（它要真实 WM_MOUSEMOVE 轨迹），
    /// 所以没法靠"把鼠标挪过去"来制造"提示正开着"这个状态。
    /// 而这个状态恰恰是关键：<c>ThemeBtn_Click</c> 里有一行
    /// <c>ThemeBtn.ToolTip = "..."</c>，它执行的**那一刻**提示气泡很可能正开着
    /// （用户就是悬停在按钮上点的它）。重新赋值 ToolTip 属性会让
    /// ToolTipService 去换掉正在显示的那一个 —— 而 ToolTip 底层是 Popup，
    /// 换的时候要动 HwndSource 的根 Visual。
    /// </para>
    /// </summary>
    private static bool ForceToolTipShown(FrameworkElement owner, string text)
    {
        try
        {
            var svcType = typeof(System.Windows.Controls.ToolTipService);
            var curField = svcType.GetField("_currentToolTip",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            if (curField == null)
            {
                // .NET 10 可能改了名字 —— 把候选字段列出来，便于后续调整
                var names = svcType.GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                    .Select(f => f.Name).ToArray();
                Note("   ToolTipService 里没有 _currentToolTip，现有静态字段：{0}", string.Join(", ", names));
                return false;
            }

            var tip = new System.Windows.Controls.ToolTip
            {
                Content = text,
                PlacementTarget = owner,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse,
            };
            tip.IsOpen = true;
            curField.SetValue(null, tip);
            Note("   已强制置位 ToolTipService._currentToolTip，IsOpen={0}", tip.IsOpen);
            return true;
        }
        catch (Exception ex)
        {
            Note("   强制置位失败：{0}: {1}", ex.GetType().Name, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 当前进程里活跃的 HwndSource 数量。
    ///
    /// <para>
    /// ToolTip / ContextMenu 底层都是 <c>Popup</c>，而 Popup 会**自建一个 HwndSource**
    /// （自己一个窗口）。所以"这个数突然 +1"就等于"有个弹层真的弹出来了"——
    /// 这比去翻内部字段可靠得多，也不受版本改名影响。
    /// </para>
    /// </summary>
    private static int PopupSourceCount() =>
        PresentationSource.CurrentSources.Cast<PresentationSource>().Count();

    private static int CursorX()
    {
        GetCursorPos(out var p);
        return p.X;
    }

    /// <summary>
    /// 读当前正在显示的 ToolTip。
    /// <c>ToolTip.Current</c> 是 WPF 的**内部** API，公开面没有 ——
    /// 但它是唯一能确认"提示气泡真的弹出来了"的办法，
    /// 而"弹出来了"正是本探针要制造的前提条件。
    /// </summary>
    private static System.Windows.Controls.ToolTip? CurrentToolTip()
    {
        try
        {
            var prop = typeof(System.Windows.Controls.ToolTip)
                .GetProperty("Current",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            return prop?.GetValue(null) as System.Windows.Controls.ToolTip;
        }
        catch { return null; }
    }

    public static int Run()
    {
        Report.Clear();

        // 接住所有通道，任何异常都要留下痕迹
        var caught = new List<(string, Exception)>();
        Dispatcher.CurrentDispatcher.UnhandledException += (s, e) =>
        {
            caught.Add(("dispatcher", e.Exception)); e.Handled = true;
        };
        System.Windows.Forms.Application.ThreadException += (s, e) =>
            caught.Add(("winforms", e.Exception));
        System.Windows.Forms.Application.SetUnhandledExceptionMode(
            System.Windows.Forms.UnhandledExceptionMode.CatchException);
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            if (e.ExceptionObject is Exception ex) caught.Add(("appdomain", ex));
        };

        Services.Init();

        var w = new MainWindow();
        w.Show();
        w.UpdateLayout();
        Services.Window = w;
        Pump(DispatcherPriority.Background);

        // 悬停到"主题"按钮上 —— 它带 ToolTip
        var srcBefore = PopupSourceCount();
        var themeBtn = w.ThemeBtn;
        Note("主题按钮：{0}", themeBtn == null ? "(null)" : themeBtn.Name);

        bool hovered = Hover(themeBtn);
        Note("鼠标挪到主题按钮上：{0}", hovered);

        // 等 ToolTipService 的悬停延时（默认 500ms）
        for (var i = 0; i < 12; i++) { Pump(DispatcherPriority.Background); Thread.Sleep(60); }

        var tip = CurrentToolTip();
        Note("ToolTip 状态：Current={0} IsOpen={1}", tip == null ? "(null)" : "有", tip?.IsOpen);
        Note("HwndSource 数量：{0}（悬停前 {1}）", PopupSourceCount(), srcBefore);

        /* ── 扰动 1：提示正开着时点主题按钮（会重新赋值 ThemeBtn.ToolTip）──
         *
         * 这是目前最像用户现场的一组：用户把鼠标停在"主题"按钮上，
         * 提示气泡弹出来了，然后他点了按钮 —— 代码里紧跟着就把
         * ThemeBtn.ToolTip 换成了新字符串。 */
        caught.Clear();
        var forced = ForceToolTipShown(themeBtn!, "当前浅色 · 点击切换");
        Note("扰动1 前提：提示强制置位={0}", forced);
        for (var i = 0; i < 6; i++) { Pump(DispatcherPriority.Background); Thread.Sleep(50); }
        try
        {
            themeBtn!.ToolTip = "当前深色 · 点击切换";
        }
        catch (Exception ex) { Note("   直接抛出：{0}: {1}", ex.GetType().Name, ex.Message); }
        for (var i = 0; i < 8; i++) { Pump(DispatcherPriority.Background); Thread.Sleep(50); }
        Note("扰动1（提示开着时改 ToolTip 属性）异常：{0}", caught.Count);
        foreach (var (ch, ex) in caught)
            Note("   [{0}] {1}: {2}", ch, ex.GetType().Name, ex.Message);

        /* ── 扰动 2：提示开着时切主题（换调色板字典 → 整棵视觉树重解析）── */
        caught.Clear();
        ForceToolTipShown(themeBtn!, "当前深色 · 点击切换");
        for (var i = 0; i < 6; i++) { Pump(DispatcherPriority.Background); Thread.Sleep(50); }
        try { Theme.SetMode(Theme.IsLightNow ? Theme.Dark : Theme.Light); }
        catch (Exception ex) { Note("   直接抛出：{0}: {1}", ex.GetType().Name, ex.Message); }
        for (var i = 0; i < 8; i++) { Pump(DispatcherPriority.Background); Thread.Sleep(50); }
        Note("扰动2（提示开着时切主题）异常：{0}", caught.Count);
        foreach (var (ch, ex) in caught)
            Note("   [{0}] {1}: {2}", ch, ex.GetType().Name, ex.Message);

        /* ── 扰动 3：提示开着时调 ShowWindow（= 托盘点击的真实路径）── */
        caught.Clear();
        ForceToolTipShown(themeBtn!, "当前深色 · 点击切换");
        for (var i = 0; i < 6; i++) { Pump(DispatcherPriority.Background); Thread.Sleep(50); }
        try { Services.ShowWindow(); }
        catch (Exception ex) { Note("   直接抛出：{0}: {1}", ex.GetType().Name, ex.Message); }
        for (var i = 0; i < 8; i++) { Pump(DispatcherPriority.Background); Thread.Sleep(50); }
        Note("扰动3（提示开着时 ShowWindow）异常：{0}", caught.Count);
        foreach (var (ch, ex) in caught)
            Note("   [{0}] {1}: {2}", ch, ex.GetType().Name, ex.Message);

        /* ── 扰动 4：提示开着时开 ContextMenu（两个 Popup 叠在一起）── */
        caught.Clear();
        ForceToolTipShown(themeBtn!, "当前深色 · 点击切换");
        for (var i = 0; i < 6; i++) { Pump(DispatcherPriority.Background); Thread.Sleep(50); }
        try
        {
            var menu = new ContextMenu { PlacementTarget = themeBtn };
            menu.Items.Add(new MenuItem { Header = "探针项" });
            menu.IsOpen = true;
            for (var i = 0; i < 6; i++) { Pump(DispatcherPriority.Background); Thread.Sleep(50); }
            menu.IsOpen = false;
        }
        catch (Exception ex) { Note("   直接抛出：{0}: {1}", ex.GetType().Name, ex.Message); }
        for (var i = 0; i < 8; i++) { Pump(DispatcherPriority.Background); Thread.Sleep(50); }
        Note("扰动4（提示开着时开 ContextMenu）异常：{0}", caught.Count);
        foreach (var (ch, ex) in caught)
            Note("   [{0}] {1}: {2}", ch, ex.GetType().Name, ex.Message);

        /* ── 扰动 5：提示开着时切页面（TabControl 换内容 → 布局全变）── */
        caught.Clear();
        ForceToolTipShown(themeBtn!, "当前深色 · 点击切换");
        for (var i = 0; i < 6; i++) { Pump(DispatcherPriority.Background); Thread.Sleep(50); }
        try { w.SelectTab(4); }
        catch (Exception ex) { Note("   直接抛出：{0}: {1}", ex.GetType().Name, ex.Message); }
        for (var i = 0; i < 8; i++) { Pump(DispatcherPriority.Background); Thread.Sleep(50); }
        Note("扰动5（提示开着时切页面）异常：{0}", caught.Count);
        foreach (var (ch, ex) in caught)
            Note("   [{0}] {1}: {2}", ch, ex.GetType().Name, ex.Message);

        /* ── 扰动 6：提示开着时隐藏窗口（= 点 × 收回托盘）── */
        caught.Clear();
        ForceToolTipShown(themeBtn!, "当前深色 · 点击切换");
        for (var i = 0; i < 6; i++) { Pump(DispatcherPriority.Background); Thread.Sleep(50); }
        try { w.Hide(); }
        catch (Exception ex) { Note("   直接抛出：{0}: {1}", ex.GetType().Name, ex.Message); }
        for (var i = 0; i < 8; i++) { Pump(DispatcherPriority.Background); Thread.Sleep(50); }
        Note("扰动6（提示开着时隐藏窗口）异常：{0}", caught.Count);
        foreach (var (ch, ex) in caught)
            Note("   [{0}] {1}: {2}", ex.GetType().Name, ex.Message);

        try { w.Show(); } catch { }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("──── 悬停探针报告 ────");
        foreach (var line in Report) sb.AppendLine(line);
        var text = sb.ToString();
        Log.Info(text);
        try { File.WriteAllText(Path.Combine(AppPaths.DataDir, "hover-probe.txt"), text); }
        catch { }
        Console.WriteLine(text);
        return 0;
    }
}
