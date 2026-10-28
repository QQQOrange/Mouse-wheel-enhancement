using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MouseWheelEnhancement.Core;

namespace MouseWheelEnhancement;

/// <summary>
/// `--wheeltest [lines=N]`：在**自己的窗口**里真的滚动，量出"一格到底滚了多少像素"。
///
/// <para>
/// 为什么需要它：设置页自己的页面是"按像素滚动"的（见
/// <see cref="App.OnPreviewMouseWheelScroll"/>），而 WPF 的 <c>ScrollViewer</c>
/// 无视 delta 大小、一个事件滚固定一档。这两套语义叠在一起时，
/// "系统每次滚动几行"这个值会同时改变两边 —— 光读代码看不出哪一边在起作用。
/// </para>
/// <para>
/// 这个命令把三条路径分别量出来（都用真实的 <c>SendInput</c>，光标真的挪到
/// 帮助页内容上）：
/// </para>
/// <list type="number">
/// <item>**外来签名** → 走完整链路：钩子 → 判定 → 平滑 → 回注入；</item>
/// <item>**自有签名** → 钩子放行，只剩 WPF + 我们自己的像素换算；</item>
/// <item>连续快滚 → 看倍率有没有兑现成位移。</item>
/// </list>
/// <para>
/// 每格之后读一次 <c>ScrollViewer.VerticalOffset</c>，所以报告里的 Δpx 就是
/// 用户眼睛里"这一格滚了多远"。
/// </para>
/// </summary>
internal static class WheelTest
{
    /// <summary>
    /// 跑一次实测。<paramref name="lines"/> &gt; 0 时先把系统行数设成它（测完不还原，
    /// 因为那本来就是用户自己的设置，没有"原值"可言）。
    /// </summary>
    public static int Run(MainWindow w, int lines)
    {
        var sb = new StringBuilder();
        var fail = 0;
        sb.AppendLine($"自身窗口滚动实测  v{Log.Version}   {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine(new string('-', 66));

        try
        {
            if (lines > 0)
            {
                SystemWheelLines.Set(lines);
                sb.AppendLine($"按参数把系统「每次滚动几行」设为 {lines}");
            }

            w.SelectTab(MainWindow.HelpTabIndex);
            w.UpdateLayout();
            Pump(300);

            var sv = w.HelpPanel?.Parent as ScrollViewer;
            if (sv == null)
            {
                sb.AppendLine("失败：帮助页的 HelpPanel 父级不是 ScrollViewer");
                return 2;
            }

            var systemLines = SystemWheelLines.Current();
            sb.AppendLine($"系统行数 = {systemLines}");
            sb.AppendLine($"帮助页视口 {sv.ActualWidth:0}×{sv.ActualHeight:0}，" +
                          $"ScrollableHeight {sv.ScrollableHeight:0}，" +
                          $"CanContentScroll={sv.CanContentScroll}");

            /* 预期值：一格应该滚多少像素（见 OnPreviewMouseWheelScroll 的算法）。 */
            var expect = systemLines > 0
                ? Native.WHEEL_DELTA * App.PixelsPerLine * systemLines / (double)Native.WHEEL_DELTA
                : 0;
            sb.AppendLine($"一格的理论位移 = {expect:0.##} px（{systemLines} 行 × {App.PixelsPerLine:0.#} px/行）");
            sb.AppendLine();

            /* 光标落到帮助页内容正中。窗口要置顶一次，否则坐标底下可能是别的程序。 */
            var p = sv.PointToScreen(new Point(sv.ActualWidth / 2, sv.ActualHeight / 2));
            if (!Native.SetCursorPos((int)p.X, (int)p.Y))
            {
                sb.AppendLine("失败：光标挪不过去（会话可能被锁）");
                return 3;
            }
            try { w.Activate(); w.Topmost = true; w.Topmost = false; } catch { /* 抢不到前台也继续 */ }
            Pump(300);

            var myHwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
            var under = Native.WindowFromPoint(new Native.POINT { X = (int)p.X, Y = (int)p.Y });
            var root = under == IntPtr.Zero ? IntPtr.Zero : Native.GetAncestor(under, Native.GA_ROOT);
            if (root != myHwnd)
            {
                sb.AppendLine("失败：光标位置被别的窗口占着（把本程序放到最前面再跑）");
                return 3;
            }

            /* 先把页面往下推一段，后面无论往上还是往下滚都有空间。 */
            for (var i = 0; i < 4; i++)
            {
                InputInjector.SendWheelWith(-Native.WHEEL_DELTA, ForeignSig);
                Pump(120);
            }
            Pump(300);

            App.WheelDiagOn = true;
            App.WheelDiagLog.Clear();

            /* 单格位移收集起来给最后的判定用 —— 用户感知的就是"这一格滚了多远"。 */
            var single = new List<double>();

            /* ── ① 外来签名：完整链路（钩子 → 判定 → 平滑 → 回注入） ── */
            sb.AppendLine("① 外来签名（走完整增强链路），慢滚一格一次：");
            foreach (var (up, n) in new[] { (true, 3), (false, 3) })
            {
                for (var i = 0; i < n; i++)
                {
                    var before = sv.VerticalOffset;
                    InputInjector.SendWheelWith(up ? Native.WHEEL_DELTA : -Native.WHEEL_DELTA, ForeignSig);
                    Pump(650);
                    var after = sv.VerticalOffset;
                    single.Add(after - before);
                    sb.AppendLine($"   {(up ? "向上" : "向下")} #{i + 1}：{before:0.##} → {after:0.##}" +
                                  $"（Δ{after - before:0.##} px）");
                }
            }

            /* ── ② 自有签名：钩子放行，只剩 WPF + 我们自己的像素换算 ── */
            sb.AppendLine();
            sb.AppendLine("② 自有签名（钩子不接管），慢滚一格一次：");
            for (var i = 0; i < 3; i++)
            {
                var before = sv.VerticalOffset;
                InputInjector.SendWheelWith(-Native.WHEEL_DELTA, InputInjector.Signature);
                Pump(650);
                var after = sv.VerticalOffset;
                single.Add(after - before);
                sb.AppendLine($"   向下 #{i + 1}：{before:0.##} → {after:0.##}（Δ{after - before:0.##} px）");
            }

            /* ── ③ 快滚：倍率应当兑现成位移 ── */
            sb.AppendLine();
            sb.AppendLine("③ 外来签名 + 连续快滚 8 格（45 ms 间隔）：");
            var b3 = sv.VerticalOffset;
            var clock = Stopwatch.StartNew();
            for (var i = 0; i < 8; i++)
            {
                InputInjector.SendWheelWith(-Native.WHEEL_DELTA, ForeignSig);
                var due = 45 * (i + 1);
                while (clock.ElapsedMilliseconds < due) Thread.Sleep(0);
            }
            Pump(900);                       // 等动画器把积压发完
            var after3 = sv.VerticalOffset;
            sb.AppendLine($"   8 格：{b3:0.##} → {after3:0.##}（Δ{after3 - b3:0.##} px），" +
                          $"倍率读数 {Services.Engine.LastMultiplier:0.00}×，" +
                          $"钩子接管 {Services.Engine.BoostedCount} 次");

            /* ── ④ 判定 ── */
            sb.AppendLine();
            sb.AppendLine(new string('-', 66));

            if (expect <= 0)
            {
                sb.AppendLine("⚠ 系统行数不是 1~10（读数是 -1/0），无法给出理论值 —— " +
                              "这一项本身就不该由软件换算");
            }
            else
            {
                /* 一格的实际位移应当接近理论值。超过 3 倍就说明有别的路径在滚。
                 * （慢滚时倍率 ≈1，所以 ①②两段都该落在理论值附近。） */
                var worst = single.Count == 0 ? 0 : single.Max(Math.Abs);
                var ok = worst > 0 && worst <= expect * 3.0 + 1;
                sb.AppendLine($"{(ok ? "✓" : "✗")} 单格最大位移 {worst:0.##} px，" +
                              $"理论 {expect:0.##} px（≤3 倍为正常，共 {single.Count} 次测量）");
                if (!ok) fail = 4;
            }

            var bypass = 0;
            foreach (var line in App.WheelDiagLog)
                if (line.Contains("bypass")) bypass++;
            sb.AppendLine($"{(bypass == 0 ? "✓" : "⚠")} 像素换算被绕过的次数：{bypass}" +
                          $"（>0 说明事件没有走 OnPreviewMouseWheelScroll）");

            sb.AppendLine();
            sb.AppendLine($"像素换算调用明细（共 {App.WheelDiagLog.Count} 条，最多列 40 条）：");
            var shown = 0;
            foreach (var line in App.WheelDiagLog)
            {
                if (shown++ >= 40) break;
                sb.AppendLine("   " + line);
            }

            App.WheelDiagOn = false;
        }
        catch (Exception ex)
        {
            sb.AppendLine("实测过程抛异常：" + ex);
            fail = 5;
        }

        sb.AppendLine();
        sb.AppendLine($"结论：{(fail == 0 ? "通过" : "失败（代码 " + fail + "）")}");

        var path = Path.Combine(AppPaths.DataDir, "wheeltest.txt");
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        Console.WriteLine(sb.ToString());
        return fail;
    }

    /// <summary>等待 <paramref name="ms"/> 毫秒，期间把消息泵干 —— 否则注入的滚轮事件没人处理。</summary>
    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (s, e) => { frame.Continue = false; timer.Stop(); };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    /// <summary>外来签名：钩子会当成"用户的真滚轮"处理（和 smoke 用的是同一个值）。</summary>
    private static readonly IntPtr ForeignSig = (IntPtr)0x5EE0;
}
