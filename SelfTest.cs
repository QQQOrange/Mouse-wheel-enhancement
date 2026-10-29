using System.Text;
using System.Text.Json;
using MouseWheelEnhancement.Core;
using MouseWheelEnhancement.Update;

namespace MouseWheelEnhancement;

/// <summary>
/// `--selftest`：把纯逻辑（速度曲线、平滑摊帧、版本比较、令牌清洗、名单匹配）
/// 跑一遍断言。
///
/// 这些东西的共同点是**没法靠肉眼验**：曲线差 5%、摊帧少发一格、版本号比较
/// 用了字符串序，界面上都看不出来，只有用户滚起来觉得"哪里怪怪的"。
/// 所以把它们钉成断言，改完随手跑一次就知道有没有踩坏。
/// 不依赖鼠标、窗口、网络，任何机器上都能跑。
/// </summary>
internal static class SelfTest
{
    private static readonly StringBuilder Report = new();
    private static int _pass;
    private static int _fail;

    public static int Run()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Mouse wheel enhancement 自检  v{Log.Version}");
        sb.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine(new string('-', 64));

        try
        {
            TestBoostCurve();
            TestVelocityTracking();
            TestBurstGuard();
            TestScrollSemantics();
            TestPrecisionInput();
            TestReversal();
            TestRounding();
            TestFrameSpreading();
            TestGlide();
            TestPlanScroll();
            TestFrameRateFollowsDisplay();
            TestVersionCompare();
            TestShaParse();
            TestTokenSanitize();
            TestFilter();
            TestRepoNormalize();
            TestConfigRoundTrip();
            TestStableEntry();
            TestLayoutJudgement();
            TestAdminMode();
            TestTheme();
            TestSelfBoost();
        }
        catch (Exception ex)
        {
            _fail++;
            sb.AppendLine($"✗ 自检本身抛异常：{ex}");
        }

        sb.AppendLine(new string('-', 64));
        sb.AppendLine($"通过 {_pass} 项，失败 {_fail} 项 —— {(_fail == 0 ? "全部通过" : "有失败项")}");

        var text = sb.ToString() + Environment.NewLine + Report;
        var outPath = Path.Combine(AppPaths.DataDir, "selftest.txt");
        try { File.WriteAllText(outPath, text, new UTF8Encoding(false)); } catch { }

        // 从终端启动时把结果打回终端（WinExe 没有自己的控制台，要借父进程的）
        try
        {
            Native.AttachConsole(Native.ATTACH_PARENT_PROCESS);
            Console.Out.Write(text);
            Console.Out.Write($"\n结果已写入：{outPath}\n");
            Console.Out.Flush();
        }
        catch { }

