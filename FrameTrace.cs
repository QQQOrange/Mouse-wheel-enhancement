using System.Diagnostics;
using System.Text;
using MouseWheelEnhancement.Core;

namespace MouseWheelEnhancement;

/// <summary>
/// <c>--frametrace</c>：把平滑动画线程**真的**跑起来，量"空闲很久之后滚一下"
/// 的整条出帧序列 —— 重点是**第一帧**发了多少。
///
/// <para>
/// 为什么非要单独一个开关：<c>--selftest</c> 里那条"整段模拟"用的是**固定 dt**，
/// 它证明的是"算法本身没有到期倾泻的路径"。但"线程从长睡里醒来、第一帧的 dt 是多少"
/// 不是算法能管的 —— 那取决于主循环怎么记上一帧的时刻，而 bug 恰好长在这里：
/// 旧代码在**等之前**就把时间记成当时，线程一睡几分钟，醒来后 dt 被夹到 50 ms，
/// 第一帧就按 50 ms 的量发，单格滚动开头先跳出去大半格（实测 45 单位 ≈ 0.375 格）。
/// 固定 dt 的模拟永远碰不到这条，只有真的让线程睡一觉才看得见。
/// </para>
///
/// <para>
/// 走 <see cref="ScrollAnimator.FrameSink"/>：**不真的注入**。所以不需要窗口、
/// 不需要焦点，也不会把测试用的滚动打进用户正在用的应用里。
/// </para>
///
/// <para>
/// 断言（任一条不过 → 退出码非 0）：① 首帧不超过 8 单位；② 总量守恒；
/// ③ 单帧不超过 <c>UnitsPerFrame × 4</c>；④ 末尾不回升（曲线单调收尾）；
/// ④b 末帧间隔 ≤ 70 ms（最后一帧不拖）；④c 落地段 ≤ 150 ms（收尾不拖）；
/// ④d 中段步长起伏 ≤ 3.5×（不一顿一顿）；⑤ 能停下来（不残留积压）；
/// ⑥ 末 6 帧平均速度 ≤ 72 单位/秒。
/// </para>
/// </summary>
internal static class FrameTrace
{
    /// <summary>一帧的记录。<paramref name="AtMs"/> 是相对"按下滚轮"那一刻的毫秒数。</summary>
    private readonly record struct Frame(int Units, double AtMs, double DtMs, double TauMs);

    /// <summary>
    /// 首个**发出去**的帧允许的最大量。
    ///
    /// <para>
    /// 取整改成"零头累积"之后，第 1 个循环帧（dt≈0.2ms）通常什么都不发，
    /// 真正第一次发送发生在第 2 帧（约 6ms），发的是曲线本身那一步（一格约 19%，
    /// 也就是 20 出头）。所以这个上限从 8 放宽到 30（1/4 格）——
    /// 它现在只兜"一次性甩出大半格"这种明显异常；陈旧 dt 那个老 bug 由
    /// <see cref="MaxFirstDtMs"/> 单独钉住，那个判据更直接。
    /// </para>
    /// </summary>
    private const int MaxFirstFrameUnits = 30;

    /// <summary>首帧的 dt 上限（毫秒）。线程从长睡里醒来时，第一帧的 dt 不能是"睡了多久"。</summary>
    private const double MaxFirstDtMs = 20.0;

    /// <summary>
    /// 末 6 个发帧的平均速度上限（单位/秒）—— 只作粗兜底。
    ///
    /// <para>
    /// **v1.8.11 起口径变了。** 以前这一条是"结尾砸停"的主判据（72 单位/秒），
    /// 前提是收尾会一路爬到 25~35 单位/秒才停。但现在尾段一旦算出来
    /// "下一跳会慢到 >12ms"就直接收尾（见 <see cref="ScrollAnimator.TailGapMs"/>），
    /// 末尾速度自然落在 80~120 单位/秒 —— 那**不是砸停**，因为每一步只有 1 单位
    /// （0.8 px），肉眼看到的位移是亚像素级的。
    /// </para>
    /// <para>
    /// 真正该守的不变式换成了 <see cref="MaxTailStepUnits"/>：末段单步必须足够小。
    /// 这里只留 160 单位/秒（1.3 格/秒）作粗兜底，防止"整格整格地砸下来"。
    /// </para>
    /// </summary>
    private const double MaxTailRateUnits = 160.0;

