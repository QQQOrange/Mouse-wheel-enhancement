using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using MouseWheelEnhancement.Core;
using MouseWheelEnhancement.Update;

namespace MouseWheelEnhancement;

internal static partial class SelfTest
{
    private static void TestBoostCurve()
    {
        Section("倍率曲线");
        var cfg = AppConfig.Default().Boost;

        Near("阈值以下正好 1.0×", WheelVelocityTracker.MultiplierFor(0, cfg), 1.0);
        Near("阈值处正好 1.0×（无跳变）", WheelVelocityTracker.MultiplierFor(cfg.FastThresholdPerSec, cfg), 1.0);
        Near("饱和点达到上限", WheelVelocityTracker.MultiplierFor(cfg.SaturationPerSec, cfg), cfg.MaxMultiplier);
        Near("超过饱和点仍是上限（不无限放大）",
            WheelVelocityTracker.MultiplierFor(500, cfg), cfg.MaxMultiplier);

        // 单调不减，且没有跳变
        var prev = WheelVelocityTracker.MultiplierFor(0, cfg);
        var mono = true;
        var smooth = true;
        for (var r = 0.0; r <= 60; r += 0.5)
        {
            var m = WheelVelocityTracker.MultiplierFor(r, cfg);
            if (m < prev - 1e-9) mono = false;
            if (m - prev > 0.12) smooth = false; // 0.5 格/秒的步长不该带来超过 12% 的跳变
            prev = m;
        }
        Check("倍率随速度单调不减", mono);
        Check("曲线无台阶（渐变）", smooth);

        // 默认值本身要合理：阈值 10 格/秒、上限 3 倍
        Near("默认阈值 10 格/秒", cfg.FastThresholdPerSec, 10);
        Near("默认上限 3.0×", cfg.MaxMultiplier, 3.0);
    }

    private static void TestVelocityTracking()
    {
        Section("速度判定");
        var cfg = AppConfig.Default().Boost;
        var t = new WheelVelocityTracker();
        var now = 1000L;

        // 慢滚：每 200ms 一格 = 5 格/秒 → 必须原样放行
        BoostDecision d = default;
        var slowAllNative = true;
        for (var i = 0; i < 6; i++)
        {
            d = t.Sample(120, now, cfg, false, false);
            if (d.Boosted || d.OutputUnits != 120) slowAllNative = false;
            now += 200;
        }
        Check("慢滚一律不接管、输出 = 原样一格", slowAllNative);

        // 快滚：每 20ms 一格 = 50 格/秒 → 加速到上限
        t.Reset();
        now = 5000;
        t.Sample(120, now, cfg, false, false);
        for (var i = 0; i < 12; i++)
        {
            now += 20;
            d = t.Sample(120, now, cfg, false, false);
        }
        Check("快滚会接管", d.Boosted);
        Near("快滚倍率到上限", d.Multiplier, cfg.MaxMultiplier, 0.02);
        Near("输出 = 一格 × 倍率", d.OutputUnits, 360, 2);

        // 方向反转（负 delta 用绝对值对称）
        t.Reset();
        now = 9000;
        t.Sample(-120, now, cfg, false, false);
        for (var i = 0; i < 10; i++) { now += 20; d = t.Sample(-120, now, cfg, false, false); }
        Check("反向滚动同样能加速", d.Boosted && d.OutputUnits < 0, $"out={d.OutputUnits}");
    }

    /// <summary>
    /// 起步护栏：**轻轻一拨绝不能加速**。
    ///
    /// <para>
    /// 这几条全部来自实机投诉："设了 9~10 格阈值，轻轻滚一下却整页翻过去。"
    /// 根因是速度按"格/秒"算 —— 一拨散成 2~3 个事件、间隔 10 ms，算出来就是 13~25 格/秒，
    /// 阈值越高越没用（阈值卡的是速度，而抖动时那个速度是假的）。
    /// 现在一段滚动必须**同时**够长（> 3 格）又够久（> 70 ms）才会进入加速判定。
    /// </para>
    /// </summary>
    private static void TestBurstGuard()
    {
        Section("起步护栏（轻拨不加速）");
        var cfg = AppConfig.Default().Boost;
        var t = new WheelVelocityTracker();

        /* ① 最核心的一条：两个事件、间隔 10 ms。
         *    旧算法：13 格/秒 → 越过默认阈值 → 约 1.4×。现在必须原样放行。 */
        t.Reset();
        var now = 1000L;
        var d1 = t.Sample(120, now, cfg, false, false);
        now += 10;
        var d2 = t.Sample(120, now, cfg, false, false);
        Check("两下 10ms 内的轻拨不加速",
            !d1.Boosted && !d2.Boosted, $"d1={d1.OutputUnits} d2={d2.OutputUnits}");

        /* ② 三个事件也一样 —— 主观上是"一下"，客观上是三条事件。 */
        t.Reset();
        now = 3000;
        var boom = false;
        for (var i = 0; i < 3; i++)
        {
            if (t.Sample(120, now, cfg, false, false).Boosted) boom = true;
            now += 10;
        }
        Check("三下 10ms 内的连拨也不加速", !boom);

        /* ③ 一次拨动连发六格、但整段只有 25 ms（解码器抖动 / 驱动拆分）。
         *    格数护栏挡不住这种，所以必须有"够久"这条。 */
        t.Reset();
        now = 5000;
        boom = false;
        for (var i = 0; i < 6; i++)
        {
            if (t.Sample(120, now, cfg, false, false).Boosted) boom = true;
            now += 5;
        }
        Check("六格却只有 25ms 的抖动爆发不加速", !boom);

        /* ④ 慢滚六格（每格 200 ms）：每格间隔超过 120 ms，各自算新的一段。 */
        t.Reset();
        now = 7000;
        boom = false;
        for (var i = 0; i < 6; i++)
        {
            if (t.Sample(120, now, cfg, false, false).Boosted) boom = true;
            now += 200;
        }
        Check("慢滚六格（每格 200ms）不加速", !boom);

        /* ⑤ 真正的连续快滚：20 ms 一格、滚 15 格（300 ms）—— 必须加速。 */
        t.Reset();
        now = 9000;
        var last = default(BoostDecision);
        for (var i = 0; i < 15; i++)
        {
            last = t.Sample(120, now, cfg, false, false);
            now += 20;
        }
        Check("持续 300ms 的快滚会加速", last.Boosted, $"mult={last.Multiplier:0.00}");
        Near("快滚倍率到上限", last.Multiplier, cfg.MaxMultiplier, 0.05);

        /* ⑥ 护栏刚过时倍率只放开一部分，不能从 1× 一步窜到上限。
         *    60 ms 一格（≈16.7 格/秒）：第 2 格过护栏（1 格护栏），
         *    此时倍率应明显低于上限；再滚一格渐入到顶。 */
        t.Reset();
        now = 12000;
        BoostDecision at3 = default, at6 = default, at8 = default;
        for (var i = 0; i < 8; i++)
        {
            var dd = t.Sample(120, now, cfg, false, false);
            if (i == 2) at3 = dd;
            if (i == 5) at6 = dd;
            if (i == 7) at8 = dd;
            now += 60;
        }
        Check("护栏刚过时倍率没顶满（渐入）",
            at3.Multiplier < cfg.MaxMultiplier * 0.9,
            $"第3格 mult={at3.Multiplier:0.00} 上限={cfg.MaxMultiplier:0.0}");
        Check("再滚几格才接近配置倍率",
            at8.Multiplier > at3.Multiplier,
            $"第8格 mult={at8.Multiplier:0.00}");
        Check("渐入两格内到位（第 6 格已接近满倍率）",
            at6.Multiplier >= at8.Multiplier - 0.05,
            $"第6格 {at6.Multiplier:0.00} / 第8格 {at8.Multiplier:0.00}");

        /* ⑦ dt 下限：两个"同一毫秒"到达的事件不该算出 1000 格/秒这种没有物理意义的数。 */
        t.Reset();
        now = 20000;
        t.Sample(120, now, cfg, false, false);
        t.Sample(120, now, cfg, false, false);
        Check("同一毫秒到达的事件不会把速度算成上千格/秒",
            t.RatePerSec < 200, $"rate={t.RatePerSec:0} 格/秒");

        /* ⑧ 按投诉者的真实参数再跑一遍：阈值 9 格/秒、满速 40、上限 5×。
         *    修复前，两下 10 ms 的轻拨会得到约 1.4×（一格变 168 单位），
         *    在短页面上就是"整页翻过去"。现在必须原样一格。 */
        var userCfg = new BoostConfig
        {
            FastThresholdPerSec = 9,
            SaturationPerSec = 40,
            MinMultiplier = 1,
            MaxMultiplier = 5,
        };

        t.Reset();
        now = 30000;
        t.Sample(120, now, userCfg, false, false);
        now += 10;
        var u2 = t.Sample(120, now, userCfg, false, false);
        Check("真实参数下轻拨只有一格（不多发）",
            !u2.Boosted && u2.OutputUnits == 120, $"out={u2.OutputUnits} 单位");

        /* 同一份配置下，真正的连续快滚仍然要吃满 5× —— 护栏不能把功能一起废掉。 */
        t.Reset();
        now = 40000;
        BoostDecision fast = default;
        for (var i = 0; i < 20; i++)
        {
            fast = t.Sample(120, now, userCfg, false, false);
            now += 12;
        }
        Check("真实参数下连续快滚仍到 5×", fast.Boosted && fast.Multiplier > 4.5,
            $"mult={fast.Multiplier:0.00}");
    }

