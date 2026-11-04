using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MouseWheelEnhancement.Core;

namespace MouseWheelEnhancement;

/// <summary>
/// <c>--scrollsmooth</c>：在**真实注入 + 真实渲染**的路径上量"逐帧位移"，
/// 专门抓"末尾卡一下、然后猛地跳一段"。
///
/// <para>
/// 和 <c>--frametrace</c> 的分工：
/// </para>
/// <list type="bullet">
/// <item><c>--frametrace</c> 走 <see cref="ScrollAnimator.FrameSink"/>，**不注入**，
/// 量的是"动画器打算发多少"。它能抓到曲线本身的不连续，但抓不到
/// 注入 → 目标程序 → 布局 这一路的问题。</item>
/// <item>本命令把光标真的挪到帮助页上、真的 <c>SendInput</c>，然后在
/// <c>CompositionTarget.Rendering</c> 里逐帧读 <c>ScrollViewer.VerticalOffset</c> ——
/// 量的是**用户眼睛里看到的位移**。</item>
/// </list>
///
/// <para>
/// 为什么必须分开：v1.8.5 修的三个原因（收尾上界硬开关、降级整块跳过、迟到帧补偿）
/// 都在动画器里，`--frametrace` 能验；但"末段是不是真的一帧一跳"最终取决于
/// 注入到 WPF 布局这一路，只有真滚才知道。
/// </para>
///
/// <para>
/// 判据（任一不过 → 退出码非 0）：
/// ① 有位移；② 单帧突刺 ≤ 中位步长的 <see cref="MaxSpikeRatio"/>；
/// ③ 末段不回升（后一步不得超过前一步的 1.6 倍）；④ 末段不拖（最后两次变化间隔 ≤ 80 ms）。
/// </para>
/// </summary>
internal static class ScrollSmooth
{
    /// <summary>外来签名：让钩子把它当成"用户"的滚轮，走完整增强链路。</summary>
    private const int ForeignSig = 0x00001234;

    /// <summary>单帧位移 / 中位位移的上限。超过就是"一帧跳一大段"。</summary>
    private const double MaxSpikeRatio = 3.5;

    /// <summary>末段相邻步长的最大放大倍数（"又窜一下"）。</summary>
    private const double MaxTailRise = 1.6;

    /// <summary>
    /// 末段回升判定的**绝对容差**（px）。
    ///
    /// <para>
    /// 收尾时单步会降到 0.5 px（半像素，亚像素滚动就是这个量级）。
    /// 0.5 → 1.0 按比值算是 2.0×，可实际只差 **0.5 像素** —— 人眼看不见，
    /// 那是像素量化噪声，不是"又窜一段"。所以比值判定要配一个绝对容差：
    /// 只有"既涨了 1.6 倍以上、又多走了 1.5 px 以上"才算回升。
    /// </para>
    /// </summary>
    private const double TailRiseAbsolutePx = 1.5;

    /// <summary>末段最后两次变化之间允许的最大间隔（"停住了又补一下"）。</summary>
    private const double MaxTailGapMs = 80;

    /// <summary>
    /// 收尾最后一步的间隔上限（v1.8.9）。
    ///
    /// <para>
    /// 80ms 那个阈值只拦"明显拖"，但用户报的"最后一下生硬"其实是**更小的空窗**：
    /// 实测末步间隔 57ms —— 画面先停住 57ms，然后补 0.5px 再结束。
    /// 人眼把前 40ms 读作"已经停了"，后面那一下就是"抽搐"。
    /// 40ms 之内（≈25fps）还能读作连续运动，所以末步必须落在这个窗口里。
    /// </para>
    /// </summary>
    private const double MaxFinalGapMs = 40;

    /// <summary>速度"抽搐"容忍度：相邻两步的速度比超过它就算一次回跳。</summary>
    private const double VelocitySpikeRatio = 1.30;

    /// <summary>算"末段速度"时，间隔短于它的步不参与（采样抖动会造出 0.5px/1ms 的假尖峰）。</summary>
    private const double MinSpeedGapMs = 4.0;

