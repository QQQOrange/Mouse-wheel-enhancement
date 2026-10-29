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

            /* 帮助页的滚动容器。v1.7.6 在 HelpPanel 上面插了一层 StackPanel
             * （放了「快速定位」卡片），所以不能再写 `HelpPanel.Parent as ScrollViewer` ——
             * 直接用它自己的名字，层级再变也不会断。 */
            var sv = w.HelpScroll;
            if (sv == null)
            {
                sb.AppendLine("失败：帮助页里找不到滚动容器");
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

            /* ── ③ 快滚：倍率应当兑现成位移 ──
             *
             * 这一段量的是用户真正关心的事："我拨得越快，页面是不是真的滚得越多"。
             * 按几档常见手速各打一段 8 格，位移除以「8 × 单格位移」就是**兑现倍率**；
             * 再和钩子读到的倍率对照 —— 两者差得远，说明量在动画器里被截断了
             * （v1.6.0 的"加速失效"、v1.7.3 修的回归，都是这个症状）。 */
            sb.AppendLine();
            sb.AppendLine("③ 不同手速下的倍率兑现（每段 8 格，往下滚）：");
            var baseline = single.Count == 0 ? 0 : single.Max(Math.Abs);
            var delivered = new List<double>();
            foreach (var gap in new[] { 150, 100, 70, 45 })
            {
                ScrollToTop(sv, baseline);
                var before3 = sv.VerticalOffset;
                var d = RunBurst(sv, 8, gap);
                var ratio = baseline > 0 ? d / (8 * baseline) : 0;
                delivered.Add(ratio);
                sb.AppendLine($"   {gap,3} ms/格（{1000.0 / gap,4:0.#} 格/秒）：" +
                              $"{before3:0.##} → {sv.VerticalOffset:0.##}（Δ{d:0.##} px）" +
                              $" → 兑现 {ratio:0.00}×，钩子读数 {Services.Engine.LastMultiplier:0.00}×");
            }

            /* ③b 持续猛甩：16 格 @35 ms。这段专治"积压上限截断"——
             *     需求是 16 × 满倍率，动画器每秒最多倒 60 格/秒（修前），
             *     超出的部分被静默丢掉，表现为"读着 5×、只快一两成"。 */
            ScrollToTop(sv, baseline);
            var beforeS = sv.VerticalOffset;
            var dS = RunBurst(sv, 16, 35);
            var ratioS = baseline > 0 ? dS / (16 * baseline) : 0;
            sb.AppendLine($"   持续猛甩 16 格 @35 ms：Δ{dS:0.##} px → 兑现 {ratioS:0.00}×" +
                          $"，钩子读数 {Services.Engine.LastMultiplier:0.00}×");

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

            /* ④ 兑现判定：倍率必须变成位移。
             *    · 随速度单调不减 —— 慢滚不加速、快滚明显更快；
             *    · 最快档 ≥ 1.5×、持续猛甩 ≥ 2.0× —— 被积压上限截断的老毛病
             *      （v1.6.0 / v1.7.3 回归）在这个档位上现形：修前分别只有
             *      1.68× 和 1.8×，而配置里写的是 5×。 */
            var monoOk = true;
            for (var i = 1; i < delivered.Count; i++)
                if (delivered[i] < delivered[i - 1] - 0.05) monoOk = false;
            sb.AppendLine($"{(monoOk ? "✓" : "✗")} 兑现倍率随速度单调不减" +
                          $"（{string.Join(" → ", delivered.ConvertAll(r => $"{r:0.00}×"))}）");
            if (!monoOk) fail = 6;

            var fastOk = delivered.Count == 0 || delivered[^1] >= 1.5;
            sb.AppendLine($"{(fastOk ? "✓" : "✗")} 最快档（45 ms/格）兑现 {delivered[^1]:0.00}×" +
                          $"（≥1.5×；不足说明量被动画器截断）");
            if (!fastOk) fail = 7;

            var sustainOk = ratioS >= 2.0;
            sb.AppendLine($"{(sustainOk ? "✓" : "✗")} 持续猛甩兑现 {ratioS:0.00}×" +
                          $"（≥2.0×；修前只有 ~1.8×，就是「加速失效」）");
            if (!sustainOk) fail = 8;

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

    /// <summary>等待 <paramref="ms"/> 毫秒，期间把消息泵干 —— 否则注入的滚轮事件没人处理。</summary>
    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (s, e) => { frame.Continue = false; timer.Stop(); };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    /// <summary>
    /// 打一段快滚：<paramref="notches"/> 格、每格间隔 <paramref="intervalMs"/> 毫秒，
    /// 返回这段的位移（px）。等 900 ms 让动画器把积压发完再量。
    /// </summary>
    private static double RunBurst(ScrollViewer sv, int notches, int intervalMs)
    {
        var before = sv.VerticalOffset;
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < notches; i++)
        {
            InputInjector.SendWheelWith(-Native.WHEEL_DELTA, ForeignSig);
            var due = intervalMs * (i + 1);
            while (clock.ElapsedMilliseconds < due) Thread.Sleep(0);
        }
        Pump(900);
        return sv.VerticalOffset - before;
    }

    /// <summary>
    /// 把帮助页滚回顶（用自有签名注入，钩子不接管、直接按像素滚，一步一格）。
    /// 每档手速测试前都调一次，保证每段都有足够的滚动空间。
    /// </summary>
    private static void ScrollToTop(ScrollViewer sv, double pxPerNotch)
    {
        if (sv.VerticalOffset <= 0.5) return;
        var need = (int)Math.Ceiling(sv.VerticalOffset / Math.Max(1.0, pxPerNotch)) + 2;
        for (var i = 0; i < need; i++)
            InputInjector.SendWheelWith(Native.WHEEL_DELTA, InputInjector.Signature);
        Pump(400);
    }

    /// <summary>外来签名：钩子会当成"用户的真滚轮"处理（和 smoke 用的是同一个值）。</summary>
    private static readonly IntPtr ForeignSig = (IntPtr)0x5EE0;
}