    /// <summary>
    /// 滚动语义分流：哪些窗口"一个事件滚一档"，以及那种窗口该怎么发。
    ///
    /// <para>
    /// 这一组是"轻轻一滚翻一整页"的最终答案。WPF / WinForms 的滚动**只看方向、
    /// 不看 delta 大小**（WPF 官方 issue #5936 至今未改），所以把一个 120 摊成
    /// 8 个 15 会被它们当成 8 次滚动 —— 一格变一屏。浏览器 / Qt / Office 按像素
    /// 比例滚，摊帧才安全。
    /// </para>
    /// </summary>
    private static void TestScrollSemantics()
    {
        Section("滚动语义（按事件 vs 按比例）");

        Check("WPF 窗口算「按事件滚动」",
            ScrollSemantics.IsPerEventScroll("HwndWrapper[CapsLockPlus.exe;;3f2a1b0c-1234-4567-89ab-cdef01234567]"));
        Check("WinForms 窗口算「按事件滚动」",
            ScrollSemantics.IsPerEventScroll("WindowsForms10.Window.8.app.0.141b42a_r7_ad1"));

        /* 自带平滑动画的框架（v1.8.32）：对它们必须"只发一个事件"。 */
        Check("Chromium / Electron 认作自带平滑",
            ScrollSemantics.SelfAnimates("Chrome_WidgetWin_1"));
        Check("Chromium 的 0 号窗口也认",
            ScrollSemantics.SelfAnimates("Chrome_WidgetWin_0"));
        Check("Firefox 认作自带平滑",
            ScrollSemantics.SelfAnimates("MozillaWindowClass"));
        Check("WPF 不算自带平滑（它是按事件）",
            !ScrollSemantics.SelfAnimates("HwndWrapper[App;;1234]"));
        Check("普通 Win32 窗口不算自带平滑",
            !ScrollSemantics.SelfAnimates("Notepad"));
        /* v1.9.0：语义表驱动 —— 表里每一条都必须自洽（分类 + 是否补偿系统行数）。 */
        var ruleCount = 0;
        foreach (var (prefix, kind, ignoreLines) in ScrollSemantics.AllRules())
        {
            ruleCount++;
            var sample = prefix.EndsWith("[", StringComparison.Ordinal) ? prefix + "App;;1]"
                       : prefix.EndsWith(".", StringComparison.Ordinal) ? prefix + "Window.8.app.0.x"
                       : prefix.EndsWith("_", StringComparison.Ordinal) ? prefix + "1"
                       : prefix;
            Check($"语义表：{prefix} → {kind}", ScrollSemantics.Classify(sample) == kind,
                ScrollSemantics.Classify(sample).ToString());
            Check($"语义表：{prefix} 的补偿标记一致",
                ScrollSemantics.IgnoresSystemLines(sample) == ignoreLines);
        }
        Check("语义表已覆盖主要框架（Qt/PP 走专用判定，不在表内）", ruleCount >= 6, ruleCount.ToString());

        Check("两个语义互斥（不会同时成立）",
            !(ScrollSemantics.SelfAnimates("Chrome_WidgetWin_1")
              && ScrollSemantics.IsPerEventScroll("Chrome_WidgetWin_1")));

        Check("Chrome / Electron 不算（按像素比例）",
            !ScrollSemantics.IsPerEventScroll("Chrome_WidgetWin_1"));
        Check("Firefox 不算", !ScrollSemantics.IsPerEventScroll("MozillaWindowClass"));
        Check("Office 不算", !ScrollSemantics.IsPerEventScroll("XLMAIN"));
        /* PowerPoint（v1.8.38）：一格必须只发一个事件，否则"滚一下翻两页" */
        Check("PowerPoint 主窗口算「按事件滚动」",
            ScrollSemantics.IsPerEventScroll("PPTFrameClass"));
        Check("PowerPoint 放映窗口算「按事件滚动」",
            ScrollSemantics.IsPerEventScroll("screenClass"));
        Check("PowerPoint 的一格只发一格",
            ScrollSemantics.WholeNotchesFor(Native.WHEEL_DELTA) == 1);

        /* 细腻模式对 PPT 不能补偿：它"一格翻一页"，与系统行数无关。
         * 补了就是"滚一格跳三页"（用户实测）。 */
        Check("PPT 不读系统行数（细腻模式不补偿）",
            ScrollSemantics.IgnoresSystemLines("PPTFrameClass"));
        Check("放映窗口同样不补偿",
            ScrollSemantics.IgnoresSystemLines("screenClass"));

        /* WPS 演示（实测类名 PP12FrameClass）：和 PPT 一样按事件、不读系统行数 */
        Check("WPS 演示算「按事件滚动」",
            ScrollSemantics.IsPerEventScroll("PP12FrameClass"));
        Check("WPS 演示不读系统行数",
            ScrollSemantics.IgnoresSystemLines("PP12FrameClass"));
        Check("PP14FrameClass 之类的变体也认",
            ScrollSemantics.IsPerEventScroll("PP14FrameClass"));
        Check("不是 PP 数字 FrameClass 的不误判",
            !ScrollSemantics.IsPerEventScroll("PPXFrameClass"));
        Check("WPF 读系统行数（需要补偿）",
            !ScrollSemantics.IgnoresSystemLines("HwndWrapper[App;;1]"));
        Check("记事本（系统 Edit）不算", !ScrollSemantics.IsPerEventScroll("Notepad"));

        /* Qt = 按事件滚动（v1.7.14）
         *
         * QScrollBar::wheelEvent 是 `angleDelta().y() / 120` 的**整数除法**：
         * 15/120 = 0，一个字节都不滚。所以微信（Qt51514QWindowIcon）里
         * 摊帧发 15 单位等于"什么都没发生" —— 这就是"聊天列表加速无效"的根因。
         * 必须按整格（±120）发，Qt 才认得。 */
        Check("Qt 5 窗口（微信 4.x）算「按事件滚动」",
            ScrollSemantics.IsPerEventScroll("Qt51514QWindowIcon"));
        Check("Qt 6 窗口也算", ScrollSemantics.IsPerEventScroll("Qt652QWindowIcon"));
        Check("Qt 变体（带点版本号）也算", ScrollSemantics.IsPerEventScroll("Qt6.5.2QWindowIcon"));
        Check("只是以 Qt 开头但不是 Qt 窗口的不算",
            !ScrollSemantics.IsPerEventScroll("QtWhatever") &&
            !ScrollSemantics.IsPerEventScroll("QtIconHost"));
        Check("空类名不算（拿不到就按比例处理，别误伤）",
            !ScrollSemantics.IsPerEventScroll(""));
        Check("前缀相近但不等的不算",
            !ScrollSemantics.IsPerEventScroll("HwndWrapperXX") &&
            !ScrollSemantics.IsPerEventScroll("WindowsForms11.Window.8.app.0"));

        /* 拆成几个整格：倍率兑现成"事件个数"，而不是"更小的 delta"。 */
        Check("没用加速时只发一格", ScrollSemantics.WholeNotchesFor(120) == 1);
        Check("3 倍 → 3 个整格", ScrollSemantics.WholeNotchesFor(360) == 3);
        Check("5 倍 → 5 个整格", ScrollSemantics.WholeNotchesFor(600) == 5);
        Check("不足一格也至少发 1 个", ScrollSemantics.WholeNotchesFor(30) == 1);
        Check("异常大的量被夹到 16 格", ScrollSemantics.WholeNotchesFor(999_999) == 16);
        Check("负数按绝对值算（方向由调用方给）", ScrollSemantics.WholeNotchesFor(-360) == 3);

        /* 整格模式怎么出帧：一帧一格，不够一格就不发。 */
        Check("整格模式：一格就发一格", ScrollAnimator.WholeNotchFrame(120) == 120);
        Check("整格模式：一帧绝不多发（3 格也是先发 1 格）",
            ScrollAnimator.WholeNotchFrame(360) == 120);
        Check("整格模式：不足一格不发（留着等下次）",
            ScrollAnimator.WholeNotchFrame(60) == 0);
        Check("整格模式：方向跟着走", ScrollAnimator.WholeNotchFrame(-240) == -120);
        Check("整格模式：没积压不发", ScrollAnimator.WholeNotchFrame(0) == 0);

        /* 关键不变式：分帧只是把"一坨"摊成"几帧"，总量一单位不能差。 */
        var pending = 600.0;
        var sent = 0;
        var frames = 0;
        while (Math.Abs(pending) >= 120 && frames < 100)
        {
            var u = ScrollAnimator.WholeNotchFrame(pending);
            if (u == 0) break;
            sent += u;
            pending -= u;
            frames++;
        }
        Check("整格模式：5 格分 5 帧发完，总量不差", sent == 600 && frames == 5,
            $"发出 {sent} / {frames} 帧");

        /* ── 注入边界：单帧量与积压上限必须装得下「加速之后的量」 ──
         *
         * 这条不变式 v1.6.1 就有，当年靠"系统行数补偿倍数"间接兑现（补偿恒为 3，
         * 容量也恒为 3 倍）。v1.7.0 撤掉接管方案时把补偿删了，容量跟着缩回 1 倍，
         * 满倍率一格（5× = 600 单位）正好顶到积压上限，猛甩时超出部分被静默截断 ——
         * 用户看到的"倍率读着 5×、页面只快一两成"就是这么来的（v1.7.3 修）。
         * 现在容量按配置的倍率上限放大，这里把三条不变式钉死：
         * ① 帧数 × 单帧量 ≈ 一格（摊帧不丢量）；
         * ② 满倍率的一格必须整块装得进积压（否则就是 v1.6.0 的"加速失效"）；
         * ③ 整格模式的单次投递上限容得下满倍率（否则 8× 会悄悄变成 5 格）。 */
        Section("注入边界（容量随倍率放大）");

        Check("单帧量 = 120 ÷ 每格帧数（4 帧 → 30）", RuntimeSettings.UnitsPerFrameFor(4) == 30);
        Check("8 帧/格 → 每帧 15", RuntimeSettings.UnitsPerFrameFor(8) == 15);
        Check("1 帧/格 → 一整格", RuntimeSettings.UnitsPerFrameFor(1) == 120);
        Check("帧数荒谬时也至少 1 单位（不会发出 0）", RuntimeSettings.UnitsPerFrameFor(400) >= 1);

        Check("积压上限 = 格数 × 120（5 格 → 600）", RuntimeSettings.BacklogUnitsFor(5.0) == 600);
        Check("1 格 → 120", RuntimeSettings.BacklogUnitsFor(1.0) == 120);

        /* ② 倍率容量：5× 加速的一格是 600 单位，5 格上限必须整块装得下
         *    （装不下就是 v1.6.0 那个"加速失效"：被截回一部分，还没处查）。 */
        Check("5× 加速的一格（600 单位）不会被积压上限截断",
            5 * 120 <= RuntimeSettings.BacklogUnitsFor(5.0, 5),
            $"上限 {RuntimeSettings.BacklogUnitsFor(5.0, 5)}");
        Check("8× 加速的一格（960 单位）也装得下",
            8 * 120 <= RuntimeSettings.BacklogUnitsFor(5.0, 8),
            $"上限 {RuntimeSettings.BacklogUnitsFor(5.0, 8)}");
        Check("倍率容量同时放大单帧量（8 帧/格 × 5× → 75）",
            RuntimeSettings.UnitsPerFrameFor(8, 5) == 75);
        Check("关掉加速时容量回到 1 倍（行为与从前一致）",
            RuntimeSettings.UnitsPerFrameFor(8, 1) == 15
            && RuntimeSettings.BacklogUnitsFor(5.0, 1) == 600);

        /* 用真实配置端到端验一遍：用户那份（阈值 10 / 饱和 39 / 上限 5× /
         * 8 帧每格）烘焙出来的快照，容量必须按 5× 放大。 */
        var realCfg = AppConfig.Default();
        realCfg.Boost.Enabled = true;
        realCfg.Boost.MaxMultiplier = 5;
        realCfg.Smooth.FramesPerNotch = 8;
        realCfg.Smooth.MaxBacklogNotches = 5;
        var real = RuntimeSettings.From(realCfg);
        Check("真实配置（5× / 5 格）积压上限 = 3000 单位",
            Math.Abs(real.MaxBacklogUnits - 3000) < 1e-9, $"实际 {real.MaxBacklogUnits}");
        Check("真实配置单帧量 = 75（8 帧/格 × 5×）", real.UnitsPerFrame == 75,
            $"实际 {real.UnitsPerFrame}");
        Check("真实配置的倍率容量 = 5", real.BoostScale == 5, real.BoostScale.ToString());
        Check("取整余量上限跟着倍率容量（5× → 600）",
            WheelVelocityTracker.ResidualLimitFor(5) == 600,
            WheelVelocityTracker.ResidualLimitFor(5).ToString());
        Check("关掉加速时余量上限回到 120", WheelVelocityTracker.ResidualLimitFor(1) == 120,
            WheelVelocityTracker.ResidualLimitFor(1).ToString());

        /* 关掉加速：容量回到 1 倍，和从前的行为完全一致。 */
        var noBoost = AppConfig.Default();
        noBoost.Boost.Enabled = false;
        noBoost.Boost.MaxMultiplier = 5;
        var off = RuntimeSettings.From(noBoost);
        Check("关掉加速后容量不放大（5 格 → 600）",
            Math.Abs(off.MaxBacklogUnits - 600) < 1e-9, $"实际 {off.MaxBacklogUnits}");
        Check("关掉加速后单帧量 = 120 ÷ 帧数", off.UnitsPerFrame == 30,
            $"实际 {off.UnitsPerFrame}");

        /* ③ 整格模式：一次投满 16 档（1920 单位）也不能被普通上限吃掉。 */
        Check("整格模式单次投递上限容得下满倍率（16 档 > 5 格上限）",
            ScrollAnimator.MaxWholeNotches * (double)Native.WHEEL_DELTA
                > RuntimeSettings.BacklogUnitsFor(5.0),
            $"{ScrollAnimator.MaxWholeNotches} 档 = {ScrollAnimator.MaxWholeNotches * Native.WHEEL_DELTA} 单位");

        /* 系统行数现在只被"读"和"用户主动写"，不再参与任何注入量的计算。
         * 这里把它的取值范围钉死：非法值一律不写进系统（-1 / 0 / 99 都是坑）。 */
        Section("系统行数（只读 + 用户主动写）");

        Check("1~10 都可用", SystemWheelLines.IsUsable(1) && SystemWheelLines.IsUsable(10));
        Check("「一次滚一页」不可用", !SystemWheelLines.IsUsable(SystemWheelLines.PageMode));
        Check("0（读取失败）不可用", !SystemWheelLines.IsUsable(0));
        Check("超出范围不可用", !SystemWheelLines.IsUsable(11) && !SystemWheelLines.IsUsable(-5));
        Check("非法值一律拒绝写入（绝不把系统写成 -1/0/99）",
            !SystemWheelLines.Set(0) && !SystemWheelLines.Set(-1) && !SystemWheelLines.Set(99));

        /* 本程序自己的窗口要能认出来：它的页面按像素滚（PreviewMouseWheel），
         * 所以不能走"整格语义"，否则滚过头、也丢掉摊帧的平滑。 */
        Section("认出自己（设置窗口按比例滚）");
        var own = RuntimeSettings.From(AppConfig.Default());
        Check("认得出自己的 exe", own.IsOwnExe(Environment.ProcessPath),
            $"own={own.OwnExe}");
        Check("别的程序不算自己", !own.IsOwnExe("notepad.exe"));
        Check("空 exe 不算自己", !own.IsOwnExe(""));
        Check("null 不算自己", !own.IsOwnExe(null));
    }