    /// <summary>
    /// 末 6 个发帧的**最大单步**（单位）。
    ///
    /// <para>
    /// 2 单位 = 0.017 格 ≈ 1.6 px。收尾阶段只要每步都不超过它，
    /// "最后一帧"就不可能是肉眼可见的一跳 —— 这才是"不砸停"的本质，
    /// 而不是"末段速度必须很慢"（慢到 25 单位/秒的爬行会被读作"已经停了"，
    /// 反而产生"停住之后又挪了一下"）。
    /// </para>
    /// </summary>
    private const int MaxTailStepUnits = 2;

    /// <summary>
    /// 相邻两次发送之间的最大间隔（毫秒）。
    ///
    /// <para>
    /// 用户报的"每次滚完最后一下，真的有一种延迟"，形态就是收尾的最后 1~2 单位
    /// 拖到 80~94 ms 后才发 —— 像"页面已经停了，又偷偷补了一点"。70 ms 在
    /// 500 Hz（2 ms）到 60 Hz（16.7 ms）之间都足够宽松，但拦得住那种"最后一帧拖走"。
    /// </para>
    /// </summary>
    private const double MaxFrameGapMs = 70.0;

    /// <summary>
    /// 尾段允许主动丢弃的最大量（单位）。
    ///
    /// <para>
    /// v1.8.11 起，尾段一旦算出来"下一跳会慢到 >12ms（约 67 px/s 以下）"就直接收尾，
    /// 不再爬行 —— 那一段爬行眼睛早已读作"停了"，最后那一下就是用户报的
    /// "停住之后又挪了一下"。代价是每次少走几单位：8 单位 = 0.067 格 ≈ 4 px。
    /// </para>
    /// </summary>
    private const int MaxTailDrop = 8;

    /// <summary>走完 95% 之后到最后一帧之间最多再花多久（毫秒）——"落地段"的长度。</summary>
    private const double MaxLandMs = 150.0;

    /// <summary>落地段最多占整段收尾的比例（v1.8.8；收尾时长本身已按手速自适应）。</summary>
    private const double MaxLandShare = 0.62;

    /// <summary>
    /// 中段步长起伏上限（最大 / 中位）。
    ///
    /// <para>
    /// 帮助页按 δ=1/2/3 摊帧时每帧是 0.5 / 1.0 / 1.5 px，长时间卡在 1 单位
    /// 再突然来一发 3 单位，观感就是"刻度感 / 一顿一顿"。上限 3.5 允许
    /// "首帧大、后面小"的正常指数衰减，拦得住"1,1,1,1,3,1,1,1,1,3"那种台阶。
    /// </para>
    /// </summary>
    private const double MaxStepiness = 3.5;

    public static int Run(string[] args)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Mouse wheel enhancement 出帧追踪  v{Log.Version}");
        sb.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine(new string('-', 64));

        var fail = 0;

        /* 模拟慢机器的每帧附加延迟（毫秒）。0 = 不模拟。
         *
         * 用法：--frametrace jitter / --frametrace jitter=12
         *
         * 为什么需要它：掉帧降级（LowFpsDegrade）只在"出帧间隔持续超目标 2.5 倍"时
         * 才生效，正常机器上永远进不去 —— 也就永远验证不到它。在 FrameSink 里睡一下
         * 会让**下一次**循环的 dt 变长（_lastFrameTicks 在发送前就记好了），
         * 正好复现"这台机器跟不上"的状态。 */
        var jitterMs = 0.0;
        foreach (var a in args)
        {
            if (a.Equals("jitter", StringComparison.OrdinalIgnoreCase)) { jitterMs = 12; break; }
            if (a.StartsWith("jitter=", StringComparison.OrdinalIgnoreCase)
                && double.TryParse(a[7..], out var jv)) { jitterMs = Math.Clamp(jv, 0, 40); break; }
        }