    /// <summary>算"末段速度"时，位移小于它的步不参与（亚像素步的比值没有物理意义）。</summary>
    private const double MinSpeedPx = 1.0;

    private readonly record struct Sample(double AtMs, double Offset);

    public static int Run(MainWindow w)
    {
        var sb = new StringBuilder();
        var fail = 0;

        sb.AppendLine($"逐帧滚动平滑度实测  v{Log.Version}   {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine(new string('-', 66));

        try
        {
            w.SelectTab(MainWindow.HelpTabIndex);
            w.UpdateLayout();
            Pump(300);

            var sv = w.HelpScroll;
            if (sv == null)
            {
                sb.AppendLine("失败：帮助页里找不到滚动容器");
                return 2;
            }

            sb.AppendLine($"帮助页视口 {sv.ActualWidth:0}×{sv.ActualHeight:0}，" +
                          $"ScrollableHeight {sv.ScrollableHeight:0}");

            var p = sv.PointToScreen(new Point(sv.ActualWidth / 2, sv.ActualHeight / 2));
            if (!Native.SetCursorPos((int)p.X, (int)p.Y))
            {
                sb.AppendLine("失败：光标挪不过去（会话可能被锁）");
                return 3;
            }
            try { w.Activate(); w.Topmost = true; w.Topmost = false; } catch { }
            Pump(300);

            var myHwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;

            /* 前台/光标被抢是很常见的（用户正在用别的程序、UAC 桌面、远程桌面…）。
             * 以前一次判不过就直接 rc=3 退出，等于"我在忙就测不了"。
             * 现在重试 6 次、每次把窗口再顶到最前；真的抢不到才退出。 */
            var root = IntPtr.Zero;
            for (var attempt = 0; attempt < 6; attempt++)
            {
                Native.SetCursorPos((int)p.X, (int)p.Y);
                try { w.Activate(); w.Topmost = true; w.Topmost = false; } catch { }
                Pump(180);

                var under = Native.WindowFromPoint(new Native.POINT { X = (int)p.X, Y = (int)p.Y });
                root = under == IntPtr.Zero ? IntPtr.Zero : Native.GetAncestor(under, Native.GA_ROOT);
                if (root == myHwnd) break;

                Pump(320);
            }

            if (root != myHwnd)
            {
                sb.AppendLine("失败：光标位置被别的窗口占着（把本程序放到最前面再跑）");
                return 3;
            }

            /* 先滚到中段：末尾才有滑行空间，也避开"一上来就在底部"的假象。 */
            for (var i = 0; i < 5; i++)
            {
                InputInjector.SendWheelWith(-Native.WHEEL_DELTA, ForeignSig);
                Pump(60);
            }
            Pump(600);

            /* 基准：单格通过同一条链路走一次，量出这台机器上"一格到底多少像素"。
             * 用它做总数判据，就不会被"行高不是常量"坑（PixelsPerLine 只是估算值）。 */
            var baseBefore = sv.VerticalOffset;
            InputInjector.SendWheelWith(-Native.WHEEL_DELTA, ForeignSig);
            Pump(700);
            var oneNotch = Math.Abs(sv.VerticalOffset - baseBefore);
            if (oneNotch < 1) oneNotch = 0;      // 量不出来就不做总量判据

            var samples = Capture(sv, out var injections);
            sb.AppendLine($"注入 {injections} 格，采样 {samples.Count} 帧");
            sb.AppendLine();

            if (samples.Count < 4)
            {
                sb.AppendLine("✗ 采样太少（窗口可能被遮挡或没有渲染）");
                return 4;
            }

            /* 只保留"真的变了"的样本：渲染帧率高于实际位移频率，
             * 大量重复值会把中位数拉到 0。 */
            var steps = new List<(double AtMs, double Px)>();
            for (var i = 1; i < samples.Count; i++)
            {
                var d = samples[i].Offset - samples[i - 1].Offset;
                if (Math.Abs(d) < 0.01) continue;
                steps.Add((samples[i].AtMs, Math.Abs(d)));
            }

            if (steps.Count < 4)
            {
                sb.AppendLine($"✗ 位移步数太少（{steps.Count} 步），测不出来");
                return 5;
            }

            var total = steps.Sum(s => s.Px);
            sb.AppendLine($"总位移 {total:0.#} px，共 {steps.Count} 步" +
                          $"（{total / injections:0.#} px/格）");
            sb.AppendLine();

            /* ① 有位移 */
            fail += Check(sb, $"有位移（{total:0.#} px）", total > 1, "几乎没有滚动");
            if (fail > 0) { Finish(sb, fail); return fail; }

            /* ①' 总量对不对（v1.8.29）：用**基准单格**折算，而不是用 PixelsPerLine 猜。
             *
             * 之前出现过一次"8 格滚了 1235px（154/格）而不是 705px（88/格）"的异常，
             * 说明某一步被投递了两次。曲线上看不出这种错，只有对总量才抓得到。 */
            if (oneNotch > 0)
            {
                var expected = oneNotch * injections;
                var drift = Math.Abs(total - expected) / Math.Max(1, expected);
                fail += Check(sb,
                    $"总量 {total:0.#} px（基准 {oneNotch:0.#}×{injections} 格 = {expected:0.#}，偏差 {drift:P0} ≤ 25%）",
                    drift <= 0.25,
                    $"疑似重复投递：{injections} 格实际滚了 {total:0.#} px");
            }
            else
            {
                sb.AppendLine("（单格基准没量出来，跳过总量判据）");
            }

            /* ② 单帧突刺：跳过前 2 步（起手本来就大），比中位步长。
             *    这就是"猛地跳一段"的量化。 */
            var body = steps.Skip(2).Select(s => s.Px).ToList();
            var sorted = body.OrderBy(v => v).ToList();
            var median = sorted[sorted.Count / 2];
            var maxStep = body.Max();
            var spike = maxStep / Math.Max(0.5, median);
            fail += Check(sb,
                $"单帧突刺 {spike:0.0}×（≤ {MaxSpikeRatio:0.#}，最大 {maxStep:0.#} / 中位 {median:0.#} px）",
                spike <= MaxSpikeRatio, $"最大单帧 {maxStep:0.#} px 是中位的 {spike:0.0} 倍");

            /* ②' 中段"脉动深度"（v1.8.26）：取中间 50% 的步长，看最大/最小。
             *
             * 触控板的速度是连续的，脉动深度接近 1；旧版"每档一个尖峰"能到 3 以上
             * （档来的一瞬间一大步，档间掉成小步）。速度域低通就是冲这个来的。 */
            var midFrom = body.Count / 4;
            var midTo = body.Count - Math.Max(2, body.Count / 4);
            var mid = body.Skip(midFrom).Take(Math.Max(3, midTo - midFrom)).ToList();
            var pulse = mid.Count == 0 ? 1.0 : mid.Max() / Math.Max(0.5, mid.Min());
            fail += Check(sb, $"中段脉动 {pulse:0.0}×（≤ 2.6，越小越像触控板）",
                pulse <= 2.6, mid.Count == 0 ? "样本不足" : $"最大 {mid.Max():0.#} / 最小 {mid.Min():0.#} px");

            /* ③ 末段不回升：最后 8 步里，后一步不该比前一步大太多。
             *    回升 = "已经在减速了，突然又窜一下"。 */
            var tail = steps.Skip(Math.Max(0, steps.Count - 8)).ToList();
            var worstRise = 0.0;
            var worstDiff = 0.0;
            for (var i = 1; i < tail.Count; i++)
            {
                var prevStep = Math.Max(0.5, tail[i - 1].Px);
                /* 只有"倍数超了 **且** 绝对量也超了"才算回升 ——
                 * 半像素级的 0.5→1 是像素量化，不是又窜一下。 */
                if (tail[i].Px > prevStep * MaxTailRise && tail[i].Px - prevStep > TailRiseAbsolutePx)
                {
                    worstRise = Math.Max(worstRise, tail[i].Px / prevStep);
                    worstDiff = Math.Max(worstDiff, tail[i].Px - prevStep);
                }
            }
            fail += Check(sb,
                $"末段不回升（超阈值 {worstRise:0.00}×{TailRiseAbsolutePx:0.#}px 以上，≤ {MaxTailRise:0.#}×）",
                worstRise == 0,
                $"末段有一跳比上一步大 {worstRise:0.00}×（多 {worstDiff:0.#} px）：" +
                string.Join(" ", tail.Select(t => t.Px.ToString("0.#"))));

            /* ④ 末段不拖：最后两次变化的间隔。 */
            var tailGap = tail.Count >= 2 ? tail[^1].AtMs - tail[^2].AtMs : 0;
            fail += Check(sb, $"末段最后一步间隔 {tailGap:0.#} ms（≤ {MaxTailGapMs:0}）",
                tailGap <= MaxTailGapMs, $"最后一步隔了 {tailGap:0.#} ms 才补上");

            /* ⑤ **末步空窗**：停住太久再补一下 = "最后一下生硬"（v1.8.9）。
             * 40ms ≈ 25fps，超过它人眼就把前一段读作"已经停了"。 */
            fail += Check(sb, $"末步空窗 {tailGap:0.#} ms（≤ {MaxFinalGapMs:0}，不在停住之后再补一下）",
                tailGap <= MaxFinalGapMs, $"最后一步隔了 {tailGap:0.#} ms，读作停住后又补了一下");

            /* ⑥ **速度不抽搐**：末段每一步的瞬时速度（px/ms）不能回跳。
             *
             * 位移是 0.5px 量级、间隔是 5~25ms 量级，两者都受整数单位与渲染帧的量化，
             * 单看任一项都会误判；只有"速度"是用户眼睛真正读到的量。
             * 回跳 = 已经在减速了又忽然快一下 —— 那就是"生硬"的量化形态。 */
            var velSpikes = 0;
            var worstVel = 1.0;
            for (var i = 1; i < tail.Count; i++)
            {
                var d0 = tail[i - 1].AtMs;
                var d1 = tail[i].AtMs;
                var prevGap = i >= 2 ? tail[i - 1].AtMs - tail[i - 2].AtMs : 0;
                var gap = d1 - d0;

                /* 采样抖动过滤：渲染帧之间可能只隔 1~3ms（采样器自己在抢 CPU），
                 * 而位移是 0.5px 的整数倍 —— 0.5px / 1ms 会被算成 0.5 px/ms 的假尖峰。
                 * 这种"速度"不是用户看到的运动，是测量噪声：
                 *   ① 间隔 < 4ms 的步不参与；
                 *   ② 位移不到 1px 的步不参与（亚像素步的比值没有意义）。 */
                if (prevGap < MinSpeedGapMs || gap < MinSpeedGapMs) continue;
                if (tail[i].Px < MinSpeedPx || tail[i - 1].Px < MinSpeedPx) continue;
                if (d1 - d0 <= 0) continue;

                var vPrev = tail[i - 1].Px / prevGap;
                var vNow = tail[i].Px / gap;
                if (vNow > vPrev * VelocitySpikeRatio)
                {
                    velSpikes++;
                    worstVel = Math.Max(worstVel, vNow / Math.Max(1e-6, vPrev));
                }
            }
            fail += Check(sb, $"末段速度不回跳（抽搐 {velSpikes} 次，最多允许 1 次）",
                velSpikes <= 1, $"末段有 {velSpikes} 次速度回跳，最大 {worstVel:0.0}×");

            var vTail = new List<string>();
            for (var i = 1; i < tail.Count; i++)
            {
                var g = tail[i].AtMs - tail[i - 1].AtMs;
                if (g > 0) vTail.Add($"{tail[i].Px / g:0.000}");
            }
            sb.AppendLine("  末段速度(px/ms)：" + string.Join(" → ", vTail));

            sb.AppendLine();
            sb.AppendLine("  前 10 步：" + Describe(steps.Take(10)));
            sb.AppendLine("  末 10 步：" + Describe(steps.Skip(Math.Max(0, steps.Count - 10))));
            sb.AppendLine($"  中位 {median:0.#} px / 最大 {maxStep:0.#} px / 共 {steps.Count} 步");
        }
        catch (Exception ex)
        {
            sb.AppendLine("✗ 实测本身抛异常：" + ex.Message);
            fail++;
        }

        Finish(sb, fail);
        return fail;
    }

    private static void Finish(StringBuilder sb, int fail)
    {
        sb.AppendLine(new string('-', 66));
        sb.AppendLine(fail == 0 ? "结论：全部通过" : $"结论：有 {fail} 项未通过");

        var path = Path.Combine(AppPaths.DataDir, "scrollsmooth.txt");
        try { File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false)); } catch { }
        try { Console.Out.Write(sb.ToString()); } catch { }