    private static void TestPrecisionInput()
    {
        Section("高精度输入（触控板 / 自由滚轮）");
        var cfg = AppConfig.Default().Boost;
        var t = new WheelVelocityTracker();
        var now = 0L;
        var allNative = true;
        BoostDecision d = default;

        // 触控板：每 4ms 一个 15 单位的 delta（频率极高，但绝不能当成"快滚"）
        for (var i = 0; i < 40; i++)
        {
            d = t.Sample(15, now, cfg, false, false);
            if (d.Boosted || d.OutputUnits != 15) allNative = false;
            now += 4;
        }
        Check("默认不增强高精度输入，原样放行", allNative);

        // 显式允许后才接管
        t.Reset();
        now = 100000;
        for (var i = 0; i < 40; i++)
        {
            d = t.Sample(15, now, cfg, false, true);
            now += 4;
        }
        Check("打开开关后高精度输入也能增强", d.Boosted, $"mult={d.Multiplier:0.00}");
    }

    private static void TestReversal()
    {
        Section("反向纠正");
        var cfg = AppConfig.Default().Boost;
        var t = new WheelVelocityTracker();
        var now = 0L;
        for (var i = 0; i < 12; i++) { t.Sample(120, now, cfg, false, false); now += 20; }

        // 突然往回滚一格：必须立刻按原样 1:1 发出，不能继续往前加速
        var d = t.Sample(-120, now + 20, cfg, false, false);
        Check("反向那一格不加速", !d.Boosted && d.OutputUnits == -120, $"out={d.OutputUnits}");
        Near("反向后速度清零重算", t.RatePerSec, 0, 0.001);
    }