        try
        {
            Services.InitConfig();   // 目录 + 日志 + 配置 + 刷新率（不装钩子、不建窗口）
            var s = RuntimeSettings.From(Services.Config);

            sb.AppendLine(
                $"设置：帧间隔 {s.FrameIntervalMs:0.##} ms（{(s.AutoFps ? "跟随显示器" : "手动")}）" +
                $"　单帧量 {s.UnitsPerFrame}　跟手 τ {s.TauMs:0} ms");
            sb.AppendLine(
                $"　　　滑行 {s.GlideMs:0} ms / 松手判定 {s.GlideDelayMs:0} ms" +
                $"　积压上限 {s.MaxBacklogUnits:0} 单位　平滑 {(s.SmoothEnabled ? "开" : "关")}");
            sb.AppendLine($"　　　单帧上限 {s.UnitsPerFrame * 4} 单位（单帧量 × 4）");
            if (jitterMs > 0) sb.AppendLine($"　　　⚠ 模拟慢帧：每帧额外 {jitterMs:0} ms");
            sb.AppendLine();

            if (!s.SmoothEnabled)
            {
                sb.AppendLine("平滑是关的，追不了帧序列。");
                return 3;
            }

            /* 空闲时长要明显长于"松手判定"，否则线程根本没进无限等待那条分支，
             * 也就复现不出陈旧 dt。 */
            var idle = (int)Math.Max(1200, s.GlideDelayMs + 900);

            fail += Case(sb, s, "空闲后滚一格", new[] { 120 }, gapMs: 0, idleMs: idle, jitterMs: jitterMs);
            fail += Case(sb, s, "空闲后往回滚一格", new[] { -120 }, gapMs: 0, idleMs: idle, jitterMs: jitterMs);
            fail += Case(sb, s, "空闲后连甩三格（15 ms 一下）", new[] { 120, 120, 120 }, gapMs: 15, idleMs: idle, jitterMs: jitterMs);
            fail += Case(sb, s, "空闲后连甩六格（8 ms 一下）", new[] { 120, 120, 120, 120, 120, 120 },
                gapMs: 8, idleMs: idle, jitterMs: jitterMs);

            sb.AppendLine(new string('-', 64));
            sb.AppendLine(fail == 0 ? "全部通过" : $"有 {fail} 项失败");
        }
        catch (Exception ex)
        {
            sb.AppendLine("✗ 追踪本身抛异常：" + ex);
            fail++;
        }

