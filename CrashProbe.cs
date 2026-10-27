using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MouseWheelEnhancement.Core;

namespace MouseWheelEnhancement;

/// <summary>
/// 崩溃取证探针：把"托盘点击 → 打开窗口"这条链路上每个可能出事的动作
/// 单独拎出来跑一遍，定位到底哪一步抛
/// <c>ArgumentException: VisualTarget 的根 Visual 不能具有父级</c>。
///
/// <para>
/// 为什么要单独写这个：崩溃发生在 WinForms 的 <c>NotifyIcon</c> 回调里 ——
/// 那条路径**既不走** WPF 的 <c>DispatcherUnhandledException</c>，
/// **也不走** <c>AppDomain.UnhandledException</c>（WinForms 的
/// <c>Application.ThreadException</c> 会先把它吃掉），所以日志里一条 ERROR 都没有，
/// 用户只看到框架默认弹窗。没有堆栈就只能靠"把每一步都单独试一遍"。
/// </para>
///
/// <para>
/// 每个用例都跑在 <c>DispatcherPriority.Background</c> 优先级的独立帧里，
/// 目的是让布局、Popup 的 HwndSource 真正建起来 —— 很多这类异常
/// 只在**布局真正跑过**之后才显形。
/// </para>
/// </summary>
internal static class CrashProbe
{
    private static readonly List<string> Report = new();

    private static void Note(string caseName, Exception? ex)
    {
        if (ex == null)
        {
            Report.Add($"[ 正常 ] {caseName}");
            return;
        }
        Report.Add($"[ 抛出 ] {caseName} → {ex.GetType().Name}: {ex.Message}");
        Report.Add($"         堆栈：{FirstFrames(ex)}");
    }

    /// <summary>只取最上面几帧 —— 探针输出要能塞进一行日志。</summary>
    private static string FirstFrames(Exception ex)
    {
        var lines = (ex.StackTrace ?? "").Split('\n');
        var take = lines.Length <= 4 ? lines : lines[..4];
        return string.Join(" | ", lines.Select(l => l.Trim()).Where(l => l.Length > 0).Take(4));
    }

    /// <summary>推一帧消息循环，让布局 / Popup / 动画真正跑起来。</summary>
    private static void Pump(DispatcherPriority priority = DispatcherPriority.Background)
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(priority, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    /// <summary>在一个受控的帧里执行 <paramref name="action"/>，把抛出的异常收回来。</summary>
    private static void Run(string caseName, Action action)
    {
        Exception? caught = null;
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Normal, new Action(() =>
        {
            try { action(); }
            catch (Exception ex) { caught = ex; }
        }));
        Pump(DispatcherPriority.Background);
        Note(caseName, caught);
    }

    public static int Run()
    {
        Report.Clear();

        Services.Init();

        var w = new MainWindow();
        w.Show();
        w.UpdateLayout();
        Pump(DispatcherPriority.Background);

        // 探针要走的正是"托盘点击"那条路：Services.Window 有值 → 走 else 分支
        // → Show() + Activate() + Topmost 翻转。
        Services.Window = w;

        /* ── 用例 A：已显示窗口，直接 Topmost 翻转 ── */
        Run("A 已显示窗口 Topmost 翻转", () =>
        {
            w.Topmost = true;
            w.Topmost = false;
        });

        /* ── 用例 B：开着的 ContextMenu（=「从运行中的应用选择」那个菜单）＋ Topmost 翻转 ──
         *
         * ContextMenu 底层是 Popup，Popup 有自己的 HwndSource。
         * 窗口的 z 序一变，Popup 会被要求重新定位 —— 这一步最容易踩到
         * "根 Visual 不能具有父级"。 */
        Run("B 开着 ContextMenu 时 Topmost 翻转", () =>
        {
            var menu = new ContextMenu { PlacementTarget = w.ThemeBtn };
            menu.Items.Add(new MenuItem { Header = "探针项" });
            menu.IsOpen = true;
            Pump(DispatcherPriority.Background);

            w.Topmost = true;
            w.Topmost = false;
            Pump(DispatcherPriority.Background);

            menu.IsOpen = false;
        });

        /* ── 用例 C：开着的 ToolTip（底层也是 Popup）＋ Topmost 翻转 ── */
        Run("C 开着 ToolTip 时 Topmost 翻转", () =>
        {
            var tip = new System.Windows.Controls.ToolTip
            {
                Content = "探针提示",
                PlacementTarget = w.ThemeBtn,
            };
            tip.IsOpen = true;
            Pump(DispatcherPriority.Background);

            w.Topmost = true;
            w.Topmost = false;
            Pump(DispatcherPriority.Background);

            tip.IsOpen = false;
        });

        /* ── 用例 D：隐藏 → 再显示 → Topmost 翻转（托盘点两下的常见序列）── */
        Run("D Hide 后 Show 再 Topmost 翻转", () =>
        {
            w.Hide();
            Pump(DispatcherPriority.Background);
            w.Show();
            Pump(DispatcherPriority.Background);
            w.Topmost = true;
            w.Topmost = false;
        });

        /* ── 用例 E：开着 ContextMenu 时隐藏窗口（Popup 成了孤儿）── */
        Run("E 开着 ContextMenu 时 Hide 窗口", () =>
        {
            var menu = new ContextMenu { PlacementTarget = w.ThemeBtn };
            menu.Items.Add(new MenuItem { Header = "探针项" });
            menu.IsOpen = true;
            Pump(DispatcherPriority.Background);

            w.Hide();
            Pump(DispatcherPriority.Background);

            menu.IsOpen = false;
        });

        /* ── 用例 F：开着 ContextMenu 时替换调色板字典（切主题）── */
        Run("F 开着 ContextMenu 时切主题", () =>
        {
            var menu = new ContextMenu { PlacementTarget = w.ThemeBtn };
            menu.Items.Add(new MenuItem { Header = "探针项" });
            menu.IsOpen = true;
            Pump(DispatcherPriority.Background);

            Core.Theme.SetMode(Core.Theme.IsLightNow ? Core.Theme.Dark : Core.Theme.Light);
            Pump(DispatcherPriority.Background);

            menu.IsOpen = false;
        });

        /* ── 用例 G：重入 —— Show() 期间再次调 ShowMainWindow ──
         *
         * 托盘双击会连发 MouseClick×2 + DoubleClick，也就是 ShowMainWindow 被调三次。
         * 若其中一次正好落在另一次 Show() 的中间，就是"根 Visual 已有父级"。 */
        Run("G Show 期间重入 ShowMainWindow", () =>
        {
            var app = (App)Application.Current;
            app.ShowMainWindow();     // 已显示 → else 分支
            app.ShowMainWindow();
            app.ShowMainWindow();
        });

        /* ── 用例 H：窗口已销毁后再 ShowMainWindow（Services.Window 置空重建）── */
        Run("H 关闭后重建窗口再 ShowMainWindow", () =>
        {
            w.Hide();
            w.Close();
            Pump(DispatcherPriority.Background);

            Services.Window = null;
            var app = (App)Application.Current;
            app.ShowMainWindow();
            Pump(DispatcherPriority.Background);
        });

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("──── 崩溃探针报告 ────");
        foreach (var line in Report) sb.AppendLine(line);
        var text = sb.ToString();

        Log.Info(text);
        try
        {
            File.WriteAllText(Path.Combine(AppPaths.DataDir, "crash-probe.txt"), text);
        }
        catch { /* 取证失败不影响结论 */ }

        Console.WriteLine(text);
        return 0;
    }
}