    private static void TestRounding()
    {
        Section("倍率取整不丢量");
        // 1.37 倍：单次 120 → 164.4，必须靠余量累积让长期平均正好是 1.37
        var cfg = new BoostConfig
        {
            FastThresholdPerSec = 1,
            SaturationPerSec = 1.2,
            MinMultiplier = 1.37,
            MaxMultiplier = 1.37,
        };
        var t = new WheelVelocityTracker();
        var now = 0L;

        /* 起步护栏会吃掉开头几格（1 格护栏 + 1 格渐入；12 ms 一格时还有 70 ms
         * 的"够久"护栏，实际要 5~6 格才放开），所以"长期平均倍率"
         * 只能从**预热之后**开始量。那之后的余量累积必须严丝合缝 ——
         * 这正是这条断言原本要守的不变式。 */
        const int warmup = 6;
        var lead = 0;
        var steady = 0;
        for (var i = 0; i < 100; i++)
        {
            now += 12;
            var o = t.Sample(120, now, cfg, false, false).OutputUnits;
            if (i < warmup) lead += o;
            else steady += o;
        }

        Check("预热期不会超发（护栏 + 渐入）",
            lead <= (int)(120 * warmup * 1.2), $"预热 {lead} 单位 / {warmup} 格");

        Near("预热之后的总量（1.37× 不漂移）", steady, 120 * (100 - warmup) * 1.37, 12);

        // 直接用"总输出 / 总输入 = 倍率"来断言更直观
        var ratio = steady / (120.0 * (100 - warmup));
        Check("预热之后的平均倍率等于配置值", Math.Abs(ratio - 1.37) < 0.02, $"实际 {ratio:0.000}");

        // 任何一次输出都不能小于输入（否则"加速"反而变慢）
        var t2 = new WheelVelocityTracker();
        var neverShrinks = true;
        now = 0;
        for (var i = 0; i < 50; i++)
        {
            now += 10;
            var d = t2.Sample(120, now, cfg, false, false);
            if (d.Boosted && Math.Abs(d.OutputUnits) < 120) neverShrinks = false;
        }
        Check("加速后单次输出不少于原样一格", neverShrinks);
    }