        var path = Path.Combine(AppPaths.DataDir, "frametrace.txt");
        try { File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false)); } catch { /* 写不进去也不影响结论 */ }
        try { Console.Out.Write(sb.ToString()); } catch { /* 无控制台（双击启动）就算了 */ }
        Log.Info("frametrace 结束，失败 " + fail + " 项，报告 " + path);

        return fail == 0 ? 0 : 1;
    }

    /// <summary>跑一个场景并打印报告，返回失败条数。</summary>
    /// <param name="pushes">每次推入的量（单位，1 格 = 120）；负数是反方向。</param>
    /// <param name="gapMs">两次推入之间间隔多久 —— 模拟"连甩"。</param>
    /// <param name="idleMs">推之前先让线程闲多久（必须足够长，让它真进无限等待）。</param>
    private static int Case(StringBuilder sb, RuntimeSettings s, string name,
        int[] pushes, double gapMs, int idleMs, double jitterMs = 0)
    {
        var total = pushes.Sum();
        sb.AppendLine($"■ {name}：共推入 {total:+#;-#;0} 单位（{Math.Abs(total) / 120.0:0.##} 格）");

        var frames = Capture(s, pushes, gapMs, idleMs, jitterMs, out var drainMs);
        if (frames.Count == 0)
        {
            sb.AppendLine("  ✗ 一帧都没发出来");
            sb.AppendLine();
            return 1;
        }

        var fail = 0;

        /* ① 首个发帧。这是整个开关存在的理由（旧代码一醒来就发 45 单位）。 */
        var first = Math.Abs(frames[0].Units);
        fail += Check(sb, $"首个发帧 {first} 单位（≤ {MaxFirstFrameUnits}）", first <= MaxFirstFrameUnits,
            $"首个发帧 {first} 单位，dt {frames[0].DtMs:0.##} ms");

        /* ①b 更直接的那条：首帧的 dt 不能是"线程睡了多久"。
         * 旧代码在等之前就记时间，睡几分钟后醒来 dt 被夹到 50ms，
         * 于是开头先跳大半格 —— 断言 dt 本身就够了，不用依赖取整方式。 */
        fail += Check(sb, $"首帧 dt {frames[0].DtMs:0.#} ms ≤ {MaxFirstDtMs:0} ms（不是陈旧 dt）",
            frames[0].DtMs <= MaxFirstDtMs, $"{frames[0].DtMs:0.##} ms");

        /* ② 总量守恒（允许尾段丢弃）：多一单位是"补一下"，少一单位是"没滚到位"。
         * v1.8.11 起尾段会主动丢掉最后不到 8 单位（见 MaxTailDrop）——
         * 那点距离换来的是"没有停住之后的孤立一跳"。 */
        var sent = frames.Sum(f => f.Units);
        var lost = sent - total;
        fail += Check(sb,
            $"总量守恒：发出 {sent:+#;-#;0} / 推入 {total:+#;-#;0}（尾段丢弃 {Math.Abs(lost)} ≤ {MaxTailDrop} 单位）",
            Math.Abs(lost) <= MaxTailDrop, $"差 {lost:+#;-#;0} 单位，超过允许的 {MaxTailDrop}");

        /* ③ 单帧上限。连甩时靠它保证"一帧不会跨大半格"。 */
        var cap = s.UnitsPerFrame * 4;
        var maxFrame = frames.Max(f => Math.Abs(f.Units));
        fail += Check(sb, $"单帧不超过 {cap} 单位（实测 {maxFrame}）", maxFrame <= cap,
            $"最大单帧 {maxFrame} 单位");

        /* ④ 末尾不回升：曲线收尾必须是单调的。这正是用户说的"最后一下刻度感" ——
         * 指数曲线已经降到 1 了，结尾突然再来一发大的。 */
        var tail = frames.Skip(Math.Max(0, frames.Count - 8)).Select(f => Math.Abs(f.Units)).ToArray();
        var rebound = 0;
        for (var i = 1; i < tail.Length; i++)
            rebound = Math.Max(rebound, tail[i] - tail[i - 1]);
        fail += Check(sb, $"末尾不回升（最大回升 {Math.Max(0, rebound)} 单位，≤ 1 算连续）", rebound <= 1,
            "末尾 " + string.Join(" ", tail));

        /* ④b **最后一帧别拖**：相邻两次发送的间隔不能越来越离谱。
         *
         * 用户原话："每次滚完最后一下，真的有一种延迟"。形态是收尾的最后 1~2 单位
         * 要等 80~94 ms 才发（v1.7.4 实测），像"页面已经停了，又偷偷补了一点"。
         * 阈值 70 ms：比最高刷新率（500 Hz = 2 ms）宽得多，但远小于人手能察觉的
         * "刚才好像还动了一下"。 */
        var gaps = new List<double>();
        for (var i = 1; i < frames.Count; i++)
            gaps.Add(frames[i].AtMs - frames[i - 1].AtMs);
        var maxGap = gaps.Count == 0 ? 0 : gaps.Max();
        fail += Check(sb, $"末帧间隔 {maxGap:0.#} ms（≤ {MaxFrameGapMs:0}，最后一帧不拖）",
            maxGap <= MaxFrameGapMs, $"最大间隔 {maxGap:0.##} ms");

        /* ④c **收尾别拖**：从"已经走完 95%"到"最后一帧"不该超过 150 ms。
         * 单格滚动 390 ms 里有 180 ms 在磨最后 5% —— 那一段用户读作"延迟"。 */
        var t95 = TimeToPercent(frames, 0.95);
        var landMs = frames.Count == 0 ? 0 : frames[^1].AtMs - t95;

        /* 落地段预算（v1.8.8 起改口径）。
         *
         * 收尾时长现在**按手速自适应**（快滚更长、慢滚更短，见 ScrollAnimator.StopTau），
         * 所以再拿"绝对 150ms"当上限，等于把设计意图判成失败。
         * 改成两条真正该守的不变式：
         *   ① 落地段不许吃掉整段收尾的大半（ratio）；
         *   ② 绝对上限 500ms（防"拖半秒以上"那种体验）。
         * 另配一条"按实际帧节奏缩放"的兜底，供极慢机器使用。 */
        gaps.Sort();
        var medianGap = gaps.Count > 0 ? gaps[gaps.Count / 2] : s.FrameIntervalMs;
        var landBudget = Math.Max(Math.Min(MaxLandMs + 350, drainMs * MaxLandShare), medianGap * 14);
        fail += Check(sb,
            $"走完 95% 后落地段 {landMs:0} ms（≤ {landBudget:0}，不超过整段 {MaxLandShare:P0}）",
            landMs <= landBudget,
            $"{landMs:0} ms > {landBudget:0} ms（整段 {drainMs:0} ms，中位帧隔 {medianGap:0.#} ms）");

        /* ④d **刻度感**：中段的步长不该大起大落。
         *
         * **窗口用进度（20%~80%）而不是时间**：用时间窗（t95*0.4 ~ last*0.9）
         * 会把整段衰减尾巴扫进来 —— 大部分 1 单位小步 + 一两个 16/31 单位的开头，
         * max/median 自然算到 5~6×，但那是**指数衰减的自然形态**，不是用户报的
         * "刻度感 / 一顿一顿"。用户报的是"长时间卡在 1 单位再突然来一发 3 单位"，
         * 应该看**中段**（20%~80% 进度）的步长，那里最大/中位实测 1.7~1.8×，
         * 阈值 3.5 允许首帧大、后面小的正常衰减。 */
        var totalUnits = frames.Sum(f => Math.Abs(f.Units));
        var acc = 0.0;
        var progUnits = new List<int>();
        foreach (var f in frames)
        {
            acc += Math.Abs(f.Units);
            if (acc >= totalUnits * 0.20 && acc <= totalUnits * 0.80)
                progUnits.Add(Math.Abs(f.Units));
        }
        progUnits.Sort();
        double stepiness = 1.0;
        if (progUnits.Count >= 3)
        {
            var median = progUnits[progUnits.Count / 2];
            stepiness = progUnits.Max() / Math.Max(1.0, median);
        }
        fail += Check(sb, $"中段步长起伏 {stepiness:0.0}×（≤ {MaxStepiness:0.#}，不一顿一顿）",
            stepiness <= MaxStepiness, $"最大/中位 = {stepiness:0.00}");

        /* ⑤ 收得住：不该留下尾巴一直慢慢爬。 */
        fail += Check(sb, $"能停下来（{drainMs:0} ms 内发完）", drainMs > 0 && drainMs < 4000,
            $"{drainMs:0} ms 后仍在发");

        /* ⑥ **收尾不砸停**：末 6 个发帧之间的平均速度。
         *
         * 注意必须用"相邻两次发送的时间差"算，不能用每帧的 dt —— dt 是循环的帧间隔
         * （固定 6ms），而零头累积之后相邻两次发送可能隔 3 帧，肉眼看到的是后者。 */
        var tail6 = frames.Skip(Math.Max(0, frames.Count - 6)).ToArray();
        var tailSpan = tail6.Length > 1 ? tail6[^1].AtMs - tail6[0].AtMs : 0;
        var tailRate = tailSpan > 0 ? tail6.Skip(1).Sum(f => Math.Abs(f.Units)) / (tailSpan / 1000.0) : 0;
        fail += Check(sb,
            $"末 6 帧平均速度 {tailRate:0} 单位/秒（≤ {MaxTailRateUnits:0}，粗兜底）",
            tailRate <= MaxTailRateUnits, $"{tailRate:0} 单位/秒");

        /* 只看**最后 3 个发帧**：尾段调度器接管的范围是"剩 ≤8 单位"，
         * 每帧固定 1 单位，所以最后 3 帧一定是 1 单位。
         * 再往前数会混进"按比例发"的帧（慢机器上一帧可能发 3~4 单位），那是正常的。 */
        var tail3 = frames.Skip(Math.Max(0, frames.Count - 3)).ToArray();
        var maxTailStep = tail3.Length == 0 ? 0 : tail3.Max(f => Math.Abs(f.Units));
        fail += Check(sb,
            $"最后 3 帧单步最大 {maxTailStep} 单位（≤ {MaxTailStepUnits}，最后一跳是亚像素）",
            maxTailStep <= MaxTailStepUnits, $"最大单步 {maxTailStep} 单位");

        /* ⑦ **末段 τ 不能骤降**：τ 是"这一帧敢发多少"的时间常数。
         * 它在末段突然变小，等于末段突然加速 —— 用户看到的就是"快到末尾时猛地跳一段"。
         * 允许缓慢下调（收尾上界会压 τ），但不允许一帧之内掉一半以上。 */
        var tailTau = frames.Skip(Math.Max(0, frames.Count - 8)).Select(f => f.TauMs).ToArray();
        var tauDrop = 1.0;
        var tauRise = 1.0;
        for (var i = 1; i < tailTau.Length; i++)
        {
            if (tailTau[i - 1] <= 0.5) continue;
            var ratio = tailTau[i] / tailTau[i - 1];
            tauDrop = Math.Min(tauDrop, ratio);
            tauRise = Math.Max(tauRise, ratio);
        }

        /* 两个方向都要拦：
         *   · 骤降 = 突然加速（末段猛跳一段）—— 旧版收尾上界一撤掉就是这个形态；
         *   · 骤升 = 突然减速（"卡了一下"）—— 掉帧降级整块跳过滑行时是这个形态。
         * 实测修好之后两个比值都是 1.00（τ 在末段恒定 35ms，靠 pending 自然衰减减速）。 */
        fail += Check(sb, $"末段 τ 不骤降（最小比值 {tauDrop:0.00}，≥ 0.55）", tauDrop >= 0.55,
            "τ 序列 " + string.Join(" → ", tailTau.Select(t => t.ToString("0.#"))));
        fail += Check(sb, $"末段 τ 不骤升（最大比值 {tauRise:0.00}，≤ 1.65）", tauRise <= 1.65,
            "τ 序列 " + string.Join(" → ", tailTau.Select(t => t.ToString("0.#"))));

        sb.AppendLine("  前 6 帧：" + Describe(frames.Take(6)));
        sb.AppendLine("  末 8 帧 τ：" + string.Join(" → ", tailTau.Select(t => t.ToString("0.#") + "ms")));
        sb.AppendLine("  末 6 帧：" + Describe(frames.Skip(Math.Max(0, frames.Count - 6))));
        sb.AppendLine($"  共 {frames.Count} 帧，用时 {drainMs:0} ms，最大单帧 {maxFrame} 单位，" +
                      $"末段速度 {tailRate:0} 单位/秒，落地段 {landMs:0} ms");
        sb.AppendLine();
        return fail;
    }

    /// <summary>累计位移到 <paramref name="percent"/> 的时刻（毫秒）。</summary>
    private static double TimeToPercent(List<Frame> frames, double percent)
    {
        if (frames.Count == 0) return 0;
        var total = frames.Sum(f => Math.Abs(f.Units));
        if (total <= 0) return 0;
        var want = total * percent;
        var acc = 0.0;
        foreach (var f in frames)
        {
            acc += Math.Abs(f.Units);
            if (acc >= want) return f.AtMs;
        }
        return frames[^1].AtMs;
    }

    /// <summary>
    /// 打印一小段帧序列。括号里是**距上一次发送**的间隔 ——
    /// 这才是肉眼看到的节奏；每帧的 dt 恒等于刷新周期，看不出收尾有没有放松。
    /// </summary>
    private static string Describe(IEnumerable<Frame> frames)
    {
        var list = frames.ToList();
        var prev = double.NaN;
        return string.Join("  ", list.Select(f =>
        {
            if (double.IsNaN(prev))
            {
                prev = f.AtMs;
                return $"{f.Units:+#;-#;0}(t {f.AtMs:0.#}ms)";
            }

            var gap = f.AtMs - prev;
            prev = f.AtMs;
            return $"{f.Units:+#;-#;0}(+{gap:0.#}ms)";
        }));
    }

    /// <summary>真开一条动画线程，推入后收集出帧，直到发完或超时。</summary>
    private static List<Frame> Capture(RuntimeSettings s, int[] pushes, double gapMs, int idleMs,
        double jitterMs, out double drainMs)
    {
        var frames = new List<Frame>();
        var gate = new object();
        var clock = new Stopwatch();
        var startAt = 0.0;

        using var animator = new ScrollAnimator();
        animator.UpdateSettings(s);
        animator.FrameSink = (units, dt, tau) =>
        {
            lock (gate)
                frames.Add(new Frame(units, clock.Elapsed.TotalMilliseconds - startAt, dt, tau));

            /* 模拟"这台机器跟不上"：睡在这里，下一次循环的 dt 就会被拉长
             * （_lastFrameTicks 在发送前已记好），从而踩到 LowFpsDegrade 的分支。 */
            if (jitterMs > 0) Thread.Sleep((int)jitterMs);
        };
        animator.Start();

        try
        {
            /* 关键一步：**先让它闲够**。线程在 pending == 0 时进 WaitOne(无限)，
             * 陈旧 dt 的 bug 只有在"睡了一觉"之后才会现形。 */
            Thread.Sleep(idleMs);

            clock.Start();
            startAt = clock.Elapsed.TotalMilliseconds;

            for (var i = 0; i < pushes.Length; i++)
            {
                animator.Push(pushes[i]);
                if (i < pushes.Length - 1 && gapMs > 0)
                {
                    /* 用绝对时间点 + Sleep(0) 让出，不用 Thread.Sleep(gapMs)：
                     * 后者受系统计时器粒度影响，8 ms 会变成 15~31 ms，
                     * "连甩"就不成其为连甩了。 */
                    var due = startAt + gapMs * (i + 1);
                    while (clock.Elapsed.TotalMilliseconds < due) Thread.Sleep(0);
                }
            }

            var deadline = Stopwatch.StartNew();
            while (deadline.ElapsedMilliseconds < 4000 && animator.PendingUnits != 0)
                Thread.Sleep(1);

            drainMs = clock.Elapsed.TotalMilliseconds;
        }
        finally
        {
            animator.FrameSink = null;
        }

        lock (gate) return new List<Frame>(frames);
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
}