        Log.Info("scrollsmooth 结束，失败 " + fail + " 项，报告 " + path);
    }

    /// <summary>
    /// 挂上逐帧采样，然后打一段快滚（8 格 @45 ms —— 触发加速与滑行），
    /// 一直采到位移停住或超时。
    /// </summary>
    private static List<Sample> Capture(ScrollViewer sv, out int injections)
    {
        var list = new List<Sample>();
        var clock = Stopwatch.StartNew();

        EventHandler onFrame = (_, _) =>
        {
            /* 这个回调在**渲染帧**上触发（160 Hz 就是 6.25 ms 一次）。
             * 读的是"上一轮布局完成后的 offset"，恒定滞后一帧 ——
             * 对逐帧步长的比值判断没有影响。 */
            list.Add(new Sample(clock.Elapsed.TotalMilliseconds, sv.VerticalOffset));
        };

        System.Windows.Media.CompositionTarget.Rendering += onFrame;
        try
        {
            const int notches = 8;
            const int gapMs = 45;
            var deadline = Stopwatch.StartNew();
            var n = 0;

            while (n < notches && deadline.ElapsedMilliseconds < 3000)
            {
                InputInjector.SendWheelWith(-Native.WHEEL_DELTA, ForeignSig);
                n++;
                Pump(gapMs);
            }
            injections = n;

            /* 采到"连续 250 ms 没有变化"为止 —— 全程包含滑行收尾。 */
            var lastChange = clock.Elapsed.TotalMilliseconds;
            var lastOffset = sv.VerticalOffset;
            var idleFrom = clock.Elapsed.TotalMilliseconds;
            while (clock.Elapsed.TotalMilliseconds - idleFrom < 250
                   && clock.Elapsed.TotalMilliseconds < 6000)
            {
                Pump(50);
                var now = sv.VerticalOffset;
                if (Math.Abs(now - lastOffset) > 0.01)
                {
                    lastOffset = now;
                    lastChange = clock.Elapsed.TotalMilliseconds;
                    idleFrom = clock.Elapsed.TotalMilliseconds;
                }
            }
            _ = lastChange;
        }
        finally
        {
            System.Windows.Media.CompositionTarget.Rendering -= onFrame;
        }

        return list;
    }

    private static string Describe(IEnumerable<(double AtMs, double Px)> steps)
    {
        var prev = double.NaN;
        return string.Join("  ", steps.Select(s =>
        {
            var gap = double.IsNaN(prev) ? 0 : s.AtMs - prev;
            prev = s.AtMs;
            return $"{s.Px:0.#}px{(gap > 0 ? $"(+{gap:0}ms)" : "")}";
        }));
    }

    private static int Check(StringBuilder sb, string what, bool ok, string detail)
    {
        if (ok)
        {
            sb.AppendLine("  ✓ " + what);
            return 0;
        }
        sb.AppendLine("  ✗ " + what + "　—— " + detail);
        return 1;
    }

    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (s, e) => { frame.Continue = false; timer.Stop(); };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