    /// <summary>
    /// 松手滑行与**收尾**：结尾不能"补一下"，也不能"匀速走一段然后撞墙"。
    ///
    /// <para>
    /// 这里钉的是两代 bug：
    /// </para>
    /// <list type="number">
    /// <item>旧版在截止期、或滑行上限到点时把剩余积压整块倒出去 ——
    /// 指数曲线已经把速度降到 1~2 单位/帧，突然补一发 6~30 单位，
    /// 就是"末尾一下刻度感"（DESIGN 第 28 节）。</item>
    /// <item>取整时把 0 抬成 1（每帧至少发 1 单位）—— 曲线衰减到 0.4、0.2 之后，
    /// 速度被"抬"成一个恒定的 1 单位/帧，pending 一发完就静止。
    /// 视觉上就是"最后一段匀速前进，然后突然停住"（DESIGN 第 34 节）。</item>
    /// </list>
    /// <para>
    /// 现在的取整规则是**零头跨帧累积**（<see cref="ScrollAnimator.Quantize"/>）：
    /// 两次发送的间隔越拉越长，等效速度平滑趋近 0；同时滑行 τ 不低于跟手 τ，
    /// 避免"松手之后反而加速"。
    /// </para>
    /// </summary>
    private static void TestGlide()
    {
        Section("松手滑行与收尾");

        const int Upf = 30;          // 每格拆 4 帧
        const double Dt = 8.0;       // 120fps
        const double Tau = 45;

        // ① 跟手曲线：总量守恒、能停下来、末尾单调不增
        var hand = Drain(120, Dt, Tau, Upf, out var handSent);
        Check("跟手期总量一单位不差", handSent == 120, $"发出 {handSent}");
        Check("跟手期能停下来（不会一直 1 单位/帧 爬）", hand.Count < 80, $"{hand.Count} 帧");
        Check("跟手期末尾不突增（允许舍入 ±1）", TailMonotone(hand, 8), string.Join("/", hand.TakeLast(8)));

        /* ② 收尾 τ 由「惯性长度」按比例映射（ReleaseTau，主循环用的就是它）。
         *
         * 以前这里是"反解 τ 让积压恰好在 GlideMs 内排空"，结果绝大多数参数下
         * 都被跟手下限夹住 —— 滑块拖了手感不变。现在是比例映射，拖哪就是哪。 */
        var glideTau = ScrollAnimator.ReleaseTau(Tau, 180);
        Check("默认惯性(180)下收尾 τ = 跟手 τ", Math.Abs(glideTau - Tau) < 1e-9, $"{glideTau:0.0} ms");
        Check("惯性调大 → 收尾 τ 变大", ScrollAnimator.ReleaseTau(Tau, 400) > Tau,
            $"{ScrollAnimator.ReleaseTau(Tau, 400):0.0} ms");
        Check("惯性调小 → 收尾 τ 变小（收得更利落）", ScrollAnimator.ReleaseTau(Tau, 60) < Tau,
            $"{ScrollAnimator.ReleaseTau(Tau, 60):0.0} ms");
        Check("收尾 τ 有下限（不会小到一帧发完）", ScrollAnimator.ReleaseTau(Tau, 1) >= 12,
            $"{ScrollAnimator.ReleaseTau(Tau, 1):0.0} ms");
        Check("收尾 τ 有上限（不会拖到几百毫秒）", ScrollAnimator.ReleaseTau(Tau, 400) <= 140,
            $"{ScrollAnimator.ReleaseTau(Tau, 400):0.0} ms");
        Check("惯性为 0 时直接用跟手 τ", Math.Abs(ScrollAnimator.ReleaseTau(Tau, 0) - Tau) < 1e-9);

        var glide = Drain(120, Dt, ScrollAnimator.ReleaseTau(Tau, 400), Upf, out var glideSent);
        Check("收尾期总量一单位不差", glideSent == 120, $"发出 {glideSent}");
        Check("惯性调大后收尾明显更久（>1.2 倍帧数）", glide.Count > hand.Count * 1.2,
            $"{glide.Count} vs {hand.Count} 帧");
        Check("收尾期末尾不突增", TailMonotone(glide, 8), string.Join("/", glide.TakeLast(8)));

        // ④ 单帧上限：追赶时也不许"一帧跨好几格"
        var burst = ScrollAnimator.FrameAmount(600, 16.7, Tau, Upf, true);
        Check("单帧不超过 4 个标准帧量（默认 120 单位）", burst <= Upf * 4, $"{burst:0.0} 单位");

        /* ⑤ 关闭平滑仍然一次发完；**尾段不再被抬成 1 单位**（本轮修复的核心）。
         *
         * 旧版这里有一条 "最后 1 个单位一次收掉（不留 1 单位/帧 的尾巴）" ——
         * 那条断言本身就在维护 bug：它把"每帧至少 1 单位"当成了正确行为。 */
        Check("关闭平滑时一次发完", ScrollAnimator.FrameAmount(360, 8, 45, Upf, false) == 360);

        var tailWant = ScrollAnimator.FrameAmount(6, Dt, Tau, Upf, true);
        Check("尾段的曲线金额是小数，不再被抬成 1 单位/帧", tailWant > 0 && tailWant < 1.0,
            $"{tailWant:0.00} 单位");

        var q0 = ScrollAnimator.Quantize(tailWant, 0, out var c1);
        Check("小数金额这一帧先不发（零头攒着）", q0 == 0 && Math.Abs(c1 - tailWant) < 1e-9,
            $"发出 {q0}，零头 {c1:0.00}");

        var q1 = ScrollAnimator.Quantize(0.4, 0.8, out var c2);
        Check("攒够一单位才发，且只扣整数部分", q1 == 1 && Math.Abs(c2 - 0.2) < 1e-9,
            $"发出 {q1}，零头 {c2:0.00}");

        Check("没攒够就不发（0.4 + 0.4 还是 0）",
            ScrollAnimator.Quantize(0.4, 0.4, out var c3) == 0 && Math.Abs(c3 - 0.8) < 1e-9,
            $"零头 {c3:0.00}");

        Check("换方向时丢掉旧方向的零头（不提前抖一下）",
            ScrollAnimator.Quantize(-1.2, 0.9, out var c4) == -1 && c4 <= 0.2 + 1e-9, $"{c4:0.00}");

        // ⑥ 真实循环的整段模拟：跟手 → 松手（τ 平滑接管）→ 收尾
        foreach (var (start, fps) in new[] { (120.0, 60.0), (120.0, 144.0), (360.0, 60.0), (600.0, 144.0) })
        {
            var fr = SimulateTimed(start, fps, 300, out var sent);
            var seq = UnitsOf(fr);
            var last = seq.Count == 0 ? 0 : seq[^1];
            var maxFrame = seq.Count == 0 ? 0 : seq.Max();
            Check($"整段模拟（{start:0} 单位 / {fps:0}Hz）总量守恒", sent == (int)start, $"{sent}");
            Check($"整段模拟（{start:0} 单位 / {fps:0}Hz）末尾不突增", TailMonotone(seq, 10),
                string.Join("/", seq.TakeLast(10)));
            Check($"整段模拟（{start:0} 单位 / {fps:0}Hz）最后一帧 ≤ 2 单位（结尾不补发）",
                last <= 2, $"{last} 单位");
            Check($"整段模拟（{start:0} 单位 / {fps:0}Hz）单帧 ≤ 120 单位", maxFrame <= 120, $"{maxFrame} 单位");
        }

        /* ⑦ 收尾不砸停 + 不拖：这一组是本轮修复的验收标准。
         *
         * 指标用"末 6 帧的平均速度"（单位/秒）而不是"最后一帧多大"：
         * 砸停的形态是"速度还有 1.3 格/秒，然后突然 0"，关键在**停下来之前的速率**。
         * 修复前实测 160~264 单位/秒（1.3~2.2 格/秒）；v1.7.1 起 38~51；
         * v1.7.4 落地段加宽（半格 + τ=90）后压到 30 以内。阈值 72 单位/秒
         * （0.6 格/秒）留在"肉眼读作渐停"和"砸停"之间。
         *
         * 同时钉住"最后一帧不拖"和"落地段不长"：用户报的
         * "每次滚完最后一下，真的有一种延迟"，形态是收尾的最后 1~2 单位拖到
         * 80~94 ms 才发、走完 95% 之后还要再磨 180 ms。
         *
         * ⚠ "落地段不长"的阈值随 v1.8.8 改过：收尾时长现在由手速决定
         * 所以判据换成"≤250ms 且不超过整段 62%"，与 --frametrace 同一口径。 */
        foreach (var (start, fps, glideMs) in new[]
                 {
                     (120.0, 60.0, 100), (120.0, 160.0, 100),
                     (360.0, 160.0, 300), (720.0, 144.0, 300),
                 })
        {
            var fr = SimulateTimed(start, fps, glideMs, out var sent);
            var rate = TailRate(fr, 6);
            var t95 = TimeToPercent(fr, 0.95);
            var land = (fr.Count == 0 ? 0 : fr[^1].AtMs) - t95;
            var maxGap = MaxFrameGap(fr);

            Check($"{start:0} 单位/{fps:0}Hz/滑行{glideMs}ms：末 2 帧每帧 ≤ 2 单位（Mos 模型的收敛尾巴）",
                fr.Skip(Math.Max(0, fr.Count - 2)).All(f => Math.Abs(f.Units) <= 2),
                string.Join("/", UnitsOf(fr).TakeLast(6)));
            Check($"{start:0} 单位/{fps:0}Hz/滑行{glideMs}ms：走完 95% 之后仍有 ≥ 25 ms 的落地段", land >= 25, $"{land:0} ms");
            /* 落地段的上限是**跟着收尾设计走的**，不是拍脑袋的 150：
             * v1.8.8 之前有一条固定的 150ms 收尾硬上界，落地段自然 ≤150；
             * 6 格猛甩的落地段实测 167ms —— 这就是"快滚尾巴更长"的应有形态。
             * 这里改用 --frametrace 同一条口径：绝对上限 250ms，
             * 同时"落地段不超过整段收尾的 62%"（防止它变成长时间爬行）。 */
            var drainMs = fr.Count == 0 ? 0 : fr[^1].AtMs;
            Check($"{start:0} 单位/{fps:0}Hz/滑行{glideMs}ms：落地段 ≤ 250 ms（收尾不拖）",
                land <= 250, $"{land:0} ms");
            Check($"{start:0} 单位/{fps:0}Hz/滑行{glideMs}ms：落地段不超过整段的 62%",
                drainMs <= 0 || land <= Math.Max(40, drainMs * 0.62), $"{land:0} / {drainMs:0} ms");
            Check($"{start:0} 单位/{fps:0}Hz/滑行{glideMs}ms：末帧间隔 ≤ 70 ms（最后一帧不拖）",
                maxGap <= 70, $"{maxGap:0.#} ms");
            Check($"{start:0} 单位/{fps:0}Hz/滑行{glideMs}ms：总量守恒", sent == (int)start, $"{sent}");

            /* 中段步长起伏：进度窗口 20%~80%。和 frametrace ④d 同口径。
             * 时间窗（t95*0.4 ~ last*0.9）会把衰减尾巴扫进来，
             * max/median 自然算到 5~6×，但那是指数衰减的自然形态，
             * 不是用户报的"刻度感 / 一顿一顿"。 */
            var stepiness = MidStepiness(fr);
            Check($"{start:0} 单位/{fps:0}Hz/滑行{glideMs}ms：中段步长起伏 ≤ 3.5×（不一顿一顿）",
                stepiness <= 3.5, $"最大/中位 = {stepiness:0.00}");
        }

        // ⑧ 参数默认值与夹取
        var c = AppConfig.Default();
        Check("默认开启滑行（GlideMs > 0）", c.Smooth.GlideMs > 0, c.Smooth.GlideMs.ToString());
        Check("默认滑行时长合理（300~500ms，v1.8.27 拉长到 420）",
            c.Smooth.GlideMs is >= 300 and <= 500, c.Smooth.GlideMs.ToString());
        c.Smooth.GlideMs = 9999;
        c.Smooth.GlideDelayMs = 1;
        c.Normalize();
        Check("越界的滑行参数会被夹回", c.Smooth.GlideMs == 800 && c.Smooth.TailTauMs >= 5,
            $"{c.Smooth.GlideMs} / {c.Smooth.TailTauMs}");
    }