        return _fail == 0 ? 0 : 1;
    }

    /* ══════════════ 断言工具 ══════════════ */

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (ok)
        {
            _pass++;
            Report.AppendLine($"  ✓ {name}");
        }
        else
        {
            _fail++;
            Report.AppendLine($"  ✗ {name}{(detail == null ? "" : "  ← " + detail)}");
        }
    }

    private static void Near(string name, double actual, double expected, double tol = 0.001)
        => Check(name, Math.Abs(actual - expected) <= tol, $"实际 {actual:0.####}，期望 {expected:0.####}");

    private static void Section(string title) => Report.AppendLine($"\n[{title}]");

    private static bool IsWhole(double v) => Math.Abs(v - Math.Round(v)) < 1e-9;

    /* ══════════════ 曲线 ══════════════ */

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

        Check("Chrome / Electron 不算（按像素比例）",
            !ScrollSemantics.IsPerEventScroll("Chrome_WidgetWin_1"));
        Check("Firefox 不算", !ScrollSemantics.IsPerEventScroll("MozillaWindowClass"));
        Check("Office 不算", !ScrollSemantics.IsPerEventScroll("XLMAIN"));
        Check("Qt 不算", !ScrollSemantics.IsPerEventScroll("Qt5152QWindowIcon"));
        Check("记事本（系统 Edit）不算", !ScrollSemantics.IsPerEventScroll("Notepad"));
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

        // ② 滑行：τ 由「滑行时长」反解 —— 同样的积压要拖得更久，且结尾同样不突增
        var glideTau = ScrollAnimator.GlideTau(120, 300);
        Check("滑行 τ 由时长反解（比跟手 τ 大）", glideTau > Tau, $"{glideTau:0.0} ms");
        var glide = Drain(120, Dt, glideTau, Upf, out var glideSent);
        Check("滑行期总量一单位不差", glideSent == 120, $"发出 {glideSent}");
        Check("滑行期明显比跟手期长（>1.2 倍帧数）", glide.Count > hand.Count * 1.2,
            $"{glide.Count} vs {hand.Count} 帧");
        Check("滑行期末尾不突增", TailMonotone(glide, 8), string.Join("/", glide.TakeLast(8)));

        // ③ 积压越大，滑行 τ 越小（同样的时长走完更多量）—— 连甩之后才不会飘半秒
        var small = ScrollAnimator.GlideTau(120, 300);
        var big = ScrollAnimator.GlideTau(600, 300);
        Check("积压越大滑行 τ 越小（时长恒定）", big < small, $"{big:0.0} vs {small:0.0}");
        Check("滑行 τ 有下限（不会小到一帧发完）", big >= 12, $"{big:0.0}");

        /* 滑行只能减速，不能加速。
         * 积压小的时候（一格，松手时手里约半格）反解出来的 τ 会比跟手 τ 还小 ——
         * 那意味着"手一停，内容反而提速"，紧接着又撞上结尾，比匀速停更突兀。 */
        var rawShort = ScrollAnimator.GlideTau(120, 100);
        Check("短滑行反解出的 τ 会小于跟手 τ（所以必须有下限）", rawShort < Tau, $"{rawShort:0.0} ms");
        Check("滑行 τ 不低于跟手 τ（松手后不会提速）",
            Math.Abs(ScrollAnimator.GlideTau(120, 100, Tau) - Tau) < 1e-9,
            $"{ScrollAnimator.GlideTau(120, 100, Tau):0.0} ms");

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
         * 80~94 ms 才发、走完 95% 之后还要再磨 180 ms。 */
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

            Check($"{start:0} 单位/{fps:0}Hz/滑行{glideMs}ms：末 6 帧平均速度 ≤ 72 单位/秒（渐停而非砸停）",
                rate <= 72, $"{rate:0} 单位/秒");
            Check($"{start:0} 单位/{fps:0}Hz/滑行{glideMs}ms：走完 95% 之后仍有 ≥ 40 ms 的落地段",
                land >= 40, $"{land:0} ms");
            Check($"{start:0} 单位/{fps:0}Hz/滑行{glideMs}ms：落地段 ≤ 150 ms（收尾不拖）",
                land <= 150, $"{land:0} ms");
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
        Check("默认滑行时长合理（200~400ms）",
            c.Smooth.GlideMs >= 200 && c.Smooth.GlideMs <= 400, c.Smooth.GlideMs.ToString());
        c.Smooth.GlideMs = 9999;
        c.Smooth.GlideDelayMs = 1;
        c.Normalize();
        Check("越界的滑行参数会被夹回", c.Smooth.GlideMs <= 400 && c.Smooth.GlideDelayMs >= 10,
            $"{c.Smooth.GlideMs}/{c.Smooth.GlideDelayMs}");
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

        var dt = 1000.0 / fps;
        var fr = new List<SimFrame>();
        var p = start;
        var t = 0.0;
        var glideStart = -1.0;
        var glideTau = 0.0;
        var carry = 0.0;

        while (p > 0 && fr.Count < 4000)
        {
            t += dt;
            var tau = Tau;
            if (glideMs > 0 && t > GlideDelay)
            {
                if (glideStart < 0)
                {
                    glideStart = t;
                    glideTau = ScrollAnimator.GlideTau(p, glideMs, Tau);
                }
                var w = Math.Clamp((t - glideStart) / Ramp, 0, 1);
                w = w * w * (3 - 2 * w);
                tau = Tau + (glideTau - Tau) * w;

                /* 落地段：手里不足 LandingStartUnits 时把 τ 平滑**插值**到 LandingTauMs。
                 * v1.7.5 起用纯插值（不再 Math.Max）—— LandingTauMs 比滑行 τ 小的时候
                 * Math.Max 会让落地段失效。 */
                var inLanding = p < ScrollAnimator.LandingStartUnits;
                if (inLanding)
                {
                    var k = 1.0 - p / ScrollAnimator.LandingStartUnits;
                    tau = tau + (ScrollAnimator.LandingTauMs - tau) * k;
                }

                /* 收尾上界：松手之后整段收尾不超过 MaxFinishMs（v1.7.5）。
                 * 与 ScrollAnimator 主循环完全一致，否则这里测的就不是线上跑的那条路径。
                 * 落地段期间不收紧 —— 软着陆由 LandingTauMs 单独管。 */
                if (glideStart >= 0 && start > 1 && !inLanding)
                {
                    var spent = t - glideStart;
                    if (spent > 0)
                    {
                        var remainMs = Math.Max(20.0, ScrollAnimator.MaxFinishMs - spent);
                        var span = Math.Log(Math.Max(start / Math.Max(p, 1.0), 1.5));
                        var capTau = remainMs / Math.Max(span, 0.2);
                        if (capTau < tau) tau = Math.Max(6.0, capTau);
                    }
                }
            }

            var want = ScrollAnimator.FrameAmount(p, dt, tau, Upf, true);
            var e = ScrollAnimator.Quantize(want, carry, out carry);
            if (e == 0) continue;                       // 攒零头，这一帧不发
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

    private static void TestVersionCompare()
    {
        Section("版本号比较");
        Check("1.10.0 > 1.9.0（按数字不是字符串）", UpdateService.IsNewer("1.10.0", "1.9.0"));
        Check("1.2.1 > 1.2.0", UpdateService.IsNewer("1.2.1", "1.2.0"));
        Check("2.0.0 > 1.99.99", UpdateService.IsNewer("2.0.0", "1.99.99"));
        Check("同版本不算新", !UpdateService.IsNewer("1.2.0", "1.2.0"));
        Check("带 v 前缀能比", UpdateService.IsNewer("v1.3.0", "1.2.0"));
        Check("1.2 与 1.2.0 视为相同", !UpdateService.IsNewer("1.2", "1.2.0"));
        Check("解析不了一律不催升级", !UpdateService.IsNewer("abc", "1.2.0"));
        Check("空串不催升级", !UpdateService.IsNewer("", "1.2.0"));
    }

    private static void TestShaParse()
    {
        Section("从发布说明里取 SHA256");
        var sha = new string('A', 64);
        Check("能抠出 64 位十六进制", UpdateService.ShaFromNotes($"修复若干\nSHA256: {sha}") == sha);
        Check("小写也认且统一成大写",
            UpdateService.ShaFromNotes("sha256: " + new string('b', 64)) == new string('B', 64));
        Check("没有就返回空串", UpdateService.ShaFromNotes("这段说明里没有校验值") == "");
        Check("长度不足的十六进制不算", UpdateService.ShaFromNotes("sha256: abcdef") == "");
    }

    private static void TestTokenSanitize()
    {
        Section("GitHub 令牌清洗");
        Check("空串 → 没填", Text.SanitizeToken("") == null);
        Check("只有空白 → 没填", Text.SanitizeToken("  \n\t ") == null);
        Check("普通令牌原样", Text.SanitizeToken("ghp_abc123") == "ghp_abc123");
        Check("去掉粘贴带来的引号与换行", Text.SanitizeToken("  \"ghp_abc\"\n") == "ghp_abc");
        Check("去掉中间夹的换行", Text.SanitizeToken("ghp_a\nbc") == "ghp_abc");
        Check("兼容 用户名:令牌 老格式", Text.SanitizeToken("octocat:ghp_abc") == "ghp_abc");
        Check("冒号在末尾时不吞字符", Text.SanitizeToken("ghp_abc:") == "ghp_abc:");
        Check("细粒度令牌的下划线不被洗掉",
            Text.SanitizeToken("github_pat_11A_ bbb") == "github_pat_11A_bbb");
        Check("显示时打码，不暴露完整令牌",
            !Text.MaskToken("ghp_1234567890abcdef").Contains("7890abcd"));
    }

    /* ══════════════ 名单与配置 ══════════════ */

    /// <summary>
    /// 主题：两份调色板的 Key 必须**完全一致**，否则切主题时会有控件变成透明/黑。
    /// 这个错误类型很隐蔽 —— 编译能过，只有真的切一次那一个控件才露馅。
    /// </summary>
    private static void TestTheme()
    {
        Section("界面主题");

        // ① 字符串收敛
        Check("system / light / dark 原样保留",
            Theme.Normalize("system") == Theme.System &&
            Theme.Normalize("light") == Theme.Light &&
            Theme.Normalize("dark") == Theme.Dark);
        Check("大小写与空白都被吃掉", Theme.Normalize("  DARK ") == Theme.Dark);
        Check("空值与乱写一律当跟随系统",
            Theme.Normalize(null) == Theme.System &&
            Theme.Normalize("") == Theme.System &&
            Theme.Normalize("purple") == Theme.System);

        // ② 强制的意思就是"不看系统"
        Check("强制浅色不受系统影响", Theme.ResolveIsLight(Theme.Light));
        Check("强制深色不受系统影响", !Theme.ResolveIsLight(Theme.Dark));
        Check("跟随系统时等于系统当前的取值",
            Theme.ResolveIsLight(Theme.System) == Theme.SystemIsLight());

        // ③ 两份调色板的 Key 必须一一对应
        try
        {
            var dark = new System.Windows.ResourceDictionary
            { Source = new Uri("pack://application:,,,/Themes/Palette.Dark.xaml") };
            var light = new System.Windows.ResourceDictionary
            { Source = new Uri("pack://application:,,,/Themes/Palette.Light.xaml") };

            var dk = dark.Keys.Cast<object>().Select(k => k.ToString()!).OrderBy(x => x).ToList();
            var lk = light.Keys.Cast<object>().Select(k => k.ToString()!).OrderBy(x => x).ToList();

            Check("深色调色板不是空的", dk.Count > 20, $"{dk.Count} 个 Key");
            var missing = dk.Except(lk).ToList();
            var extra = lk.Except(dk).ToList();
            Check("浅色调色板补齐了深色的每个 Key", missing.Count == 0, string.Join(",", missing));
            Check("浅色调色板没有多余的 Key", extra.Count == 0, string.Join(",", extra));
        }
        catch (Exception ex)
        {
            Check("两份调色板能加载", false, ex.Message);
        }

        // ④ 配置里存的必须是收敛后的值
        var c = AppConfig.Default();
        Check("默认跟随系统", c.Appearance.Theme == Theme.System, c.Appearance.Theme);
        c.Appearance.Theme = "Nonsense";
        c.Normalize();
        Check("非法主题被收敛成 system", c.Appearance.Theme == Theme.System, c.Appearance.Theme);

        // ⑤ 窗口位置记忆（v1.7.6）：正常值原样保留，非法值收敛
        var place = AppConfig.Default();
        place.Appearance.WindowWidth = 1440;
        place.Appearance.WindowHeight = 900;
        place.Appearance.WindowLeft = -120;
        place.Appearance.WindowTop = 60;
        place.Appearance.LastTab = 3;
        place.Normalize();
        Check("窗口大小/位置原样保留",
            place.Appearance.WindowWidth == 1440 && place.Appearance.WindowHeight == 900
            && place.Appearance.WindowLeft == -120 && place.Appearance.WindowTop == 60,
            $"{place.Appearance.WindowWidth}×{place.Appearance.WindowHeight} @ {place.Appearance.WindowLeft},{place.Appearance.WindowTop}");
        Check("上次页签保留", place.Appearance.LastTab == 3, place.Appearance.LastTab.ToString());

        var badPlace = AppConfig.Default();
        badPlace.Appearance.WindowWidth = -5;
        badPlace.Appearance.WindowHeight = double.NaN;
        badPlace.Appearance.LastTab = 99;
        badPlace.Normalize();
        Check("非法窗口尺寸被收敛到 0",
            badPlace.Appearance.WindowWidth == 0 && badPlace.Appearance.WindowHeight == 0,
            $"{badPlace.Appearance.WindowWidth}×{badPlace.Appearance.WindowHeight}");
        Check("越界页签被收敛到 6", badPlace.Appearance.LastTab == 6, badPlace.Appearance.LastTab.ToString());
    }

    /// <summary>
    /// 「增强对自己也生效」：默认不排除自己，且旧配置里被写成 true 的那一项要纠正回来。
    ///
    /// <para>
    /// 这是用户实际踩到的问题：配置里的 pauseWhileSettingsOpen 是 true，
    /// 于是他在自己的设置窗口里滚，一点增强都没有 —— 看起来像"功能坏了"。
    /// </para>
    /// </summary>
    private static void TestSelfBoost()
    {
        Section("对自己也生效");

        var own = AppRuleName.Normalize(Environment.ProcessPath);

        var cfg = AppConfig.Default();
        var s = RuntimeSettings.From(cfg);

        Check("默认不在自己窗口里暂停", !cfg.Behavior.PauseWhileSettingsOpen);
        Check("默认不排除自己", !s.ShouldSkipSelf(own), $"own={s.OwnExe}");
        Check("自己不在默认黑名单里", s.IsAppEnabled(own, ""), own);

        // 开关打开时才跳过（这就是"试原生手感"的用途）
        var off = AppConfig.Default();
        off.Behavior.PauseWhileSettingsOpen = true;
        var so = RuntimeSettings.From(off);
        Check("打开暂停开关后跳过自己", so.ShouldSkipSelf(own));
        Check("暂停开关不影响别人", so.IsAppEnabled("chrome.exe", ""));

        // exe 名判定的边界：空、大小写、别的程序
        Check("空 exe 不算自己", !so.ShouldSkipSelf(""));
        Check("大小写不影响判定", so.ShouldSkipSelf(own.ToUpperInvariant()));
        Check("别的程序不算自己", !so.ShouldSkipSelf("notepad.exe"));

        /* ── v1 → v2 的迁移 ──
         * 老配置里这一项是 true（早期默认值），升级后必须被纠正成 false，
         * 否则用户升级完还是会觉得"对自己没效果"。 */
        var legacy = AppConfig.Default();
        legacy.SchemaVersion = 1;
        legacy.Behavior.PauseWhileSettingsOpen = true;
        legacy.Normalize();
        Check("v1 老配置里的自排除被纠正", !legacy.Behavior.PauseWhileSettingsOpen);
        // v1 的配置会一路迁移到当前版本（v2 自排除 + v3 平滑范围 + v4 滑行语义）
        Check("迁移后 schemaVersion 记为当前版本 5", legacy.SchemaVersion == 5, legacy.SchemaVersion.ToString());

        // 已经是 v2 之后，用户自己打开就不再被覆盖
        var kept = AppConfig.Default();
        kept.SchemaVersion = 2;
        kept.Behavior.PauseWhileSettingsOpen = true;
        kept.Normalize();
        Check("v2 之后用户手动打开的开关不再被覆盖", kept.Behavior.PauseWhileSettingsOpen);

        /* ── v2 → v3 的迁移 ──
         * 老配置的平滑范围是「仅加速时」：慢速滚动根本不经过本程序，
         * 用户实测"一格一格拨还是段落式"，所以默认改成「所有滚动」，
         * 升级时一次性纠正。 */
        var scope2 = AppConfig.Default();
        scope2.SchemaVersion = 2;
        scope2.Smooth.Scope = SmoothScope.BoostedOnly;
        scope2.Normalize();
        Check("v2 老配置的平滑范围被纠正为所有滚动", scope2.Smooth.Scope == SmoothScope.Always,
            scope2.Smooth.Scope.ToString());
        Check("迁移后 schemaVersion 记为 5", scope2.SchemaVersion == 5, scope2.SchemaVersion.ToString());

        // v3 之后用户自己选回「仅加速时」不再被覆盖
        var keptScope = AppConfig.Default();
        keptScope.SchemaVersion = 3;
        keptScope.Smooth.Scope = SmoothScope.BoostedOnly;
        keptScope.Normalize();
        Check("v3 之后用户选回仅加速时不再被覆盖", keptScope.Smooth.Scope == SmoothScope.BoostedOnly);

        /* ── v3 → v4 的迁移：滑行从「τ + 硬上限」改成「时长」──
         * 老值 110 是时间常数，直接当 110ms 用尾巴会短一半；
         * 但 0 是用户明确关掉了滑行，必须保留。 */
        var oldGlide = AppConfig.Default();
        oldGlide.SchemaVersion = 3;
        oldGlide.Smooth.GlideMs = 110;              // 旧配置里的 glideTauMs
        oldGlide.Normalize();
        Check("v3 老配置的滑行时长换算成新默认值", oldGlide.Smooth.GlideMs == 300,
            oldGlide.Smooth.GlideMs.ToString());
        Check("v4 迁移后 schemaVersion 记为 5", oldGlide.SchemaVersion == 5, oldGlide.SchemaVersion.ToString());

        var glideOff = AppConfig.Default();
        glideOff.SchemaVersion = 3;
        glideOff.Smooth.GlideMs = 0;                // 用户关掉了滑行
        glideOff.Normalize();
        Check("关掉滑行的老配置升级后仍然是关的", glideOff.Smooth.GlideMs == 0,
            glideOff.Smooth.GlideMs.ToString());

        /* ── v4 → v5 的迁移：像素台阶（"刻度感"）──
         * 帮助页把 δ 换算成像素时，1/2/3 帧/格 会踩 0.5 px 的像素台阶。
         * 一次性把 ≤3 的旧值抬到 4；用户自己调到 4~8 的一律保持原值
         * （用户靠 8 帧/格 的 0.25 px 亚像素步长来避开 0.5 px 台阶，不能覆盖）。 */
        var oldFrames = AppConfig.Default();
        oldFrames.SchemaVersion = 4;
        oldFrames.Smooth.FramesPerNotch = 8;
        oldFrames.Normalize();
        Check("v4 老配置的 8 帧/格 不被改动", oldFrames.Smooth.FramesPerNotch == 8,
            oldFrames.Smooth.FramesPerNotch.ToString());
        Check("v5 迁移后 schemaVersion 记为 5", oldFrames.SchemaVersion == 5,
            oldFrames.SchemaVersion.ToString());

        var oldFrames6 = AppConfig.Default();
        oldFrames6.SchemaVersion = 4;
        oldFrames6.Smooth.FramesPerNotch = 6;
        oldFrames6.Normalize();
        Check("v4 老配置的 6 帧/格 不被改动", oldFrames6.Smooth.FramesPerNotch == 6,
            oldFrames6.Smooth.FramesPerNotch.ToString());

        var oldFrames3 = AppConfig.Default();
        oldFrames3.SchemaVersion = 4;
        oldFrames3.Smooth.FramesPerNotch = 3;
        oldFrames3.Normalize();
        Check("v4 老配置的 3 帧/格 被抬到 4（消掉像素台阶）",
            oldFrames3.Smooth.FramesPerNotch == 4, oldFrames3.Smooth.FramesPerNotch.ToString());

        // v5 之后用户自己选回 8 帧/格 不再被覆盖
        var keptFrames = AppConfig.Default();
        keptFrames.SchemaVersion = 5;
        keptFrames.Smooth.FramesPerNotch = 8;
        keptFrames.Normalize();
        Check("v5 之后用户选回 8 帧/格 不再被覆盖", keptFrames.Smooth.FramesPerNotch == 8,
            keptFrames.Smooth.FramesPerNotch.ToString());
    }

    /// <summary>
    /// 设置页自己的页面（按像素滚动）的**边界判据**。
    ///
    /// <para>
    /// 这是 v1.7.2 修掉的 bug 的回归防线。以前判据是
    /// <c>Math.Abs(target - offset) &lt; 0.5</c> —— "位移不到半像素"被当成了
    /// "到边界了"，于是摊帧的尾帧（delta=1~3 单位，0.17~0.5 px）全部被放行，
    /// 而 WPF 的 ScrollViewer 拿到一个事件就滚一整行（16 px 起步）。
    /// 我们摊出去 17 px，它一跳几十 px，一格直接顶到一页多 ——
    /// 用户看到的就是"设置页里一滚动就是一页"。
    /// </para>
    /// <para>
    /// 实测记录（<c>--wheeltest lines=1</c>，视口 741 px）：
    /// 修复前单格 784.5 px，修复后 20 px（= 1 行 × 20 px/行，精确）。
    /// </para>
    /// </summary>
    private static void TestPlanScroll()
    {
        Section("设置页像素滚动：边界判据");

        /* 核心：小数位移也是位移，ScrollViewer 的 VerticalOffset 是 double。 */
        Check("0.33 px 的小数位移照常滚（不是边界）",
            App.PlanScroll(2214, 0.33, 5949) is double t1 && Math.Abs(t1 - 2213.67) < 1e-9,
            $"得到 {App.PlanScroll(2214, 0.33, 5949):0.####}");
        Check("摊帧尾帧那种 0.17 px 也滚",
            App.PlanScroll(1973.5, 0.17, 5949) is double t2 && Math.Abs(t2 - 1973.33) < 1e-9,
            $"得到 {App.PlanScroll(1973.5, 0.17, 5949):0.####}");
        Check("一个整像素也滚", App.PlanScroll(1000, 1, 5949) == 999);

        /* 只有"夹过之后没动"才放行。 */
        Check("到顶了才放行", App.PlanScroll(0, 20, 5949) == null);
        Check("到底了才放行", App.PlanScroll(5949, -20, 5949) == null);
        Check("内容比视口还短时放行（ScrollableHeight=0）", App.PlanScroll(0, 20, 0) == null);
        Check("负的 ScrollableHeight 也放行", App.PlanScroll(0, 20, -5) == null);

        /* 正常路径。 */
        Check("正常往下滚 20 px", App.PlanScroll(100, -20, 5949) == 120);
        Check("正常往上滚 20 px", App.PlanScroll(100, 20, 5949) == 80);
        Check("跨过底部时被夹住（不越界）", App.PlanScroll(5940, -20, 5949) == 5949);
        Check("跨过顶部时被夹住（不越界）", App.PlanScroll(10, 20, 5949) == 0);

        /* 位移为 0（理论上不会发生，但判据必须自洽）。 */
        Check("位移为 0 时放行（夹过之后确实没动）", App.PlanScroll(100, 0, 5949) == null);
    }

    private static void TestFilter()
    {
        Section("黑名单 / 白名单");
        var cfg = AppConfig.Default();
        cfg.Filter.Mode = FilterMode.Blacklist;
        cfg.Filter.Apps.Clear();
        cfg.Filter.Apps.Add(new AppRule { Exe = "game.exe" });
        var s = RuntimeSettings.From(cfg);

        Check("黑名单：名单里不增强", !s.IsAppEnabled("game.exe", "标题"));
        Check("黑名单：名单外增强", s.IsAppEnabled("notepad.exe", "标题"));
        Check("大小写不敏感（已归一）", !s.IsAppEnabled("GAME.EXE".ToLowerInvariant(), ""));
        Check("解析失败（空 exe）时放行，不误伤", s.IsAppEnabled("", ""));

        var wl = AppConfig.Default();
        wl.Filter.Mode = FilterMode.Whitelist;
        wl.Filter.Apps.Clear();
        wl.Filter.Apps.Add(new AppRule { Exe = "code.exe" });
        var ws = RuntimeSettings.From(wl);
        Check("白名单：名单里增强", ws.IsAppEnabled("code.exe", ""));
        Check("白名单：名单外不增强", !ws.IsAppEnabled("notepad.exe", ""));

        // 标题正则
        var rx = AppConfig.Default();
        rx.Filter.Apps.Clear();
        rx.Filter.TitleRegex = "远程|remote";
        var rs = RuntimeSettings.From(rx);
        Check("标题正则命中 → 不增强", !rs.IsAppEnabled("foo.exe", "远程桌面连接"));
        Check("标题正则不命中 → 增强", rs.IsAppEnabled("foo.exe", "记事本"));

        // 写坏的正则不该让程序崩，只是失效
        var bad = AppConfig.Default();
        bad.Filter.TitleRegex = "([";
        var bs = RuntimeSettings.From(bad);
        Check("非法正则被安全忽略", bs.TitleRegex == null && bs.IsAppEnabled("foo.exe", "x"));

        Check("exe 名归一：去路径、补扩展名、转小写",
            AppRuleName.Normalize(@"C:\Program Files\App\Foo.EXE") == "foo.exe");
        Check("exe 名归一：没扩展名也补上", AppRuleName.Normalize("Foo") == "foo.exe");
    }

    private static void TestRepoNormalize()
    {
        Section("仓库地址");
        Check("默认仓库正确", AppConfig.Default().Update.Repo == "QQQOrange/Mouse-wheel-enhancement");
        Check("去掉网址前缀", AppConfig.NormalizeRepo("https://github.com/a/b") == "a/b");
        Check("去掉 .git 后缀", AppConfig.NormalizeRepo("a/b.git") == "a/b");
        Check("去掉多余斜杠与空格", AppConfig.NormalizeRepo("  a/b/  ") == "a/b");
        Check("填坏了退回默认", AppConfig.NormalizeRepo("") == "QQQOrange/Mouse-wheel-enhancement");
        var u = new UpdateService(new ConfigStore(Path.Combine(Path.GetTempPath(), "mwe-selftest-cfg")));
        Check("API 地址里带仓库名",
            u.LatestApi.Contains("repos/QQQOrange/Mouse-wheel-enhancement/releases/latest"), u.LatestApi);
        Check("网页地址正确",
            u.LatestWeb == "https://github.com/QQQOrange/Mouse-wheel-enhancement/releases/latest", u.LatestWeb);
    }

    private static void TestConfigRoundTrip()
    {
        Section("配置读写");
        var tmp = Path.Combine(Path.GetTempPath(), "mwe-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var store = new ConfigStore(tmp);
            var c = store.Config;

            // 默认值
            Near("默认触发阈值", c.Boost.FastThresholdPerSec, 10);
            Near("默认最大倍率", c.Boost.MaxMultiplier, 3.0);
            Check("默认开启平滑", c.Smooth.Enabled);
            Check("默认平滑所有滚动（单格滚动也要有平滑过程）", c.Smooth.Scope == SmoothScope.Always);
            Check("默认黑名单模式", c.Filter.Mode == FilterMode.Blacklist);
            Check("默认名单里有远程桌面", c.Filter.Apps.Any(a => a.Exe == "mstsc.exe"));

            // 改 → 存 → 读
            c.Boost.MaxMultiplier = 5;
            c.Filter.Apps.Add(new AppRule { Exe = "Test.EXE", Note = "x" });
            store.SaveNow();

            var store2 = new ConfigStore(tmp);
            Near("倍率持久化", store2.Config.Boost.MaxMultiplier, 5);
            Check("名单持久化且已归一",
                store2.Config.Filter.Apps.Any(a => a.Exe == "test.exe"), "应存在 test.exe");
            store2.Dispose();
            store.Dispose();

            // 小数必须被抹平成整格（用户口径：格数不要小数点）
            var c3 = AppConfig.Default();
            c3.Boost.FastThresholdPerSec = 7.6;
            c3.Boost.SaturationPerSec = 33.4;
            c3.Boost.MaxMultiplier = 4.5;
            c3.Smooth.TailTauMs = 47.3;
            c3.Normalize();
            Check("阈值取整（7.6 → 8）", c3.Boost.FastThresholdPerSec == 8, c3.Boost.FastThresholdPerSec.ToString("0.##"));
            Check("满速点取整（33.4 → 33）", c3.Boost.SaturationPerSec == 33, c3.Boost.SaturationPerSec.ToString("0.##"));
            Check("倍率取整（4.5 → 5，四舍五入不是银行家舍入）",
                c3.Boost.MaxMultiplier == 5, c3.Boost.MaxMultiplier.ToString("0.##"));
            Check("惯性取整（47.3 → 47）", c3.Smooth.TailTauMs == 47, c3.Smooth.TailTauMs.ToString("0.##"));
            Check("默认配置本身就全是整数",
                IsWhole(AppConfig.Default().Boost.FastThresholdPerSec) &&
                IsWhole(AppConfig.Default().Boost.SaturationPerSec) &&
                IsWhole(AppConfig.Default().Boost.MaxMultiplier) &&
                IsWhole(AppConfig.Default().Smooth.TailTauMs) &&
                IsWhole(AppConfig.Default().Smooth.GlideMs) &&
                IsWhole(AppConfig.Default().Smooth.MaxBacklogNotches));
            Check("满速点至少比阈值高 5 格",
                c3.Boost.SaturationPerSec >= c3.Boost.FastThresholdPerSec + 5,
                $"{c3.Boost.SaturationPerSec} vs {c3.Boost.FastThresholdPerSec}");

            /* 「以管理员身份运行」不再从属于「开机自启」。
             *
             * 旧版本会在自启关掉时把这个开关强制拉平，于是用户关一次开机自启
             * 就悄悄丢掉了管理员模式 —— 下次打开又看到"当前不是管理员身份"。
             * 现在两者独立：可以只提权、不开机自启。 */
            var ca = AppConfig.Default();
            ca.Behavior.RunAtLogin = false;
            ca.Behavior.RunAsAdmin = true;
            ca.Normalize();
            Check("自启关掉时，管理员运行设置保留", ca.Behavior.RunAsAdmin,
                ca.Behavior.RunAsAdmin.ToString());

            var cb2 = AppConfig.Default();
            cb2.Behavior.RunAtLogin = true;
            cb2.Behavior.RunAsAdmin = true;
            cb2.Normalize();
            Check("自启开着时，管理员运行设置保留", cb2.Behavior.RunAsAdmin);
            Check("默认不自启、也不以管理员运行",
                !AppConfig.Default().Behavior.RunAtLogin &&
                !AppConfig.Default().Behavior.RunAsAdmin);

            // 越界值必须被夹回来
            var c2 = AppConfig.Default();
            c2.Boost.MaxMultiplier = 999;
            c2.Boost.FastThresholdPerSec = -5;
            c2.Smooth.TargetFps = 5000;
            c2.Smooth.FramesPerNotch = 7;
            c2.Normalize();
            Near("倍率上限夹到 8.0", c2.Boost.MaxMultiplier, 8.0);
            Near("阈值下限夹到 2", c2.Boost.FastThresholdPerSec, 2);
            Check("帧率夹到 240", c2.Smooth.TargetFps == 240, c2.Smooth.TargetFps.ToString());
            Check("每格帧数取 120 的约数", 120 % c2.Smooth.FramesPerNotch == 0,
                c2.Smooth.FramesPerNotch.ToString());

            // 损坏的配置要被备份而不是覆盖
            File.WriteAllText(Path.Combine(tmp, "config.json"), "{ 这不是 json ");
            var store3 = new ConfigStore(tmp);
            Check("损坏配置能被识别并给出提示", store3.LoadWarning != null);
            Check("损坏文件被保留成备份",
                Directory.GetFiles(tmp, "config.corrupt-*.json").Length == 1);
            store3.Dispose();

            // 老配置里的小数应该在**加载时**被抹平并回写，而不是留着让界面显示 10、文件里写 9.9273
            File.WriteAllText(Path.Combine(tmp, "config.json"),
                "{\"boost\":{\"fastThresholdPerSec\":9.927287581699336,\"saturationPerSec\":25.58823529411775,\"maxMultiplier\":6.5849673202614145}}");
            var store5 = new ConfigStore(tmp);
            Check("加载时把小数抹平", store5.Config.Boost.FastThresholdPerSec == 10 &&
                                      store5.Config.Boost.MaxMultiplier == 7,
                $"{store5.Config.Boost.FastThresholdPerSec} / {store5.Config.Boost.MaxMultiplier}");
            var written = File.ReadAllText(Path.Combine(tmp, "config.json"));
            Check("抹平后的值被写回文件", written.Contains("\"fastThresholdPerSec\": 10") ||
                                          written.Contains("\"fastThresholdPerSec\":10"),
                written.Length > 120 ? written[..120] : written);
            store5.Dispose();

            // 空 JSON 对象也要能读（字段缺失走默认值）
            File.WriteAllText(Path.Combine(tmp, "config.json"), "{}");
            var store4 = new ConfigStore(tmp);
            Near("缺字段时用默认值", store4.Config.Boost.MaxMultiplier, 3.0);
            Check("配置对象不为 null", store4.Config.Filter.Apps != null);
            store4.Dispose();
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    /// <summary>
    /// 「稳定入口」的解析优先级。
    ///
    /// <para>
    /// 这条逻辑直接决定开机自启会不会失效：计划任务必须指向启动器（稳定位置），
    /// 而不是 %LOCALAPPDATA%\...\app\&lt;版本&gt;\ 里的主程序本体。
    /// 优先级错了，用户看到的现象就是"开关显示着开，开机却什么都没起来"。
    /// </para>
    /// </summary>
    private static void TestStableEntry()
    {
        Section("开机自启：稳定入口");

        var envName = Core.AdminStart.LauncherPathEnv;
        var saved = Environment.GetEnvironmentVariable(envName);
        var savedProp = Core.AdminStart.LauncherPath;

        try
        {
            // 1) 环境变量优先（启动器这次拉起我们时给的）
            var fake = Path.Combine(Path.GetTempPath(), "mwe-fake-launcher-" + Guid.NewGuid().ToString("N") + ".exe");
            File.WriteAllText(fake, "x");
            try
            {
                Environment.SetEnvironmentVariable(envName, fake);
                Check("环境变量优先于其他来源",
                    Core.AdminStart.StableEntryPath == fake,
                    Core.AdminStart.StableEntryPath);

                // 2) 环境变量指向的文件不存在 → 不能用它，退回下一个来源
                var ghost = Path.Combine(Path.GetTempPath(), "mwe-not-exist-" + Guid.NewGuid().ToString("N") + ".exe");
                Environment.SetEnvironmentVariable(envName, ghost);
                var fallback = Core.AdminStart.StableEntryPath;
                Check("环境变量指向不存在的文件时不采用",
                    !string.Equals(fallback, ghost, StringComparison.OrdinalIgnoreCase) && File.Exists(fallback),
                    fallback);
            }
            finally { File.Delete(fake); }

            // 3) 都没有时退回当前 exe，绝不能是空
            Environment.SetEnvironmentVariable(envName, null);
            Core.AdminStart.LauncherPath = null;
            var self = Core.AdminStart.StableEntryPath;
            Check("没有启动器信息时退回当前程序本体", !string.IsNullOrWhiteSpace(self), self);
            Check("退回的目标必须真实存在", File.Exists(self), self);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, saved);
            Core.AdminStart.LauncherPath = savedProp;
        }

        // 4) 开机自启命令行必须指向稳定入口，而不是当前这个版本目录里的本体
        var cmd = Core.AutoStart.CommandLine;
        Check("注册表自启命令行指向稳定入口",
            cmd.Contains(Core.AdminStart.StableEntryPath, StringComparison.OrdinalIgnoreCase), cmd);
        Check("注册表自启命令行带 --minimized", cmd.Contains("--minimized"), cmd);
    }

    /// <summary>
    /// 两条"路径判据"的回归断言。
    ///
    /// <para>
    /// 它们各自对应一次真实故障：
    /// · <c>InstallRootOf</c> 错位 → 数据落进版本目录，升级时被连带删除（设置"凭空消失"）；
    /// · <c>IsVersionedBody</c> 缺失 → 自愈把开机自启写进版本目录，下次升级静默失效。
    /// </para>
    /// </summary>
    private static void TestLayoutJudgement()
    {
        Section("布局判据：版本目录");

        // ── 数据目录：<安装目录>/app/<版本>/ 要能认到安装目录 ──
        var root = Path.Combine(Path.GetTempPath(), "mwe-layout-probe");
        var verDir = Path.Combine(root, "app", "1.5.3");
        Check("版本目录 → 认得上一级安装目录",
            string.Equals(Core.AppPaths.InstallRootOf(verDir), root, StringComparison.OrdinalIgnoreCase),
            Core.AppPaths.InstallRootOf(verDir) ?? "(null)");
        Check("尾随分隔符不影响判断",
            string.Equals(Core.AppPaths.InstallRootOf(verDir + Path.DirectorySeparatorChar), root,
                StringComparison.OrdinalIgnoreCase),
            Core.AppPaths.InstallRootOf(verDir + Path.DirectorySeparatorChar) ?? "(null)");
        Check("普通目录不算版本布局",
            Core.AppPaths.InstallRootOf(root) == null,
            Core.AppPaths.InstallRootOf(root) ?? "(null)");
        Check("app 下的非版本号子目录不算",
            Core.AppPaths.InstallRootOf(Path.Combine(root, "app", "bin")) == null,
            Core.AppPaths.InstallRootOf(Path.Combine(root, "app", "bin")) ?? "(null)");

        // ── 开机自启：绝不能指向版本目录里的主程序本体 ──
        var bodyExe = Path.Combine(verDir, "MouseWheelEnhancement.App.exe");
        Check("版本目录里的主程序被识别出来", Core.AdminStart.IsVersionedBody(bodyExe), bodyExe);
        Check("启动器本身不算（它在安装目录）",
            !Core.AdminStart.IsVersionedBody(Path.Combine(root, "MouseWheelEnhancement.exe")),
            Path.Combine(root, "MouseWheelEnhancement.exe"));
        Check("空路径不算", !Core.AdminStart.IsVersionedBody(null));
    }

    /// <summary>
    /// 「以管理员身份运行」—— 提权判定 + 旧配置兼容。
    ///
    /// <para>
    /// 这条判定的错误表现是"用户明明开了开关，界面还说不是管理员"，
    /// 触发条件分散在"是否已是管理员 / 是否开了开关 / 是不是第二轮"三者之间，
    /// 靠手点很难穷举，所以把真值表钉死。
    /// </para>
    /// </summary>
    private static void TestAdminMode()
    {
        Section("以管理员身份运行");

        Check("开了开关、不是管理员、没试过 → 提权",
            Core.AdminStart.ShouldElevate(false, true, false));
        Check("已经是管理员 → 不再提权",
            !Core.AdminStart.ShouldElevate(true, true, false));
        Check("开关没开 → 不提权",
            !Core.AdminStart.ShouldElevate(false, false, false));
        Check("这一轮已经试过 → 不再提权（防 UAC 死循环）",
            !Core.AdminStart.ShouldElevate(false, true, true));

        /* 旧配置兼容：v1.5.4 及更早写的是 runAtLoginAsAdmin。
         * 键名改了的话，用户已有的设置会在升级时被静默重置成 false ——
         * 表现就是"升级完又变回普通权限"。 */
        var dir = Path.Combine(Path.GetTempPath(), "mwe-admin-cfg-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "config.json"),
                "{\"schemaVersion\":3,\"behavior\":{\"runAtLogin\":false,\"runAtLoginAsAdmin\":true}}",
                new UTF8Encoding(false));

            using var store = new ConfigStore(dir);
            Check("旧配置的 runAtLoginAsAdmin 仍能读入",
                store.Config.Behavior.RunAsAdmin, store.Config.Behavior.RunAsAdmin.ToString());
            Check("旧配置不会被误当成「开机自启」",
                !store.Config.Behavior.RunAtLogin, store.Config.Behavior.RunAtLogin.ToString());
        }
        catch (Exception ex)
        {
            Check("旧配置兼容性检查没有抛异常", false, ex.Message);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* 测试目录清不掉无所谓 */ }
        }
    }
}