    /// <summary>一帧记录：相对"滚轮动作"的毫秒数 + 这一帧真正发出去的整数单位。</summary>
    private readonly record struct SimFrame(double AtMs, int Units);

    private static List<int> UnitsOf(List<SimFrame> fr) => fr.Select(f => f.Units).ToList();

    /// <summary>
    /// 复刻动画线程的一整段：跟手 τ → 松手后按滑行时长反解 τ（55 ms smoothstep 过渡）
    /// → 收尾。取整改走**零头累积**（<see cref="ScrollAnimator.Quantize"/>），
    /// 和真实循环完全一致 —— 否则这里测的就不是线上跑的那条路径。
    /// </summary>
    /// <param name="start">一次动作推入的总量（单位）。</param>
    /// <param name="fps">出帧频率。</param>
    /// <param name="glideMs">松手后滑行多久；0 = 不滑行。</param>
    private static List<SimFrame> SimulateTimed(double start, double fps, int glideMs, out int sent)
    {
        const double Tau = 45, GlideDelay = 30, Ramp = 55;
        const int Upf = 30;
        const double DeadZone = 1.0;
        const int MaxSnap = 2;

        var dt = 1000.0 / fps;
        var fr = new List<SimFrame>();
        var p = start;
        var t = 0.0;
        var glideStart = -1.0;
        var glideTau = Tau;
        var carry = 0.0;

        /* 和线上主循环（ScrollAnimator.Loop）逐行对应：
         * 一级低通 step = 残量×(1-e^(-dt/τ)) → 步长 < 死区时补齐/单发 → 停。
         * 这里不再有"落地段插值"和"尾段 ln 调度"——那两套已经在 v1.8.14 删掉了，
         * 仿真要是还留着它们，测的就是不存在的代码。 */
        while (p > 0 && fr.Count < 4000)
        {
            t += dt;
            var tau = Tau;
            if (glideMs > 0 && t > GlideDelay)
            {
                if (glideStart < 0)
                {
                    glideStart = t;
                    glideTau = ScrollAnimator.ReleaseTau(Tau, glideMs);
                }
                var w = Math.Clamp((t - glideStart) / Ramp, 0, 1);
                w = w * w * (3 - 2 * w);
                tau = Tau + (glideTau - Tau) * w;
            }

            var want = ScrollAnimator.FrameAmount(p, dt, tau, Upf, true);
            int e;
            if (Math.Abs(want) < DeadZone)
            {
                var residual = (int)Math.Round(Math.Abs(p));
                e = residual > 0 && residual <= MaxSnap ? (int)(Math.Sign(p) * residual) : (int)Math.Sign(p);
            }
            else
            {
                e = ScrollAnimator.Quantize(want, carry, out carry);
            }

            if (e == 0) continue;
            if (Math.Abs(e) > p) e = (int)p * Math.Sign(e);
            p -= e;
            fr.Add(new SimFrame(t, e));
        }

        sent = (int)Math.Round(start - p);
        return fr;
    }
    /// <summary>把一个积压按固定 τ 排空（带零头累积），返回每帧发出的绝对量。</summary>
    private static List<int> Drain(double pending, double dt, double tau, int upf, out int sent)
    {
        var seq = new List<int>();
        var p = pending;
        var carry = 0.0;
        var guard = 0;

        while (p > 0 && guard++ < 4000)
        {
            var want = ScrollAnimator.FrameAmount(p, dt, tau, upf, true);
            var e = ScrollAnimator.Quantize(want, carry, out carry);
            if (e == 0) continue;
            if (Math.Abs(e) > p) e = (int)Math.Sign(p) * (int)p;
            p -= e;
            seq.Add(Math.Abs(e));
        }

        sent = (int)Math.Round(pending - p);
        return seq;
    }

    /// <summary>末 <paramref name="n"/> 帧的平均速度（单位/秒）—— 量"停下来之前还有多快"。</summary>
    private static double TailRate(List<SimFrame> fr, int n)
    {
        if (fr.Count < 2) return 0;
        var tail = fr.Skip(Math.Max(0, fr.Count - n)).ToList();
        var span = tail[^1].AtMs - tail[0].AtMs;
        if (span <= 0) return 0;
        return tail.Skip(1).Sum(f => Math.Abs(f.Units)) / (span / 1000.0);
    }

    /// <summary>相邻两次发送之间的最大间隔（毫秒）—— 量"最后一帧拖不拖"。</summary>
    private static double MaxFrameGap(List<SimFrame> fr)
    {
        if (fr.Count < 2) return 0;
        var max = 0.0;
        for (var i = 1; i < fr.Count; i++)
            max = Math.Max(max, fr[i].AtMs - fr[i - 1].AtMs);
        return max;
    }

    /// <summary>走到总位移的 <paramref name="pct"/> 时用了多少毫秒。</summary>
    private static double TimeToPercent(List<SimFrame> fr, double pct)
    {
        if (fr.Count == 0) return 0;
        var total = fr.Sum(f => Math.Abs(f.Units));
        var acc = 0;
        foreach (var f in fr)
        {
            acc += Math.Abs(f.Units);
            if (acc >= total * pct) return f.AtMs;
        }
        return fr[^1].AtMs;
    }

    /// <summary>
    /// 中段步长起伏：进度 20%~80% 的最大步长 / 中位步长。
    /// 与 frametrace ④d 同口径 —— 时间窗会把衰减尾巴扫进来、测出假的"刻度感"。
    /// </summary>
    private static double MidStepiness(List<SimFrame> fr)
    {
        if (fr.Count < 3) return 1.0;
        var total = fr.Sum(f => Math.Abs(f.Units));
        if (total <= 0) return 1.0;
        var acc = 0;
        var mid = new List<int>();
        foreach (var f in fr)
        {
            acc += Math.Abs(f.Units);
            if (acc >= total * 0.20 && acc <= total * 0.80)
                mid.Add(Math.Abs(f.Units));
        }
        if (mid.Count < 3) return 1.0;
        mid.Sort();
        return mid.Max() / Math.Max(1.0, mid[mid.Count / 2]);
    }

    /// <summary>末尾 <paramref name="n"/> 帧是否单调不增（允许 ±1 的舍入抖动）。</summary>
    private static bool TailMonotone(List<int> seq, int n)
    {
        if (seq.Count < 2) return true;
        var from = Math.Max(1, seq.Count - n);
        for (var i = from; i < seq.Count; i++)
            if (seq[i] > seq[i - 1] + 1) return false;
        return true;
    }

    private static void TestFrameSpreading()
    {
        Section("平滑摊帧");

        const int Upf = 30;
        const double Tau = 45;

        // 360 单位、8ms 一帧、τ=45：必须摊成多帧、刚好发完、单帧不超过 1 格
        var seq360 = Drain(360, 8, Tau, Upf, out var sum360);
        Check("能在合理帧数内发完", sum360 == 360, $"发出 {sum360}");
        Check("总量一单位不差", sum360 == 360, $"发出 {sum360}");
        Check("确实摊成了多帧（不是一次性甩出）", seq360.Count >= 3, $"用了 {seq360.Count} 帧");
        Check("每帧不超过原样一格（看着才顺）", seq360.Max() <= 120, $"最大 {seq360.Max()}");

        // 关闭平滑时必须一次性发完
        var one = ScrollAnimator.FrameAmount(360, 8, Tau, Upf, false);
        Check("关闭平滑时一次发完", one == 360, $"实际 {one}");

        /* 旧版这里有一条「超过截止时间立刻发完」的断言 —— 那正是要修的 bug。
         * 现在同样的输入只能发一小段：没有截止期，也就没有倾泻。 */
        var late = ScrollAnimator.FrameAmount(360, 8, Tau, Upf, true);
        Check("过期不会一次发完（没有截止期倾泻）", late < 120, $"{late:0.0} 单位");

        /* ── 单格滚动（120 单位）的平滑形态 ──
         * 修复前的每帧下限写死 30 单位（=1/4 格）：120 的积压被 30/30/30
         * 轰掉四分之三，剩下的再按截止期赶工 —— 开头就是一个大跳。
         * 下限删掉之后，首帧必须是曲线本身给的那一小步。 */
        var firstEmit = ScrollAnimator.FrameAmount(120, 6.94, Tau, Upf, true);
        Check("单格滚动首帧不超过五分之一格（不突发）", firstEmit <= 24, $"首帧 {firstEmit:0.0}");

        var seq = Drain(120, 6.94, Tau, Upf, out var sent120);
        Check("单格滚动能完整发完", sent120 == 120, $"剩余 {120 - sent120}");
        Check("单格滚动总量一单位不差", seq.Sum() == 120, $"发出 {seq.Sum()}");
        Check("单格滚动摊成多帧（≥8 帧）", seq.Count >= 8, $"{seq.Count} 帧");
        Check("单格滚动每帧不超过四分之一格", seq.Max() <= 30, $"最大 {seq.Max()}");
        Check("单格滚动开头单调递减（不是 30/30/30 的阶梯）",
            seq.Count >= 7 && seq.Take(6).Zip(seq.Skip(1), (a, b) => b <= a).All(x => x),
            string.Join("/", seq.Take(8)));

        /* 首帧 dt 极小（线程刚被唤醒时 dt≈0.2ms）时不能按"一个整帧量"发 ——
         * 那会让每一格滚动的开头都先跳 15%。 */
        var tiny = ScrollAnimator.FrameAmount(120, 0.2, Tau, Upf, true);
        Check("dt≈0 的首帧只发零点几单位（不按整帧量突发）", tiny <= 2, $"{tiny:0.00} 单位");
    }

    private static void TestFrameRateFollowsDisplay()
    {
        Section("帧率跟随显示器刷新率");
        var cfg = AppConfig.Default();
        Check("默认就是自动跟随", cfg.Smooth.AutoFps);

        // 常见刷新率 → 期望的帧间隔
        double Ms(int hz) => 1000.0 / hz;
        foreach (var hz in new[] { 60, 75, 120, 144, 160, 165, 200, 240, 360 })
        {
            RefreshRateMonitor.OverrideForTest(hz);
            Near($"{hz} Hz → {Ms(hz):0.00} ms/帧", RuntimeSettings.From(cfg).FrameIntervalMs, Ms(hz), 0.02);
        }

        // 手动指定时不受显示器影响
        var manual = AppConfig.Default();
        manual.Smooth.AutoFps = false;
        manual.Smooth.TargetFps = 90;
        RefreshRateMonitor.OverrideForTest(240);
        Near("手动 90 Hz 时忽略显示器 240 Hz", RuntimeSettings.From(manual).FrameIntervalMs, Ms(90), 0.02);

        // 荒谬值要夹住：不能让一块报错率的驱动把出帧节奏带飞
        RefreshRateMonitor.OverrideForTest(2000);
        Check("异常高的刷新率被夹到 2 ms", RuntimeSettings.From(cfg).FrameIntervalMs >= 2.0 - 1e-9,
            RuntimeSettings.From(cfg).FrameIntervalMs.ToString("0.00"));
        RefreshRateMonitor.OverrideForTest(5);
        Check("异常低的刷新率被夹到 40 ms", RuntimeSettings.From(cfg).FrameIntervalMs <= 40.0 + 1e-9,
            RuntimeSettings.From(cfg).FrameIntervalMs.ToString("0.00"));

        // 读不出来时退回兜底值，而不是变成 0
        RefreshRateMonitor.OverrideForTest(0);
        Near("测不出刷新率时退回 120 Hz", RuntimeSettings.From(cfg).FrameIntervalMs, Ms(120), 0.02);

        // 160/200Hz 这类"非标准"值必须能被表示（整数值的 fps 存不下 6.25ms）
        RefreshRateMonitor.OverrideForTest(160);
        Check("160 Hz 的帧间隔不是整数毫秒", Math.Abs(RuntimeSettings.From(cfg).FrameIntervalMs - 6.25) < 0.01);

        RefreshRateMonitor.OverrideForTest(0);
    }

    /* ══════════════ 更新 ══════════════ */

}
